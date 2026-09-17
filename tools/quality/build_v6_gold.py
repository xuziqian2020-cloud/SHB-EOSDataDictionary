#!/usr/bin/env python3
"""从 V6 推断前的可信证据与人工复核关系生成可重复审计的金标准。"""

import argparse
import gc
import gzip
import io
import json
import os
import re
import sqlite3


MODULE_QUOTAS = {
    "仓库与库存": {"tables": 25, "fields": 170},
    "采购管理": {"tables": 25, "fields": 160},
    "销售与客户": {"tables": 25, "fields": 170},
    "生产制造": {"tables": 25, "fields": 170},
    "计划管理": {"tables": 25, "fields": 140},
    "质量管理": {"tables": 25, "fields": 90},
    "财务管理": {"tables": 25, "fields": 130},
    "物料与BOM": {"tables": 25, "fields": 170},
}

SAFE_TABLE_RULES = {
    "ExactProjectTable": "KnowledgeExact",
    "SqlExtendedDescription": "DatabaseComment",
    "ConfirmedBusinessSemantic": "UserConfirmed",
    "GridColumnCaption": "DirectBusinessCode",
    "DataColumnCaption": "DirectBusinessCode",
}

SAFE_FIELD_RULES = {
    "ExactProjectField": "KnowledgeExact",
    "SqlExtendedDescription": "DatabaseComment",
    "GridColumnCaption": "DirectBusinessCode",
    "DataColumnCaption": "DirectBusinessCode",
    "SqlColumnAlias": "DirectBusinessCode",
}

# 这四条关系已于 2026-09-16 对照 EOS 源码逐行复核，不能由自动候选替换。
REVIEWED_CODE_RELATIONS = {
    ("dbo", "ABOMV", "ABV_ID", "dbo", "ABOM", "ABV_ID"):
        "ERP/Bu.vb:17925",
    ("dbo", "AWarehouse", "AW_ID", "dbo", "AWarehouse_Dep", "AW_ID"):
        "Program_x/驻外办公室/frmAW_Dep.vb:24",
    ("dbo", "AWarehouse", "AW_ID", "dbo", "CF_AW", "AW_ID"):
        "ERP/Account 仓库帐/AC.vb:19180",
    ("dbo", "Ac_Title", "Ac_Title_ID", "dbo", "Account_01", "Ac_Title_ID"):
        "Purchase/mpPurchase.vb:353",
}

RELIABLE_LATIN_ABBREVIATIONS = {
    "ID", "GUID", "UID", "BOM", "OA", "AI", "EOS", "ERP", "API", "SQL", "URL", "IP",
    "HTTP", "HTTPS", "XML", "JSON", "PDF", "CAD", "SAP", "MES", "WMS", "TMS", "KIS", "K3",
}


def parse_arguments():
    """读取规范基线、已交叉核验候选和目标金标路径。"""
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", required=True)
    parser.add_argument("--candidate-db", required=True)
    parser.add_argument("--candidate-scope", required=True)
    parser.add_argument("--output", required=True)
    return parser.parse_args()


def value(metadata):
    """安全读取元数据文本。"""
    return (metadata or {}).get("Value") or ""


def find_safe_evidence(metadata, allowed_rules):
    """只返回白名单中的旧有直接证据，拒绝把 V6 自动结论当作金标。"""
    evidence = (metadata or {}).get("Evidence") or []
    matches = []
    for item in evidence:
        rule_name = (item or {}).get("RuleName") or ""
        if rule_name in allowed_rules:
            matches.append(item)
    matches.sort(key=lambda item: (
        (item or {}).get("SourcePath") or "",
        int((item or {}).get("SourceLine") or 0),
        (item or {}).get("RuleName") or ""))
    return matches[0] if matches else None


def is_reliable_chinese_name(candidate):
    """复用应用正式名边界排除全英文、下划线、流程说明和未翻译缩写。"""
    text = (candidate or "").strip()
    if not text or len(text) > 30 or not re.search(r"[\u4e00-\u9fff]", text):
        return False
    if re.search(r"[,，;；=()（）{}\[\]<>_]", text):
        return False
    if text == "暂无可靠中文名称" or text == "操作":
        return False
    if re.search(r"表示|用于|如果|当.+时|进行|代码|方法|函数|返回|点击|必须|开始时|总数|绑定到|引用方|属于", text):
        return False
    for fragment in re.findall(r"[A-Za-z]+", text):
        if fragment.upper() not in RELIABLE_LATIN_ABBREVIATIONS:
            return False
    return True


