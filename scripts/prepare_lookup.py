import csv
import hashlib
import json
import zipfile
from decimal import Decimal, ROUND_HALF_UP
from datetime import datetime, timezone
from pathlib import Path


root = Path(__file__).resolve().parents[1]
raw = root / "workspace/usda-sr/FoodData_Central_sr_legacy_food_csv_2018-04"
target = root / "data/lookup"
target.mkdir(parents=True, exist_ok=True)


def rows(name):
    with (raw / name).open(encoding="utf-8-sig", newline="") as handle:
        yield from csv.DictReader(handle)


def scaled(text):
    value = Decimal(text.strip()) * 100
    if not value.is_finite() or not 0 <= value <= 1000000:
        raise ValueError("官方热量值的精度或范围无效")
    return int(value.quantize(Decimal("1"), rounding=ROUND_HALF_UP))


def write_catalog(filename, catalog):
    foods = catalog["Foods"]
    if not foods or len({food["SourceId"] for food in foods}) != len(foods):
        raise ValueError("参考目录为空或来源ID重复")
    foods.sort(key=lambda food: food["SourceId"])
    (target / filename).write_text(json.dumps(catalog, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(filename, len(foods))


energy_definition = next(row for row in rows("nutrient.csv") if row["id"] == "1008")
if energy_definition["unit_name"] != "KCAL" or energy_definition["name"] != "Energy":
    raise ValueError("USDA能量字段不符")
energies = {}
for row in rows("food_nutrient.csv"):
    if row["nutrient_id"] == "1008":
        if row["fdc_id"] in energies:
            raise ValueError("USDA能量重复")
        if row["amount"].strip():
            energies[row["fdc_id"]] = (scaled(row["amount"]), row["amount"].strip())
known = {food["SourceId"]: food for food in json.loads((root / "data/builtin/foods.json").read_text(encoding="utf-8"))["Foods"]}
categories = {row["id"]: row["description"] for row in rows("food_category.csv")}
usda = []
for row in rows("food.csv"):
    if row["fdc_id"] not in energies:
        continue
    translation = known.get(row["fdc_id"], {})
    usda.append({"SourceId": row["fdc_id"], "Name": translation.get("Name", row["description"]),
                 "OriginalName": row["description"], "Aliases": translation.get("Aliases", ""),
                 "Preparation": translation.get("Preparation", row["description"]),
                 "Category": translation.get("Category", categories[row["food_category_id"]]),
                 "KcalPer100gHundredths": energies[row["fdc_id"]][0],
                 "SourceNote": "能量字段1008；每100克可食部分；原始值 " + energies[row["fdc_id"]][1] + " kcal（显示值四舍五入至两位）；USDA SR Legacy 2018-04；CC0"})
usda_archive = root / "workspace/downloads/FoodData_Central_sr_legacy_food_csv_2018-04.zip"
usda_sha = hashlib.sha256(usda_archive.read_bytes()).hexdigest()
if usda_sha != json.loads((root / "data/builtin/sources.json").read_text(encoding="utf-8"))["archiveSha256"]:
    raise ValueError("USDA档案与已核对的来源清单不一致")
write_catalog("usda.json", {"FormatVersion": 1, "Provider": "USDA SR Legacy", "Version": "2018-04",
              "RetrievedAtUtc": datetime.fromtimestamp(usda_archive.stat().st_mtime, timezone.utc).isoformat(), "SourceUrl": "https://fdc.nal.usda.gov/download-datasets/",
              "LicenseUrl": "https://creativecommons.org/publicdomain/zero/1.0/",
              "ArchiveSha256": usda_sha, "Foods": usda})

tfda_archive = root / "workspace/food-lookup-research/tfda-nutrition.zip"
with zipfile.ZipFile(tfda_archive) as archive:
    with archive.open("20_5.json") as handle:
        records = json.load(handle)
tfda = []
for row in records:
    if row["分析項"] != "熱量":
        continue
    if row["含量單位"].strip().lower() != "kcal" or not row["每100克含量"].strip():
        raise ValueError("TFDA热量缺失或单位不符")
    tfda.append({"SourceId": row["整合編號"], "Name": row["樣品名稱"],
                 "OriginalName": row["樣品名稱"], "Aliases": (row["俗名"] or "") + " " + (row["樣品英文名稱"] or ""),
                 "Preparation": row["內容物描述"] or "", "Category": row["食品分類"],
                 "KcalPer100gHundredths": scaled(row["每100克含量"]),
                 "SourceNote": "衛生福利部食品藥物管理署，食品營養成分資料集；分析項：熱量；每100克可食部分；政府資料開放授權條款第1版 https://data.gov.tw/license"})
write_catalog("tfda.json", {"FormatVersion": 1, "Provider": "TFDA", "Version": "获取2026-10-22",
              "RetrievedAtUtc": datetime.fromtimestamp(tfda_archive.stat().st_mtime, timezone.utc).isoformat(), "SourceUrl": "https://data.gov.tw/dataset/8543",
              "LicenseUrl": "https://data.gov.tw/license",
              "ArchiveSha256": hashlib.sha256(tfda_archive.read_bytes()).hexdigest(), "Foods": tfda})

# 测试样本直接取官方公开行，包含普通热量、修正热量及其他营养素；不改写原值。
sample_ids = {row["整合編號"] for row in records if row["分析項"] == "熱量" and row["樣品名稱"] in ("雞蛋", "香蕉", "白飯", "食鹽")}
if not sample_ids:
    raise ValueError("官方测试样本未找到")
sample = [row for row in records if row["整合編號"] in sample_ids]
fixture = root / "tests/CalorieJournal.Tests/fixtures/tfda-sample.zip"
fixture.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(fixture, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    info = zipfile.ZipInfo("tfda-sample.json", (2026, 10, 22, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    archive.writestr(info, json.dumps(sample, ensure_ascii=False).encode("utf-8"))
