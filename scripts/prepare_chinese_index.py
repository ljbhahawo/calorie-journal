import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path


root = Path(__file__).resolve().parents[1]
generic_labels = {"鱼类", "甲壳类", "贝类软体动物", "豆类", "坚果", "种子", "饮料", "婴儿辅食", "香辛料", "野味肉", "快餐", "餐厅食品", "学校餐食", "谷物食品", "蔬菜", "水果"}


def read(path):
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"词表含重复键：{key}")
        result[key] = value
    return result


def matcher(translations):
    # 较长食材及否定状态先匹配，防止unsweetened同时被认成sweetened。
    keys = sorted(translations, key=lambda key: (-len(key), key))
    pattern = re.compile(r"(?<![a-z])(?:" + "|".join(map(re.escape, keys)) + r")(?![a-z])", re.IGNORECASE)
    return lambda text: list(dict.fromkeys(translations[match.group().lower()] for match in pattern.finditer(text)))


def main():
    parser = argparse.ArgumentParser(description="根据实际USDA原名及版本化中文词表生成检索索引与覆盖审计")
    parser.add_argument("--audit-only", action="store_true")
    args = parser.parse_args()
    source = root / "data/lookup/usda.json"
    catalog = read(source)
    terms = read(root / "data/search/usda-terms.json")
    ingredient_matcher = matcher(terms["FoodTerms"])
    state_matcher = matcher(terms["StateTerms"])
    known = {food["SourceId"]: food for food in read(root / "data/builtin/foods.json")["Foods"]}
    foods = []
    uncovered = []
    audit = []
    for food in catalog["Foods"]:
        original = food["OriginalName"]
        ingredients = ingredient_matcher(original)
        states = state_matcher(original)
        head = original.split(",")[0].strip()
        base = ingredient_matcher(head)
        parts = [part.strip().lower() for part in original.split(",")]
        specific = []
        if head.lower() == "wild rice":
            states = [value for value in states if value != "野生"]
        if parts[0] in ("beef", "beef composite", "veal", "pork", "lamb"):
            ingredients = ["后腿" if value == "圆形" else value for value in ingredients]
        if re.search(r"\bcheese\b", original, re.IGNORECASE):
            if "swiss" in parts:
                specific.append("瑞士奶酪")
            if "blue" in parts:
                specific.append("蓝纹奶酪")
        if "餐食" in ingredients and food["Category"] in ("Cereal Grains and Pasta", "Nut and Seed Products", "Legumes and Legume Products"):
            ingredients = ["粗粉" if value == "餐食" else value for value in ingredients]
            base = ["粗粉" if value == "餐食" else value for value in base]
        if re.search(r"\bprotein\b", original, re.IGNORECASE) and re.search(r"\bpowder\b", original, re.IGNORECASE):
            specific.append("蛋白粉")
        if parts[0] in ("chicken", "chicken breast") and re.search(r"\bbreast\b", original, re.IGNORECASE):
            specific.append("鸡胸肉")
        if parts[0] == "fish" and len(parts) > 2 and parts[1] in ("salmon", "cod") and "atlantic" in parts:
            specific.append("大西洋鲑" if parts[1] == "salmon" else "大西洋鳕")
        if parts[0] == "chicken" and re.search(r"\b(thigh|leg|drumstick)\b", original, re.IGNORECASE):
            specific.append("鸡腿肉")
        if parts[0] == "chicken" and re.search(r"\bwings?\b", original, re.IGNORECASE):
            specific.append("鸡翅")
        if parts[0] in ("rice", "rice flour") and "brown" in parts:
            specific.append("糙米" if parts[0] == "rice" else "糙米粉")
        if parts[0] == "beans" and "kidney" in parts:
            specific.append("腰豆" if "red" not in parts else "红腰豆")
            ingredients = [value for value in ingredients if value != "肾脏"]
        if parts[0] == "beans" and "snap" in parts:
            specific.append("四季豆")
        if parts[0] == "beans" and "black" in parts:
            specific.append("黑豆")
        if parts[0] == "beans" and "navy" in parts:
            specific.append("白芸豆")
        if parts[0] == "peas" and any("edible-podded" in part for part in parts):
            specific.append("荷兰豆")
        if parts[0] == "corn" and "sweet" in parts:
            specific.append("甜玉米")
        if parts[0] == "cabbage" and any("pak-choi" in part or "pe-tsai" in part for part in parts):
            specific.append("小白菜" if any("pak-choi" in part for part in parts) else "大白菜")
            ingredients = [value for value in ingredients if value != "卷心菜"]
        if parts[0] == "milk" and any(part == "dry" for part in parts):
            specific.append("奶粉")
        if parts[0] == "milk" and "whole" in parts:
            states.append("全脂")
        if parts[0] in ("egg", "eggs") and "white" in parts:
            specific.append("蛋清")
        if parts[0] in ("egg", "eggs") and "yolk" in parts:
            specific.append("蛋黄")
        if parts[0] in ("oil", "vegetable oil"):
            specific.extend(value + "油" for value in ingredients if value in ("橄榄", "菜籽", "玉米", "花生", "大豆", "椰子", "芝麻", "葵花籽", "棕榈", "牛油果"))
        if parts[0] in ("mushroom", "mushrooms") and "white" in parts:
            specific.append("白蘑菇")
        if parts[0] in ("mushroom", "mushrooms") and "oyster" in parts:
            specific.append("平菇")
            ingredients = [value for value in ingredients if value != "牡蛎"]
        if "烤制面包或肉糕" in ingredients:
            loaf = "午餐肉糕" if food["Category"] == "Sausages and Luncheon Meats" else "面包条"
            ingredients = [loaf if value == "烤制面包或肉糕" else value for value in ingredients]
            base = [loaf if value == "烤制面包或肉糕" else value for value in base]
        ingredients = list(dict.fromkeys(specific + ingredients))
        # 品牌首段没有食材时，以后续具体食材描述组成中文名，品牌仍在英文原名中。
        primary = specific or base or ingredients
        if not primary:
            uncovered.append({"SourceId": food["SourceId"], "OriginalName": original, "Head": head})
        old = known.get(food["SourceId"])
        name = old["Name"] if old else "·".join((primary + [x for x in ingredients if x not in primary])[:5])
        if not old and states:
            name += "（" + "、".join(states[:4]) + "）"
        qualifier = ""
        if set(ingredients).issubset(generic_labels):
            # 品牌或未指明品种的原文不补造食材，用具体原名限定中文描述，供核对。
            qualifier = ", ".join(original.split(",")[1:3]).strip() if head.lower().startswith("cereals") else head
            if qualifier:
                name += " · " + qualifier
        preparation = old["Preparation"] if old else "、".join(states)
        aliases = "；".join(dict.fromkeys(([old["Aliases"]] if old else []) + ingredients))
        if len(name) > 100 or len(preparation) > 200 or len(aliases) > 500:
            raise ValueError(f"中文描述超过现有食物字段长度：{food['SourceId']}")
        foods.append({"SourceId": food["SourceId"], "Name": name, "Aliases": aliases, "Preparation": preparation})
        audit.append({"SourceId": food["SourceId"], "OriginalName": original, "Name": name, "IngredientTerms": ingredients,
                      "StateTerms": states, "OriginalQualifier": qualifier,
                      "Method": "既有逐项中文映射" if old else "版本化词表组合；完整含义核对英文原名"})
    report_dir = root / "workspace/chinese-index"
    report_dir.mkdir(parents=True, exist_ok=True)
    report = {"Version": terms["Version"], "Total": len(foods), "Covered": len(foods) - len(uncovered), "Uncovered": uncovered,
              "UncoveredHeads": Counter(item["Head"] for item in uncovered), "Audit": audit}
    (report_dir / "coverage.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key not in ("Audit", "Uncovered")}, ensure_ascii=False, indent=2))
    if args.audit_only:
        return
    if uncovered:
        raise ValueError("存在没有中文食材索引的条目；审计已保存，未写入产品索引。")
    index = {"FormatVersion": 1, "Version": terms["Version"], "Provider": catalog["Provider"], "NutritionVersion": catalog["Version"],
             "CatalogSha256": hashlib.sha256(source.read_bytes()).hexdigest().upper(),
             "TermsSha256": hashlib.sha256((root / "data/search/usda-terms.json").read_bytes()).hexdigest().upper(),
             "AliasesSha256": hashlib.sha256((root / "data/search/aliases.json").read_bytes()).hexdigest().upper(),
             "Method": terms["Method"], "Foods": foods}
    target = root / "data/lookup/usda-zh.json"
    target.write_text(json.dumps(index, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"已生成{len(foods)}项索引；完整英文原名及官方营养文件未修改。")


if __name__ == "__main__":
    main()
