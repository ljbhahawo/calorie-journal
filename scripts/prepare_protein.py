import csv
import hashlib
import json
import zipfile
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path


root = Path(__file__).resolve().parents[1]
raw = root / "workspace/usda-sr/FoodData_Central_sr_legacy_food_csv_2018-04"
target = root / "data/lookup/protein.json"


def read_csv(name):
    with (raw / name).open(encoding="utf-8-sig", newline="") as handle:
        return list(csv.DictReader(handle))


def entry(source_id, text):
    text = (text or "").strip()
    result = {"SourceId": source_id, "OriginalAmount": text, "ProteinPer100gHundredths": None}
    if text:
        value = Decimal(text)
        if not value.is_finite() or not 0 <= value <= 100:
            raise ValueError("官方蛋白质范围无效：" + source_id)
        result["ProteinPer100gHundredths"] = int((value * 100).quantize(Decimal("1"), rounding=ROUND_HALF_UP))
    return result


definition = next(row for row in read_csv("nutrient.csv") if row["id"] == "1003")
if definition["name"] != "Protein" or definition["unit_name"] != "G":
    raise ValueError("USDA蛋白质字段定义不符")
usda_values = {}
for row in read_csv("food_nutrient.csv"):
    if row["nutrient_id"] == "1003":
        if row["fdc_id"] in usda_values:
            raise ValueError("USDA蛋白质来源编号重复")
        usda_values[row["fdc_id"]] = row["amount"]
tfda_archive = root / "workspace/food-lookup-research/tfda-nutrition.zip"
with zipfile.ZipFile(tfda_archive) as archive:
    with archive.open("20_5.json") as handle:
        records = json.load(handle)
tfda_values = {}
for row in records:
    if row["分析項"] == "粗蛋白":
        if row["含量單位"].strip() != "g" or row["整合編號"] in tfda_values:
            raise ValueError("TFDA蛋白质单位或来源编号无效")
        tfda_values[row["整合編號"]] = row["每100克含量"]
catalogs = []
for filename, archive, field, values in [
    ("usda.json", root / "workspace/downloads/FoodData_Central_sr_legacy_food_csv_2018-04.zip", "1003 Protein/G", usda_values),
    ("tfda.json", tfda_archive, "粗蛋白/g", tfda_values),
]:
    path = root / "data/lookup" / filename
    catalog = json.loads(path.read_text(encoding="utf-8"))
    archive_sha = hashlib.sha256(archive.read_bytes()).hexdigest().upper()
    if archive_sha != catalog["ArchiveSha256"].upper():
        raise ValueError("官方原档与热量目录来源哈希不一致")
    foods = [entry(food["SourceId"], values.get(food["SourceId"])) for food in catalog["Foods"]]
    catalogs.append({"Provider": catalog["Provider"], "NutritionVersion": catalog["Version"],
                     "ArchiveSha256": archive_sha, "CatalogSha256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
                     "Analysis": field, "Unit": "g/100g", "Foods": foods})
    print(filename, "known", sum(food["ProteinPer100gHundredths"] is not None for food in foods), "missing", sum(food["ProteinPer100gHundredths"] is None for food in foods))
target.write_text(json.dumps({"FormatVersion": 1, "Version": "protein-2026-10-23.1", "Catalogs": catalogs}, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
print("protein.json SHA256", hashlib.sha256(target.read_bytes()).hexdigest().upper())
