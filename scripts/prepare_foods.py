import argparse
import csv
import hashlib
import json
from datetime import datetime, timezone
from decimal import Decimal
from pathlib import Path
from uuid import NAMESPACE_URL, uuid5


project_root = Path(__file__).resolve().parents[1]
raw_root = project_root / "workspace/usda-sr/FoodData_Central_sr_legacy_food_csv_2018-04"


def rows(name):
    with (raw_root / name).open(encoding="utf-8-sig", newline="") as handle:
        yield from csv.DictReader(handle)


parser = argparse.ArgumentParser()
parser.add_argument("--inspect", nargs="*")
arguments = parser.parse_args()
foods = {row["fdc_id"]: row for row in rows("food.csv")}
ndb = {row["NDB_number"].zfill(5): row["fdc_id"] for row in rows("sr_legacy_food.csv")}
energy_definition = next(row for row in rows("nutrient.csv") if row["id"] == "1008")
if energy_definition["unit_name"] != "KCAL" or energy_definition["name"] != "Energy":
    raise ValueError("USDA能量字段与预期不符")
energies = {}
for row in rows("food_nutrient.csv"):
    if row["nutrient_id"] == "1008":
        if row["fdc_id"] in energies:
            raise ValueError("USDA同一食物存在重复能量记录")
        energies[row["fdc_id"]] = Decimal(row["amount"])

if arguments.inspect is not None:
    for key in arguments.inspect:
        source_id = ndb.get(key.zfill(5))
        if source_id is None:
            print(key, "NOT FOUND")
        else:
            print(key, source_id, foods[source_id]["description"], str(energies.get(source_id)))
else:
    selections = json.loads((project_root / "data/food-selections.json").read_text(encoding="utf-8"))
    output = []
    manifest_rows = []
    stamp = "2026-10-21T18:10:06+00:00"
    for selection in selections:
        source_id = ndb[selection["ndb"]]
        source = foods[source_id]
        if source["description"] != selection["originalName"]:
            raise ValueError(f"原名不一致：{selection['ndb']}")
        value = energies[source_id]
        scaled = value * 100
        if scaled != scaled.to_integral_value() or value < 0 or value > 10000:
            raise ValueError("能量精度或范围无效")
        stable_key = "usda-sr:" + source_id
        output.append({
            "Id": str(uuid5(NAMESPACE_URL, stable_key)), "Name": selection["name"],
            "OriginalName": source["description"], "Aliases": selection.get("aliases", ""),
            "Preparation": selection["preparation"], "Category": selection["category"],
            "KcalPer100gHundredths": int(scaled), "SourceKind": "USDA SR Legacy",
            "SourceId": source_id, "SourceVersion": "2018-04",
            "SourceUrl": f"https://fdc.nal.usda.gov/food-details/{source_id}/nutrients",
            "SourceNote": "NDB " + selection["ndb"] + "；能量字段1008；每100克可食部分",
            "BuiltinKey": stable_key, "UserModified": False, "IsDeleted": False,
            "CreatedAtUtc": stamp, "UpdatedAtUtc": stamp,
        })
        manifest_rows.append({"ndb": selection["ndb"], "fdcId": source_id, "name": selection["name"],
                              "originalName": source["description"], "kcalPer100g": str(value)})
    if len(output) < 50 or len({item["Id"] for item in output}) != len(output):
        raise ValueError("至少50项且不能重复")
    target = project_root / "data/builtin"
    target.mkdir(parents=True, exist_ok=True)
    (target / "foods.json").write_text(json.dumps({"Version": "usda-sr-2018-04.zh.1", "Foods": output}, ensure_ascii=False, indent=2), encoding="utf-8")
    archive = project_root / "workspace/downloads/FoodData_Central_sr_legacy_food_csv_2018-04.zip"
    manifest = {"source": "https://fdc.nal.usda.gov/fdc-datasets/FoodData_Central_sr_legacy_food_csv_2018-04.zip",
                "license": "CC0-1.0", "archiveSha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                "retrievedAtUtc": datetime.now(timezone.utc).isoformat(), "energyNutrientId": "1008",
                "basis": "kcal per 100g edible portion", "count": len(output), "foods": manifest_rows}
    (target / "sources.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"已生成{len(output)}项，单位和数值均从USDA档案逐项读取并核对原名。")