def evidence_source(item, object_name, field_name):
    """生成不含本机绝对路径的稳定证据定位。"""
    path = (item or {}).get("SourcePath") or ""
    line = int((item or {}).get("SourceLine") or 0)
    if path:
        return path.replace("\\", "/") + ((":" + str(line)) if line > 0 else "")
    target = object_name + (("." + field_name) if field_name else "")
    return ((item or {}).get("RuleName") or "TrustedEvidence") + ":" + target


def create_name_entry(kind, table, field, evidence, evidence_types):
    """把一条旧有可信名称固化为带稳定键的金标记录。"""
    object_name = table.get("ObjectName") or ""
    field_name = (field or {}).get("FieldName") or ""
    metadata = (field or {}).get("ChineseName") if field is not None else table.get("ChineseName")
    rule_name = evidence.get("RuleName") or ""
    stable_key = kind + "|dbo|" + object_name
    if field_name:
        stable_key += "|" + field_name
    return {
        "kind": kind,
        "stableKey": stable_key,
        "scopeKey": "SHB",
        "schemaName": table.get("SchemaName") or "dbo",
        "objectName": object_name,
        "fieldName": field_name,
        "expectedChineseName": value(metadata),
        "expectedDisposition": "CorrectName",
        "moduleName": value(table.get("ModuleName")),
        "evidenceType": evidence_types[rule_name],
        "source": evidence_source(evidence, object_name, field_name),
    }


def load_candidate_relations(database_path, scope_key):
    """从已通过双证据规则的候选中读取关系端点，名称金标不读取该候选。"""
    connection = sqlite3.connect(database_path)
    try:
        row = connection.execute(
            "SELECT Payload FROM SnapshotContents WHERE ScopeKey = ?", (scope_key,)).fetchone()
    finally:
        connection.close()
    if row is None:
        raise RuntimeError("候选数据库缺少指定作用域。")
    with gzip.GzipFile(fileobj=io.BytesIO(row[0])) as stream:
        candidate = json.load(stream)
    relations = {}
    for table in candidate.get("Tables") or []:
        for relation in table.get("Relations") or []:
            key = relation_key(relation)
            relation_type = relation.get("RelationType") or {}
            status = int(relation_type.get("Status") or 0)
            if status in (1, 2):
                relations[key] = relation
    del candidate
    gc.collect()
    return relations


def relation_key(relation):
    """生成方向明确的父子字段关系键。"""
    return tuple((relation.get(name) or "") for name in (
        "ParentSchemaName", "ParentTableName", "ParentFieldName",
        "ChildSchemaName", "ChildTableName", "ChildFieldName"))


def create_relation_entry(relation, evidence_type, source):
    """创建只比较真实父子方向、不依赖自动中文翻译的关系金标。"""
    key = relation_key(relation)
    return {
        "kind": "Relation",
        "stableKey": "Relation|" + "|".join(key),
        "scopeKey": "SHB",
        "schemaName": key[0],
        "objectName": key[1],
        "fieldName": key[2],
        "parentSchemaName": key[0],
        "parentTableName": key[1],
        "parentFieldName": key[2],
        "childSchemaName": key[3],
        "childTableName": key[4],
        "childFieldName": key[5],
        "expectedChineseName": "数据库外键关联" if evidence_type == "DatabaseForeignKey" else "业务代码关联",
        "expectedDisposition": "CorrectRelation",
        "moduleName": "",
        "evidenceType": evidence_type,
        "source": source,
    }


def select_relations(candidate_relations):
    """选择全部 96 条物理外键和四条人工逐行复核代码关系。"""
    entries = []
    for key in sorted(candidate_relations):
        relation = candidate_relations[key]
        status = int((relation.get("RelationType") or {}).get("Status") or 0)
        if status != 1:
            continue
        foreign_key = relation.get("ForeignKeyName") or "未命名外键"
        entries.append(create_relation_entry(
            relation, "DatabaseForeignKey", "SQLServerForeignKey:" + foreign_key))
    if len(entries) != 96:
        raise RuntimeError("物理外键唯一关系数不是预期的 96 条。")
    for key in sorted(REVIEWED_CODE_RELATIONS):
        relation = candidate_relations.get(key)
        if relation is None or int((relation.get("RelationType") or {}).get("Status") or 0) != 2:
            raise RuntimeError("人工复核代码关系未通过当前交叉核验规则：" + "|".join(key))
        entries.append(create_relation_entry(
            relation, "CrossCheckedBusinessCode", REVIEWED_CODE_RELATIONS[key]))
    return entries


def select_name_entries(baseline_path):
    """按八个核心模块的固定配额选择旧有可信表名和实际使用字段名。"""
    with gzip.open(baseline_path, "rt", encoding="utf-8-sig") as stream:
        baseline = json.load(stream)
    tables_by_module = {module: [] for module in MODULE_QUOTAS}
    fields_by_module = {module: [] for module in MODULE_QUOTAS}
    for table in baseline.get("Tables") or []:
        module = value(table.get("ModuleName"))
        if module not in MODULE_QUOTAS:
            continue
        table_evidence = find_safe_evidence(table.get("ChineseName"), SAFE_TABLE_RULES)
        if table_evidence is not None and is_reliable_chinese_name(value(table.get("ChineseName"))):
            tables_by_module[module].append((table, table_evidence))
        for field in table.get("Fields") or []:
            # 金标字段必须有真实业务使用记录，不能只因存在于数据库结构就入样。
            if not value(field.get("Usage")):
                continue
            field_evidence = find_safe_evidence(field.get("ChineseName"), SAFE_FIELD_RULES)
            if field_evidence is not None and is_reliable_chinese_name(value(field.get("ChineseName"))):
                fields_by_module[module].append((table, field, field_evidence))
    entries = []
    for module in MODULE_QUOTAS:
        tables_by_module[module].sort(key=lambda row: (
            row[0].get("ObjectName") or "").lower())
        fields_by_module[module].sort(key=lambda row: (
            (row[0].get("ObjectName") or "").lower(),
            (row[1].get("FieldName") or "").lower()))
        table_quota = MODULE_QUOTAS[module]["tables"]
        field_quota = MODULE_QUOTAS[module]["fields"]
        if len(tables_by_module[module]) < table_quota or len(fields_by_module[module]) < field_quota:
            raise RuntimeError("模块可信样本不足：" + module)
        for table, evidence in tables_by_module[module][:table_quota]:
            entries.append(create_name_entry(
                "Table", table, None, evidence, SAFE_TABLE_RULES))
        for table, field, evidence in fields_by_module[module][:field_quota]:
            entries.append(create_name_entry(
                "Field", table, field, evidence, SAFE_FIELD_RULES))
    del baseline
    gc.collect()
    return entries


def main():
    """生成固定 200 表、1,200 字段和 100 关系的 V6 金标准。"""
    args = parse_arguments()
    candidate_relations = load_candidate_relations(
        args.candidate_db, args.candidate_scope)
    entries = select_name_entries(args.baseline)
    entries.extend(select_relations(candidate_relations))
    counts = {
        kind: sum(1 for entry in entries if entry["kind"] == kind)
        for kind in ("Table", "Field", "Relation")
    }
    if counts != {"Table": 200, "Field": 1200, "Relation": 100}:
        raise RuntimeError("金标数量不符合固定门槛：" + repr(counts))
    document = {
        "version": "6.0",
        "baselineRevision": 11,
        "description": "仅来自 V6 推断前的知识库精确条目、数据库注释、直接业务代码与人工逐行复核关系。",
        "counts": counts,
        "moduleQuotas": MODULE_QUOTAS,
        "entries": entries,
    }
    output_directory = os.path.dirname(os.path.abspath(args.output))
    os.makedirs(output_directory, exist_ok=True)
    with open(args.output, "w", encoding="utf-8", newline="\n") as stream:
        json.dump(document, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps(counts, ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()
