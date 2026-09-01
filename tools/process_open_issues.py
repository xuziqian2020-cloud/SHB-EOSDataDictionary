"""XMZADD 20260901 将可信 GitHub Issue 身份绑定到可审计字典事件并原子发布规范快照。"""

import argparse
import copy
import gzip
import hashlib
import io
import json
import os
import re
import tempfile
import unicodedata
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath


TITLE_PREFIX = "[EOS-DICTIONARY-EVENT]"
MAXIMUM_OPERATION_COUNT = 100
MAXIMUM_TEXT_LENGTH = 2000
MAXIMUM_ISSUE_BODY_BYTES = 60 * 1024
MAXIMUM_PAYLOAD_BYTES = MAXIMUM_ISSUE_BODY_BYTES - 15
MAXIMUM_IDENTIFIER_LENGTH = 256
MAXIMUM_AUTHOR_LENGTH = 32
MAXIMUM_EVIDENCE_COUNT = 5
MAXIMUM_EVIDENCE_PATH_LENGTH = 512
MAXIMUM_EVIDENCE_SUMMARY_LENGTH = 240
BODY_PATTERN = re.compile(r"```json-v1\r?\n(.*?)\r?\n```", re.DOTALL)
SAFE_OPERATION_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,128}$")
SAFE_IDENTIFIER_PATTERN = re.compile(r"^[^\W]+(?:[^\W]|[@$#])*$", re.UNICODE)
SENSITIVE_ASSIGNMENT_PATTERN = re.compile(
    r"(?:password|pwd|token|access[-_\s]*token|client[-_\s]*secret|secret|api[-_\s]*key|authorization|"
    r"server|database|connection[-_\s]*string|data[-_\s]*source|initial[-_\s]*catalog|user[-_\s]*id|uid)\s*[:=]",
    re.IGNORECASE,
)
BEARER_TOKEN_PATTERN = re.compile(r"bearer\s+", re.IGNORECASE)

TABLE_BUSINESS_PROPERTIES = {
    "ChineseName",
    "BusinessMeaning",
    "ModuleName",
    "EntityName",
    "Remark",
    "Category",
    "KeepWhenEmpty",
}
FIELD_BUSINESS_PROPERTIES = {
    "ChineseName",
    "BusinessMeaning",
    "Usage",
    "EntityPropertyName",
    "Remark",
    "RelationSummary",
}
TABLE_STRUCTURE_PROPERTIES = {"SchemaName", "ObjectName", "ObjectType", "ApproximateRowCount"}
FIELD_STRUCTURE_PROPERTIES = {
    "FieldName",
    "OwnerTableName",
    "DataType",
    "LengthText",
    "IsRequired",
    "IsPrimaryKey",
    "IsForeignKey",
}
STRUCTURE_KINDS = {
    "AddTable",
    "RemoveTable",
    "AddField",
    "RemoveField",
    "AddRelation",
    "RemoveRelation",
}
SQL_SERVER_TYPES = {
    "bigint", "binary", "bit", "char", "date", "datetime", "datetime2",
    "datetimeoffset", "decimal", "float", "geography", "geometry", "hierarchyid",
    "image", "int", "money", "nchar", "ntext", "numeric", "nvarchar", "real",
    "rowversion", "smalldatetime", "smallint", "smallmoney", "sql_variant", "text",
    "time", "timestamp", "tinyint", "uniqueidentifier", "varbinary", "varchar", "xml",
}
BATCH_MEMBERS = {"BatchId", "AuthorGitHubUserId", "CreatedAtUtc", "Overrides", "Operations"}
OPERATION_MEMBERS = {
    "OperationId", "AuthorGitHubUserId", "ObjectKey", "FieldKey", "PropertyName",
    "OldValue", "NewValue", "ChangeKind", "CreatedAtUtc", "TablePayload",
    "FieldPayload", "RelationPayload", "Evidence",
}
EVIDENCE_MEMBERS = {"SourceType", "SourcePath", "SourceLine", "RuleName", "Explanation"}
TABLE_PAYLOAD_MEMBERS = {
    "SchemaName", "ObjectName", "ObjectType", "ApproximateRowCount", "Fields", "Relations"
}
FIELD_PAYLOAD_MEMBERS = {
    "FieldName", "OwnerTableName", "DataType", "LengthText", "IsRequired",
    "IsPrimaryKey", "IsForeignKey",
}
RELATION_PAYLOAD_MEMBERS = {
    "ForeignKeyName", "ParentSchemaName", "ParentTableName", "ParentFieldName",
    "ChildSchemaName", "ChildTableName", "ChildFieldName",
}
CATEGORY_VALUES = {"Business": 1, "BaseData": 2, "Technical": 3, "Excluded": 4}
CATEGORY_NAMES = {0: "Unclassified", 1: "Business", 2: "BaseData", 3: "Technical", 4: "Excluded"}


class IssueValidationError(ValueError):
    """XMZADD 20260901 用固定安全消息表示单个 Issue 校验或应用失败，避免回显不可信正文。"""


@dataclass(frozen=True)
class ProcessResult:
    """XMZADD 20260901 返回本轮可关闭 Issue、抓取版本、失败原因和已发布事件，供工作流安全延后外部动作。"""

    snapshot: dict
    closed_issue_numbers: list
    failed_issues: dict
    event_paths: list
    revision: int
    processed_issue_versions: dict


def _canonical_json_bytes(value):
    """XMZADD 20260901 生成稳定 UTF-8 JSON，保证快照哈希和跨平台重放结果一致。"""
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False
    ).encode("utf-8")


def _deterministic_gzip(content):
    """XMZADD 20260901 固定压缩时间和文件名，避免相同快照因运行环境产生不同哈希。"""
    output = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", fileobj=output, compresslevel=9, mtime=0) as stream:
        stream.write(content)
    return output.getvalue()


def _has_unsafe_control(value):
    """XMZADD 20260901 排除日志控制字符与 Unicode 隐形格式字符，同时保留业务说明需要的换行和制表。"""
    if not isinstance(value, str):
        return False
    for character in value:
        category = unicodedata.category(character)
        if category in {"Cf", "Zl", "Zp"} or category == "Cc" and character not in "\r\n\t":
            return True
    return False


def _validate_bounded_text(value, field_name, allow_none=True):
    """XMZADD 20260901 对所有公开字符串应用一致边界，防止超长或不可见内容污染事件。"""
    if value is None and allow_none:
        return
    if not isinstance(value, str):
        raise IssueValidationError(field_name + " 必须是字符串。")
    if len(value) > MAXIMUM_TEXT_LENGTH or _has_unsafe_control(value):
        raise IssueValidationError(field_name + " 超过安全文本边界。")


def _require_exact_members(value, allowed, name):
    """XMZADD 20260901 拒绝任意层未知成员，避免隐藏字段绕过公开协议白名单。"""
    if not isinstance(value, dict):
        raise IssueValidationError(name + " 必须是 JSON 对象。")
    if set(value) - allowed:
        raise IssueValidationError(name + " 包含未知成员。")


def _normalize_numeric_user_id(value, name="GitHub user ID"):
    """XMZADD 20260901 只接受 GitHub API 的非零数字不可变用户 ID 参与署名和授权。"""
    if isinstance(value, bool):
        raise IssueValidationError(name + " 格式无效。")
    text = str(value) if isinstance(value, int) else value
    if not isinstance(text, str) or not text or len(text) > MAXIMUM_AUTHOR_LENGTH:
        raise IssueValidationError(name + " 格式无效。")
    if not text.isascii() or not text.isdigit() or text == "0":
        raise IssueValidationError(name + " 格式无效。")
    return text


def _normalize_publishers(publishers):
    """XMZADD 20260901 将配置中的数字发布者 ID 固化为集合，避免登录名参与物理结构授权。"""
    if not isinstance(publishers, list):
        raise ValueError("publishers 必须是数字 GitHub user ID 列表。")
    normalized = set()
    for value in publishers:
        normalized.add(_normalize_numeric_user_id(value, "publisher GitHub user ID"))
    return normalized


def _is_safe_operation_id(value):
    """XMZADD 20260901 限定全局幂等键字符集，保证它不能形成路径或模糊文件名。"""
    return isinstance(value, str) and SAFE_OPERATION_ID_PATTERN.fullmatch(value) is not None


def _is_safe_identifier(value):
    """XMZADD 20260901 限定 SQL Server 常用标识符字符并拒绝空白、路径和分隔符。"""
    if not isinstance(value, str) or not value or len(value) > MAXIMUM_IDENTIFIER_LENGTH:
        return False
    if value != value.strip() or not SAFE_IDENTIFIER_PATTERN.fullmatch(value):
        return False
    for character in value:
        if not (character.isalnum() or character in "_@$#"):
            return False
    return True


def _split_object_key(value):
    """XMZADD 20260901 要求对象键仅由唯一的架构名和对象名组成，消除路径式歧义。"""
    if not isinstance(value, str) or value.count(".") != 1:
        raise IssueValidationError("ObjectKey 格式无效。")
    schema_name, object_name = value.split(".")
    if not _is_safe_identifier(schema_name) or not _is_safe_identifier(object_name):
        raise IssueValidationError("ObjectKey 格式无效。")
    return schema_name, object_name


def _normalize_sql_type(value):
    """XMZADD 20260901 按 C# 协议把 SQL Server 系统类型规范为小写白名单值。"""
    if not isinstance(value, str):
        raise IssueValidationError("DataType 格式无效。")
    normalized = value.strip().lower()
    if normalized not in SQL_SERVER_TYPES:
        raise IssueValidationError("DataType 不在 SQL Server 类型白名单中。")
    return normalized


def _normalize_length(value):
    """XMZADD 20260901 按 C# 协议规范 MAX、数字、精度小数位和无长度破折号。"""
    if not isinstance(value, str) or not value or value != value.strip():
        raise IssueValidationError("LengthText 格式无效。")
    if value == "—":
        return value
    if value.upper() == "MAX":
        return "MAX"
    if "," in value:
        parts = value.split(",")
        if len(parts) != 2 or not parts[0].isdigit() or not parts[1].isdigit():
            raise IssueValidationError("LengthText 格式无效。")
        precision = int(parts[0])
        scale = int(parts[1])
        if precision < 1 or precision > 38 or scale < 0 or scale > precision:
            raise IssueValidationError("LengthText 格式无效。")
        return str(precision) + "," + str(scale)
    if not value.isascii() or not value.isdigit():
        raise IssueValidationError("LengthText 格式无效。")
    return str(int(value))


def _validate_length_for_type(data_type, length_text):
    """XMZADD 20260901 保证长度范围与 SQL Server 类型匹配，避免发布不可重放的物理字段。"""
    if data_type in {"decimal", "numeric"}:
        valid = "," in length_text
    elif data_type in {"varchar", "varbinary"}:
        valid = length_text == "MAX" or length_text.isdigit() and 1 <= int(length_text) <= 8000
    elif data_type == "nvarchar":
        valid = length_text == "MAX" or length_text.isdigit() and 1 <= int(length_text) <= 4000
    elif data_type in {"char", "binary"}:
        valid = length_text.isdigit() and 1 <= int(length_text) <= 8000
    elif data_type == "nchar":
        valid = length_text.isdigit() and 1 <= int(length_text) <= 4000
    else:
        valid = length_text == "—"
    if not valid:
        raise IssueValidationError("字段类型和长度不匹配。")


def _normalize_issue_time(value):
    """XMZADD 20260901 只使用 GitHub API 的 UTC 创建时间生成事件路径和发布审计时间。"""
    if not isinstance(value, str) or len(value) > 64:
        raise IssueValidationError("Issue created_at 格式无效。")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exception:
        raise IssueValidationError("Issue created_at 格式无效。") from exception
    if parsed.tzinfo is None or parsed.utcoffset() != timezone.utc.utcoffset(parsed):
        raise IssueValidationError("Issue created_at 必须是 UTC。")
    parsed = parsed.astimezone(timezone.utc)
    return parsed, "/Date(" + str(int(parsed.timestamp() * 1000)) + ")/"


def _parse_issue_body(body):
    """XMZADD 20260901 只接收一个 json-v1 代码块，避免 Markdown 附加内容参与处理。"""
    if not isinstance(body, str):
        raise IssueValidationError("Issue 正文格式无效。")
    if len(body.encode("utf-8")) > MAXIMUM_ISSUE_BODY_BYTES:
        raise IssueValidationError("Issue 正文大小超过限制。")
    match = BODY_PATTERN.fullmatch(body)
    if match is None:
        raise IssueValidationError("Issue 正文必须只有一个 json-v1 代码块。")
    raw_json = match.group(1).encode("utf-8")
    if not raw_json or len(raw_json) > MAXIMUM_PAYLOAD_BYTES:
        raise IssueValidationError("Issue JSON 大小超过限制或为空。")
    try:
        batch = json.loads(raw_json.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise IssueValidationError("Issue JSON 格式无效。") from exception
    if not isinstance(batch, dict):
        raise IssueValidationError("Issue JSON 根节点必须是对象。")
    return batch


def _validate_payload_texts(payload, members, name):
    """XMZADD 20260901 对结构载荷文本统一限长，避免任意结构字段污染公共事件。"""
    _require_exact_members(payload, members, name)
    for key, value in payload.items():
        if isinstance(value, str) or value is None:
            _validate_bounded_text(value, name + "." + key)


def _normalize_field_payload(payload):
    """XMZADD 20260901 校验并规范字段结构载荷，使服务端与 C# 使用相同类型长度语义。"""
    _validate_payload_texts(payload, FIELD_PAYLOAD_MEMBERS, "FieldPayload")
    field_name = payload.get("FieldName")
    owner_name = payload.get("OwnerTableName")
    if not _is_safe_identifier(field_name) or not _is_safe_identifier(owner_name):
        raise IssueValidationError("FieldPayload 标识符格式无效。")
    for name in ("IsRequired", "IsPrimaryKey", "IsForeignKey"):
        if not isinstance(payload.get(name), bool):
            raise IssueValidationError("FieldPayload 布尔值格式无效。")
    data_type = _normalize_sql_type(payload.get("DataType"))
    length_text = _normalize_length(payload.get("LengthText"))
    _validate_length_for_type(data_type, length_text)
    normalized = copy.deepcopy(payload)
    normalized["DataType"] = data_type
    normalized["LengthText"] = length_text
    return normalized


def _normalize_table_payload(payload):
    """XMZADD 20260901 校验新增表只包含单表事实，字段关系必须由独立事件追加。"""
    _validate_payload_texts(payload, TABLE_PAYLOAD_MEMBERS, "TablePayload")
    if not _is_safe_identifier(payload.get("SchemaName")) or not _is_safe_identifier(payload.get("ObjectName")):
        raise IssueValidationError("TablePayload 标识符格式无效。")
    row_count = payload.get("ApproximateRowCount")
    if isinstance(row_count, bool) or not isinstance(row_count, int) or row_count < 0:
        raise IssueValidationError("ApproximateRowCount 必须是非负整数。")
    if payload.get("ObjectType") != "TABLE":
        raise IssueValidationError("共享快照只允许 TABLE。")
    if payload.get("Fields") != [] or payload.get("Relations") != []:
        raise IssueValidationError("新增表不能内嵌字段或关系。")
    return copy.deepcopy(payload)


def _normalize_relation_payload(payload):
    """XMZADD 20260901 校验关系完整父子端点，确保它拥有可稳定比较的结构身份。"""
    _validate_payload_texts(payload, RELATION_PAYLOAD_MEMBERS, "RelationPayload")
    normalized = {}
    for name in RELATION_PAYLOAD_MEMBERS:
        value = payload.get(name)
        if not _is_safe_identifier(value):
            raise IssueValidationError("RelationPayload 标识符格式无效。")
        normalized[name] = value
    return normalized


def _validate_operation_texts(operation):
    """XMZADD 20260901 校验操作所有自由文本和正文作者边界，但不采用正文身份授权。"""
    for name in (
        "OperationId", "ObjectKey", "FieldKey", "PropertyName", "OldValue", "NewValue", "ChangeKind",
        "CreatedAtUtc"
    ):
        _validate_bounded_text(operation.get(name), name)
    body_author = operation.get("AuthorGitHubUserId")
    if body_author not in (None, ""):
        _validate_bounded_text(body_author, "AuthorGitHubUserId", allow_none=False)
        if len(body_author) > MAXIMUM_AUTHOR_LENGTH:
            raise IssueValidationError("正文作者字段格式无效。")


def _normalize_set_operation(operation, is_publisher):
    """XMZADD 20260901 校验业务与物理属性作用域并仅向发布者开放物理赋值。"""
    if operation.get("NewValue") is None:
        raise IssueValidationError("Set 必须显式提供 NewValue。")
    if any(operation.get(name) is not None for name in ("TablePayload", "FieldPayload", "RelationPayload")):
        raise IssueValidationError("Set 不能携带结构载荷。")
    field_key = operation.get("FieldKey") or ""
    property_name = operation.get("PropertyName")
    field_scope = bool(field_key)
    ordinary = FIELD_BUSINESS_PROPERTIES if field_scope else TABLE_BUSINESS_PROPERTIES
    physical = FIELD_STRUCTURE_PROPERTIES if field_scope else TABLE_STRUCTURE_PROPERTIES
    all_properties = TABLE_BUSINESS_PROPERTIES | FIELD_BUSINESS_PROPERTIES | TABLE_STRUCTURE_PROPERTIES | FIELD_STRUCTURE_PROPERTIES
    if property_name not in all_properties:
        raise IssueValidationError("属性不在公开白名单中。")
    if property_name not in ordinary | physical:
        raise IssueValidationError("属性作用域与 FieldKey 不一致。")
    if property_name in physical and not is_publisher:
        raise IssueValidationError("物理结构属性仅允许发布者提交。")
    value = operation["NewValue"]
    if _contains_credential_shape(value):
        raise IssueValidationError("公开赋值包含凭据形态。")
    if property_name == "Category" and value not in CATEGORY_VALUES:
        raise IssueValidationError("Category 格式无效。")
    if property_name in {"KeepWhenEmpty", "IsRequired", "IsPrimaryKey", "IsForeignKey"} and value not in {"True", "False"}:
        raise IssueValidationError("布尔属性必须是 True 或 False。")
    if property_name == "ApproximateRowCount":
        if not value.isascii() or not value.isdigit():
            raise IssueValidationError("ApproximateRowCount 必须是非负整数。")
        int(value)
    if property_name == "ObjectType" and value != "TABLE":
        raise IssueValidationError("共享快照只允许 TABLE。")
    if property_name in {"SchemaName", "ObjectName", "FieldName", "OwnerTableName"} and not _is_safe_identifier(value):
        raise IssueValidationError("结构标识符格式无效。")
    normalized = copy.deepcopy(operation)
    if property_name == "DataType":
        normalized["NewValue"] = _normalize_sql_type(value)
    if property_name == "LengthText":
        normalized["NewValue"] = _normalize_length(value)
    return normalized


def _normalize_structure_operation(operation, is_publisher):
    """XMZADD 20260901 校验结构增删事件只携带对应载荷并要求发布者不可变 ID 授权。"""
    if not is_publisher:
        raise IssueValidationError("结构事件仅允许发布者提交。")
    kind = operation["ChangeKind"]
    field_key = operation.get("FieldKey") or ""
    property_name = operation.get("PropertyName") or ""
    table_payload = operation.get("TablePayload")
    field_payload = operation.get("FieldPayload")
    relation_payload = operation.get("RelationPayload")
    normalized = copy.deepcopy(operation)
    if kind == "AddTable":
        if field_key or property_name or field_payload is not None or relation_payload is not None:
            raise IssueValidationError("AddTable 载荷组合无效。")
        normalized["TablePayload"] = _normalize_table_payload(table_payload)
        expected = normalized["TablePayload"]["SchemaName"] + "." + normalized["TablePayload"]["ObjectName"]
        if operation["ObjectKey"].casefold() != expected.casefold():
            raise IssueValidationError("AddTable 稳定键与载荷不一致。")
    elif kind == "RemoveTable":
        if field_key or property_name or any(value is not None for value in (table_payload, field_payload, relation_payload)):
            raise IssueValidationError("RemoveTable 载荷组合无效。")
    elif kind == "AddField":
        if not field_key or property_name or table_payload is not None or relation_payload is not None:
            raise IssueValidationError("AddField 载荷组合无效。")
        normalized["FieldPayload"] = _normalize_field_payload(field_payload)
        object_name = _split_object_key(operation["ObjectKey"])[1]
        if field_key.casefold() != normalized["FieldPayload"]["FieldName"].casefold():
            raise IssueValidationError("AddField 字段键与载荷不一致。")
        if object_name.casefold() != normalized["FieldPayload"]["OwnerTableName"].casefold():
            raise IssueValidationError("AddField 所属表与载荷不一致。")
    elif kind == "RemoveField":
        if not field_key or property_name or any(value is not None for value in (table_payload, field_payload, relation_payload)):
            raise IssueValidationError("RemoveField 载荷组合无效。")
    else:
        if field_key or property_name or table_payload is not None or field_payload is not None:
            raise IssueValidationError("关系事件载荷组合无效。")
        normalized["RelationPayload"] = _normalize_relation_payload(relation_payload)
        child_key = normalized["RelationPayload"]["ChildSchemaName"] + "." + normalized["RelationPayload"]["ChildTableName"]
        if operation["ObjectKey"].casefold() != child_key.casefold():
            raise IssueValidationError("关系事件 ObjectKey 与子表端点不一致。")
    return normalized


def _normalize_operation(operation, trusted_author, is_publisher):
    """XMZADD 20260901 生成只含白名单成员和 API 可信作者的规范操作副本。"""
    _require_exact_members(operation, OPERATION_MEMBERS, "Operation")
    _validate_operation_texts(operation)
    operation_id = operation.get("OperationId")
    if not _is_safe_operation_id(operation_id):
        raise IssueValidationError("OperationId 格式无效。")
    _split_object_key(operation.get("ObjectKey"))
    field_key = operation.get("FieldKey") or ""
    if field_key and not _is_safe_identifier(field_key):
        raise IssueValidationError("FieldKey 格式无效。")
    kind = operation.get("ChangeKind")
    if kind == "Set":
        normalized = _normalize_set_operation(operation, is_publisher)
    elif kind in STRUCTURE_KINDS:
        normalized = _normalize_structure_operation(operation, is_publisher)
    else:
        raise IssueValidationError("ChangeKind 不在固定白名单中。")
    normalized["Evidence"] = _normalize_published_evidence(operation.get("Evidence"), is_publisher)
    normalized["AuthorGitHubUserId"] = trusted_author
    return normalized


def _normalize_published_evidence(evidence, is_publisher):
    """XMZADD 20260901 仅允许结构发布者提交固定数量的相对路径短证据。"""
    if evidence is None:
        return []
    if not isinstance(evidence, list) or len(evidence) > MAXIMUM_EVIDENCE_COUNT:
        raise IssueValidationError("公开证据数量无效。")
    if evidence and not is_publisher:
        raise IssueValidationError("公开来源证据仅允许结构发布者提交。")
    normalized = []
    for item in evidence:
        _require_exact_members(item, EVIDENCE_MEMBERS, "Evidence")
        source_type = item.get("SourceType")
        source_path = item.get("SourcePath")
        source_line = item.get("SourceLine")
        rule_name = item.get("RuleName")
        explanation = item.get("Explanation")
        for value in (source_type, source_path, rule_name, explanation):
            if not isinstance(value, str) or any(
                    unicodedata.category(character) in {"Cc", "Cf", "Zl", "Zp"} for character in value):
                raise IssueValidationError("公开证据文本无效。")
        if len(source_type) > 32 or len(source_path) > MAXIMUM_EVIDENCE_PATH_LENGTH or \
                len(rule_name) > 64 or len(explanation) > MAXIMUM_EVIDENCE_SUMMARY_LENGTH:
            raise IssueValidationError("公开证据文本超过限制。")
        if isinstance(source_line, bool) or not isinstance(source_line, int) or not 0 <= source_line <= 10000000:
            raise IssueValidationError("公开证据行号无效。")
        if source_path:
            path = PurePosixPath(source_path)
            if path.is_absolute() or "\\" in source_path or ":" in source_path or \
                    any(part in ("", ".", "..") for part in path.parts) or path.as_posix() != source_path:
                raise IssueValidationError("公开证据路径无效。")
        combined = " ".join((source_type, source_path, rule_name, explanation))
        if _contains_credential_shape(combined):
            raise IssueValidationError("公开证据包含凭据形态。")
        normalized.append({
            "SourceType": source_type,
            "SourcePath": source_path,
            "SourceLine": source_line,
            "RuleName": rule_name,
            "Explanation": explanation,
        })
    return normalized


def _contains_credential_shape(value):
    """XMZADD 20260901 以与 C# 相同的赋值规则识别凭据和连接文本，不在异常中回显原文。"""
    text = value or ""
    return SENSITIVE_ASSIGNMENT_PATTERN.search(text) is not None or BEARER_TOKEN_PATTERN.search(text) is not None


def _normalize_batch(batch, trusted_author, publishers):
    """XMZADD 20260901 校验单 Issue 批次并绑定可信作者，同时拒绝本地 Overrides 进入公共仓库。"""
    _require_exact_members(batch, BATCH_MEMBERS, "Batch")
    batch_id = batch.get("BatchId")
    _validate_bounded_text(batch_id, "BatchId", allow_none=False)
    if not _is_safe_operation_id(batch_id):
        raise IssueValidationError("BatchId 格式无效。")
    body_author = batch.get("AuthorGitHubUserId")
    if body_author not in (None, ""):
        _validate_bounded_text(body_author, "AuthorGitHubUserId", allow_none=False)
        if len(body_author) > MAXIMUM_AUTHOR_LENGTH:
            raise IssueValidationError("正文作者字段格式无效。")
    _validate_bounded_text(batch.get("CreatedAtUtc"), "CreatedAtUtc")
    overrides = batch.get("Overrides", [])
    if overrides not in (None, []):
        raise IssueValidationError("Overrides 不允许发布。")
    operations = batch.get("Operations")
    if not isinstance(operations, list) or not operations:
        raise IssueValidationError("Operations 必须是非空数组。")
    if len(operations) > MAXIMUM_OPERATION_COUNT:
        raise IssueValidationError("单 Issue 操作数超过限制。")
    normalized_operations = []
    seen_ids = set()
    is_publisher = trusted_author in publishers
    for operation in operations:
        normalized = _normalize_operation(operation, trusted_author, is_publisher)
        operation_id = normalized["OperationId"]
        if operation_id in seen_ids:
            raise IssueValidationError("单 Issue 内 OperationId 重复。")
        seen_ids.add(operation_id)
        normalized_operations.append(normalized)
    normalized = {
        "BatchId": batch_id,
        "AuthorGitHubUserId": trusted_author,
        "CreatedAtUtc": batch.get("CreatedAtUtc"),
        "Overrides": [],
        "Operations": normalized_operations,
    }
    if len(_canonical_json_bytes(normalized)) > MAXIMUM_PAYLOAD_BYTES:
        raise IssueValidationError("规范 JSON 大小超过限制。")
    return normalized


def _empty_snapshot():
    """XMZADD 20260901 为尚未发布快照的安全种子仓库提供客户端可验证的空结构。"""
    return {
        "FormatVersion": 1,
        "Revision": 0,
        "RefreshedAt": "/Date(0)/",
        "Tables": [],
        "ExcludedObjects": [],
        "Abbreviations": [],
    }


def _load_snapshot(repo_dir):
    """XMZADD 20260901 校验现有压缩快照哈希后加载工作副本，避免在损坏基线上继续发布。"""
    manifest_path = repo_dir / "snapshot" / "manifest.json"
    legacy_snapshot_path = repo_dir / "snapshot" / "latest.json.gz"
    if not manifest_path.exists() and not legacy_snapshot_path.exists():
        return _empty_snapshot()
    if not manifest_path.exists():
        raise ValueError("现有快照缺少 manifest。")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    relative_snapshot_path = manifest.get("SnapshotPath")
    if not isinstance(relative_snapshot_path, str) or not relative_snapshot_path:
        raise ValueError("现有 manifest 快照路径无效。")
    relative_path = Path(relative_snapshot_path)
    if relative_path.is_absolute() or "\\" in relative_snapshot_path or ".." in relative_path.parts:
        raise ValueError("现有 manifest 快照路径无效。")
    snapshot_path = (repo_dir / relative_path).resolve()
    snapshot_root = (repo_dir / "snapshot").resolve()
    try:
        snapshot_path.relative_to(snapshot_root)
    except ValueError as exception:
        raise ValueError("现有 manifest 快照路径越界。") from exception
    if not snapshot_path.is_file():
        raise ValueError("现有 manifest 快照文件缺失。")
    content = snapshot_path.read_bytes()
    if hashlib.sha256(content).hexdigest() != manifest.get("SnapshotSha256"):
        raise ValueError("现有快照哈希无效。")
    snapshot = json.loads(gzip.decompress(content).decode("utf-8"))
    if not isinstance(snapshot, dict) or snapshot.get("FormatVersion") != 1:
        raise ValueError("现有快照格式无效。")
    if not isinstance(snapshot.get("Revision"), int) or snapshot["Revision"] < 0:
        raise ValueError("现有快照修订无效。")
    if manifest.get("Revision") != snapshot["Revision"]:
        raise ValueError("现有快照和 manifest 修订不一致。")
    for collection in ("Tables", "ExcludedObjects", "Abbreviations"):
        if not isinstance(snapshot.get(collection), list):
            raise ValueError("现有快照集合无效。")
    return snapshot


def _load_applied_operation_ids(repo_dir):
    """XMZADD 20260901 从不可变事件恢复全局幂等集合，使进程重启后仍忽略重复操作。"""
    event_root = repo_dir / "events"
    operation_ids = set()
    if not event_root.exists():
        return operation_ids
    for current_root, directory_names, file_names in os.walk(event_root, followlinks=False):
        directory_names[:] = [name for name in directory_names if name != "revisions"]
        for file_name in file_names:
            if not file_name.endswith(".json"):
                continue
            path = Path(current_root) / file_name
            try:
                event = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as exception:
                raise ValueError("现有事件文件无效。") from exception
            operation_id = event.get("OperationId")
            if not _is_safe_operation_id(operation_id):
                raise ValueError("现有事件 OperationId 无效。")
            operation_ids.add(operation_id)
    return operation_ids


def _find_table(snapshot, object_key):
    """XMZADD 20260901 按不区分大小写的稳定对象键定位共享表。"""
    schema_name, object_name = _split_object_key(object_key)
    for table in snapshot["Tables"]:
        if table.get("SchemaName", "").casefold() == schema_name.casefold() and table.get("ObjectName", "").casefold() == object_name.casefold():
            return table
    return None


def _find_field(table, field_key):
    """XMZADD 20260901 按不区分大小写的稳定字段键定位共享字段。"""
    for field in table.get("Fields", []):
        if field.get("FieldName", "").casefold() == field_key.casefold():
            return field
    return None


def _metadata_text(value):
    """XMZADD 20260901 从元数据容器读取服务端真实旧值供不可变审计。"""
    if not isinstance(value, dict):
        return ""
    text = value.get("Value")
    return text if isinstance(text, str) else ""


def _remote_manual_value(current, new_value, published_evidence=None):
    """XMZADD 20260901 区分人工修改与结构发布证据值并保留可公开追溯链。"""
    current = current if isinstance(current, dict) else {}
    automatic = current.get("OriginalAutomaticValue") or current.get("Value") or ""
    evidence = copy.deepcopy(published_evidence) if published_evidence else \
        (copy.deepcopy(current.get("Evidence")) if isinstance(current.get("Evidence"), list) else [])
    if published_evidence:
        source_types = {item.get("SourceType") for item in published_evidence}
        if "EOS知识库" in source_types:
            status, score, source_type, summary = 8, 95, "EOS知识库", "EOS 项目知识库精确条目"
        elif "EOS源码" in source_types:
            status, score, source_type, summary = 2, 85, "EOS源码", "EOS 源码可靠证据"
        else:
            status, score, source_type, summary = 1, 90, "数据库依据", "结构发布数据库说明"
        return {
            "Value": new_value, "Description": None, "Status": status,
            "ConfidenceScore": score, "SourceType": source_type, "SourceSummary": summary,
            "OriginalAutomaticValue": automatic, "IsManualOverride": False,
            "IsLocked": False, "Evidence": evidence,
        }
    return {
        "Value": new_value,
        "Description": None,
        "Status": 0,
        "ConfidenceScore": 100,
        "SourceType": "GitHub人工维护",
        "SourceSummary": "GitHub人工确认",
        "OriginalAutomaticValue": automatic,
        "IsManualOverride": True,
        "IsLocked": True,
        "Evidence": evidence,
    }


def _relation_identity(payload):
    """XMZADD 20260901 用外键名和完整父子端点形成关系的稳定比较身份。"""
    return tuple(payload.get(name, "").casefold() for name in (
        "ForeignKeyName", "ParentSchemaName", "ParentTableName", "ParentFieldName",
        "ChildSchemaName", "ChildTableName", "ChildFieldName",
    ))


def _find_relation(snapshot, payload):
    """XMZADD 20260901 在全快照中按稳定身份查找唯一关系。"""
    identity = _relation_identity(payload)
    for table in snapshot["Tables"]:
        for relation in table.get("Relations", []):
            if _relation_identity(relation) == identity:
                return relation
    return None


def _new_table(payload):
    """XMZADD 20260901 由类型化载荷创建不含业务猜测的新表快照记录。"""
    return {
        "ScopeKey": None,
        "SchemaName": payload["SchemaName"],
        "ObjectName": payload["ObjectName"],
        "ObjectType": payload["ObjectType"],
        "ChineseName": None,
        "ModuleName": None,
        "EntityName": None,
        "BusinessMeaning": None,
        "Remark": None,
        "Category": 0,
        "ApproximateRowCount": payload["ApproximateRowCount"],
        "KeepWhenEmpty": False,
        "Fields": [],
        "Relations": [],
    }


def _new_field(payload):
    """XMZADD 20260901 由类型化载荷创建不含业务猜测的新字段快照记录。"""
    return {
        "FieldName": payload["FieldName"],
        "ChineseName": None,
        "OwnerTableName": payload["OwnerTableName"],
        "EntityPropertyName": None,
        "BusinessMeaning": None,
        "Usage": None,
        "DataType": payload["DataType"],
        "LengthText": payload["LengthText"],
        "IsRequired": payload["IsRequired"],
        "IsPrimaryKey": payload["IsPrimaryKey"],
        "IsForeignKey": payload["IsForeignKey"],
        "EnumName": None,
        "EnumItems": [],
        "RelationSummary": None,
        "Remark": None,
    }


def _new_relation(payload):
    """XMZADD 20260901 由完整端点载荷创建结构关系且不附加未经确认的业务解释。"""
    relation = copy.deepcopy(payload)
    relation.update({"ScopeKey": None, "RelationType": None, "BusinessMeaning": None, "Remark": None})
    return relation


def _field_payload(field):
    """XMZADD 20260901 从服务端字段提取删除前或新增后的真实物理载荷。"""
    return {name: field[name] for name in FIELD_PAYLOAD_MEMBERS}


def _relation_payload(relation):
    """XMZADD 20260901 从服务端关系提取不可变审计所需的完整端点载荷。"""
    return {name: relation[name] for name in RELATION_PAYLOAD_MEMBERS}


def _table_payload(table, include_children):
    """XMZADD 20260901 从服务端表提取物理载荷，并在删除审计中保留字段关系。"""
    payload = {
        "SchemaName": table["SchemaName"],
        "ObjectName": table["ObjectName"],
        "ObjectType": table["ObjectType"],
        "ApproximateRowCount": table["ApproximateRowCount"],
        "Fields": [],
        "Relations": [],
    }
    if include_children:
        payload["Fields"] = [_field_payload(field) for field in table.get("Fields", [])]
        unique = {}
        for relation in table.get("Relations", []):
            unique[_relation_identity(relation)] = _relation_payload(relation)
        payload["Relations"] = list(unique.values())
    return payload


def _remove_relations(snapshot, predicate):
    """XMZADD 20260901 从所有表同步移除命中关系，避免父子表保存不一致副本。"""
    for table in snapshot["Tables"]:
        table["Relations"] = [relation for relation in table.get("Relations", []) if not predicate(relation)]


def _recalculate_foreign_keys(snapshot):
    """XMZADD 20260901 按剩余真实关系重算外键标志，避免删除关系后保留陈旧物理状态。"""
    for table in snapshot["Tables"]:
        for field in table.get("Fields", []):
            field["IsForeignKey"] = False
    seen = set()
    for table in snapshot["Tables"]:
        for relation in table.get("Relations", []):
            identity = _relation_identity(relation)
            if identity in seen:
                continue
            seen.add(identity)
            child = _find_table(snapshot, relation["ChildSchemaName"] + "." + relation["ChildTableName"])
            child_field = _find_field(child, relation["ChildFieldName"]) if child else None
            if child_field:
                child_field["IsForeignKey"] = True


def _apply_set(snapshot, operation):
    """XMZADD 20260901 应用白名单标量并返回服务端实际前后值，不采用客户端 OldValue。"""
    table = _find_table(snapshot, operation["ObjectKey"])
    if table is None:
        raise IssueValidationError("事件目标表不存在。")
    field_key = operation.get("FieldKey") or ""
    target = _find_field(table, field_key) if field_key else table
    if target is None:
        raise IssueValidationError("事件目标字段不存在。")
    property_name = operation["PropertyName"]
    value = operation["NewValue"]
    metadata_properties = FIELD_BUSINESS_PROPERTIES if field_key else TABLE_BUSINESS_PROPERTIES - {"Category", "KeepWhenEmpty"}
    if property_name in metadata_properties:
        old_value = _metadata_text(target.get(property_name))
        target[property_name] = _remote_manual_value(
            target.get(property_name), value, operation.get("Evidence"))
        return old_value, value
    old_raw = target.get(property_name)
    if property_name == "Category":
        old_value = CATEGORY_NAMES.get(old_raw, "Unclassified")
        target[property_name] = CATEGORY_VALUES[value]
    elif property_name in {"KeepWhenEmpty", "IsRequired", "IsPrimaryKey", "IsForeignKey"}:
        old_value = "True" if old_raw else "False"
        target[property_name] = value == "True"
    elif property_name == "ApproximateRowCount":
        old_value = str(old_raw or 0)
        target[property_name] = int(value)
    else:
        old_value = old_raw if isinstance(old_raw, str) else ""
        target[property_name] = value
    return old_value, value


def _apply_structure(snapshot, operation):
    """XMZADD 20260901 事务性应用结构增删并返回服务端真实结构前后载荷。"""
    kind = operation["ChangeKind"]
    result = {"OldTablePayload": None, "NewTablePayload": None, "OldFieldPayload": None,
              "NewFieldPayload": None, "OldRelationPayload": None, "NewRelationPayload": None}
    if kind == "AddTable":
        if _find_table(snapshot, operation["ObjectKey"]) is not None:
            raise IssueValidationError("新增表已经存在。")
        table = _new_table(operation["TablePayload"])
        snapshot["Tables"].append(table)
        result["NewTablePayload"] = _table_payload(table, False)
    elif kind == "RemoveTable":
        table = _find_table(snapshot, operation["ObjectKey"])
        if table is None:
            raise IssueValidationError("删除表不存在。")
        result["OldTablePayload"] = _table_payload(table, True)
        schema_name = table["SchemaName"].casefold()
        object_name = table["ObjectName"].casefold()
        _remove_relations(snapshot, lambda relation: (
            relation["ParentSchemaName"].casefold() == schema_name and relation["ParentTableName"].casefold() == object_name
        ) or (
            relation["ChildSchemaName"].casefold() == schema_name and relation["ChildTableName"].casefold() == object_name
        ))
        snapshot["Tables"].remove(table)
        _recalculate_foreign_keys(snapshot)
    elif kind == "AddField":
        table = _find_table(snapshot, operation["ObjectKey"])
        if table is None or _find_field(table, operation["FieldKey"]) is not None:
            raise IssueValidationError("新增字段目标无效或已经存在。")
        field = _new_field(operation["FieldPayload"])
        table["Fields"].append(field)
        result["NewFieldPayload"] = _field_payload(field)
    elif kind == "RemoveField":
        table = _find_table(snapshot, operation["ObjectKey"])
        field = _find_field(table, operation["FieldKey"]) if table else None
        if field is None:
            raise IssueValidationError("删除字段不存在。")
        result["OldFieldPayload"] = _field_payload(field)
        endpoint = (table["SchemaName"].casefold(), table["ObjectName"].casefold(), field["FieldName"].casefold())
        _remove_relations(snapshot, lambda relation: (
            relation["ParentSchemaName"].casefold(), relation["ParentTableName"].casefold(), relation["ParentFieldName"].casefold()
        ) == endpoint or (
            relation["ChildSchemaName"].casefold(), relation["ChildTableName"].casefold(), relation["ChildFieldName"].casefold()
        ) == endpoint)
        table["Fields"].remove(field)
        _recalculate_foreign_keys(snapshot)
    elif kind == "AddRelation":
        payload = operation["RelationPayload"]
        parent = _find_table(snapshot, payload["ParentSchemaName"] + "." + payload["ParentTableName"])
        child = _find_table(snapshot, payload["ChildSchemaName"] + "." + payload["ChildTableName"])
        if parent is None or child is None or _find_field(parent, payload["ParentFieldName"]) is None or _find_field(child, payload["ChildFieldName"]) is None:
            raise IssueValidationError("关系端点不存在。")
        if _find_relation(snapshot, payload) is not None:
            raise IssueValidationError("关系已经存在。")
        relation = _new_relation(payload)
        parent["Relations"].append(copy.deepcopy(relation))
        if parent is not child:
            child["Relations"].append(copy.deepcopy(relation))
        _find_field(child, payload["ChildFieldName"])["IsForeignKey"] = True
        result["NewRelationPayload"] = copy.deepcopy(payload)
    else:
        payload = operation["RelationPayload"]
        existing = _find_relation(snapshot, payload)
        if existing is None:
            raise IssueValidationError("删除关系不存在。")
        result["OldRelationPayload"] = _relation_payload(existing)
        identity = _relation_identity(payload)
        _remove_relations(snapshot, lambda relation: _relation_identity(relation) == identity)
        _recalculate_foreign_keys(snapshot)
    return result


def _validate_final_field_structures(snapshot, touched_fields):
    """XMZADD 20260901 在单个 Issue 应用结束后校验被修改字段的最终类型与长度组合。"""
    for touched_field in touched_fields:
        is_present = False
        for table in snapshot["Tables"]:
            for field in table.get("Fields", []):
                if field is touched_field:
                    is_present = True
                    break
            if is_present:
                break
        if not is_present:
            continue
        data_type = _normalize_sql_type(touched_field.get("DataType"))
        length_text = _normalize_length(touched_field.get("LengthText"))
        _validate_length_for_type(data_type, length_text)


def _event_relative_path(issue_time, author, operation_id):
    """XMZADD 20260901 由 API 时间、数字作者和安全操作号构造受控不可变事件相对路径。"""
    relative = PurePosixPath(
        "events", issue_time.strftime("%Y"), issue_time.strftime("%m"), issue_time.strftime("%d"),
        author, operation_id + ".json"
    )
    if ".." in relative.parts or relative.is_absolute():
        raise IssueValidationError("事件路径越界。")
    return relative.as_posix()


def _create_event(snapshot, operation, issue_time_text):
    """XMZADD 20260901 应用单个规范操作并创建包含真实前后值的不可变审计事件。"""
    event = {
        "OperationId": operation["OperationId"],
        "AuthorGitHubUserId": operation["AuthorGitHubUserId"],
        "ObjectKey": operation["ObjectKey"],
        "FieldKey": operation.get("FieldKey") or "",
        "PropertyName": operation.get("PropertyName") or "",
        "ChangeKind": operation["ChangeKind"],
        "OldValue": None,
        "NewValue": None,
        "Revision": 0,
        "AppliedAtUtc": issue_time_text,
        "OldTablePayload": None,
        "NewTablePayload": None,
        "OldFieldPayload": None,
        "NewFieldPayload": None,
        "OldRelationPayload": None,
        "NewRelationPayload": None,
        "Evidence": copy.deepcopy(operation.get("Evidence") or []),
    }
    if operation["ChangeKind"] == "Set":
        event["OldValue"], event["NewValue"] = _apply_set(snapshot, operation)
    else:
        event.update(_apply_structure(snapshot, operation))
    return event


def _sort_snapshot(snapshot):
    """XMZADD 20260901 按稳定键排列快照集合，保证输入顺序不影响压缩内容哈希。"""
    snapshot["Tables"].sort(key=lambda table: (
        table.get("SchemaName", "").casefold(), table.get("SchemaName", ""),
        table.get("ObjectName", "").casefold(), table.get("ObjectName", "")
    ))
    for table in snapshot["Tables"]:
        table["Fields"].sort(key=lambda field: (field.get("FieldName", "").casefold(), field.get("FieldName", "")))
        table["Relations"].sort(key=lambda relation: (_relation_identity(relation), _canonical_json_bytes(relation)))


def _atomic_write(path, content):
    """XMZADD 20260901 在目标目录同卷生成临时文件再替换，避免客户端读取半写内容。"""
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=".dictionary-", dir=str(path.parent))
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_name, path)
    except BaseException:
        try:
            os.unlink(temporary_name)
        except OSError:
            pass
        raise


def _publish_batch(repo_dir, snapshot, pending_events, event_paths, generated_at):
    """XMZADD 20260901 先发布事件和快照并最后替换 manifest，使清单成为整批可见性边界。"""
    revision = snapshot["Revision"]
    for event, relative_path in zip(pending_events, event_paths):
        _atomic_write(repo_dir / Path(relative_path), _canonical_json_bytes(event) + b"\n")
    revision_document = {
        "FormatVersion": 1,
        "Revision": revision,
        "GeneratedAtUtc": generated_at,
        "EventPaths": event_paths,
    }
    revision_path = repo_dir / "events" / "revisions" / ("%09d.json" % revision)
    snapshot_content = _deterministic_gzip(_canonical_json_bytes(snapshot))
    snapshot_sha256 = hashlib.sha256(snapshot_content).hexdigest()
    snapshot_relative_path = "snapshot/revisions/%09d-%s.json.gz" % (revision, snapshot_sha256)
    _atomic_write(revision_path, _canonical_json_bytes(revision_document) + b"\n")
    # 每个规范快照使用不可变版本路径，最终清单切换失败时旧清单引用的旧内容仍保持有效。
    _atomic_write(repo_dir / Path(snapshot_relative_path), snapshot_content)
    manifest = {
        "FormatVersion": 1,
        "Revision": revision,
        "SnapshotSha256": snapshot_sha256,
        "SnapshotPath": snapshot_relative_path,
        "GeneratedAtUtc": generated_at,
        "LastEventPath": event_paths[-1],
    }
    _atomic_write(repo_dir / "snapshot" / "manifest.json", _canonical_json_bytes(manifest) + b"\n")


def process_issues(repo_dir, issues, publishers):
    """XMZADD 20260901 按 Issue 编号处理离线 API 数据并保留关闭前版本依据，且不访问网络或数据库。"""
    repo_dir = Path(repo_dir).resolve()
    if not isinstance(issues, list):
        raise ValueError("issues 必须是列表。")
    publisher_ids = _normalize_publishers(publishers)
    snapshot = _load_snapshot(repo_dir)
    applied_ids = _load_applied_operation_ids(repo_dir)
    closed_issue_numbers = []
    failed_issues = {}
    pending_events = []
    event_paths = []
    processed_issue_versions = {}
    last_event_time = None
    try:
        ordered_issues = sorted(issues, key=lambda issue: issue["number"])
    except (KeyError, TypeError) as exception:
        raise ValueError("Issue number 无法排序。") from exception
    for issue in ordered_issues:
        issue_number = issue.get("number") if isinstance(issue, dict) else None
        try:
            if isinstance(issue_number, bool) or not isinstance(issue_number, int) or issue_number <= 0:
                raise IssueValidationError("Issue number 格式无效。")
            if not isinstance(issue.get("title"), str) or not issue["title"].startswith(TITLE_PREFIX):
                raise IssueValidationError("Issue 标题前缀无效。")
            trusted_author = _normalize_numeric_user_id(issue.get("user", {}).get("id"))
            issue_time, issue_time_text = _normalize_issue_time(issue.get("created_at"))
            issue_body = issue.get("body")
            _normalize_issue_time(issue.get("updated_at"))
            batch = _parse_issue_body(issue_body)
            normalized_batch = _normalize_batch(batch, trusted_author, publisher_ids)
            issue_snapshot = copy.deepcopy(snapshot)
            issue_events = []
            issue_paths = []
            issue_new_ids = set()
            touched_fields = []
            for operation in normalized_batch["Operations"]:
                operation_id = operation["OperationId"]
                if operation_id in applied_ids:
                    continue
                relative_path = _event_relative_path(issue_time, trusted_author, operation_id)
                target = repo_dir / Path(relative_path)
                if target.exists():
                    raise IssueValidationError("事件路径已存在但 OperationId 未登记。")
                event = _create_event(issue_snapshot, operation, issue_time_text)
                if operation["ChangeKind"] == "Set" and operation["PropertyName"] in {"DataType", "LengthText"}:
                    table = _find_table(issue_snapshot, operation["ObjectKey"])
                    field = _find_field(table, operation["FieldKey"]) if table else None
                    if field is not None and all(existing is not field for existing in touched_fields):
                        touched_fields.append(field)
                issue_events.append(event)
                issue_paths.append(relative_path)
                issue_new_ids.add(operation_id)
            _validate_final_field_structures(issue_snapshot, touched_fields)
            snapshot = issue_snapshot
            pending_events.extend(issue_events)
            event_paths.extend(issue_paths)
            applied_ids.update(issue_new_ids)
            closed_issue_numbers.append(issue_number)
            processed_issue_versions[issue_number] = {
                "updatedAt": issue["updated_at"],
                "bodySha256": hashlib.sha256(issue_body.encode("utf-8")).hexdigest(),
            }
            if issue_events:
                last_event_time = issue_time_text
        except (IssueValidationError, KeyError, TypeError, ValueError, json.JSONDecodeError) as exception:
            failed_issues[issue_number] = str(exception)
    if pending_events:
        revision = snapshot["Revision"] + 1
        snapshot["Revision"] = revision
        snapshot["RefreshedAt"] = last_event_time
        _sort_snapshot(snapshot)
        for event in pending_events:
            event["Revision"] = revision
        _publish_batch(repo_dir, snapshot, pending_events, event_paths, last_event_time)
    return ProcessResult(
        snapshot=copy.deepcopy(snapshot),
        closed_issue_numbers=closed_issue_numbers,
        failed_issues=failed_issues,
        event_paths=event_paths,
        revision=snapshot["Revision"],
        processed_issue_versions=copy.deepcopy(processed_issue_versions),
    )


def _load_publishers_file(repo_dir):
    """XMZADD 20260901 从仓库公开配置读取数字发布者 ID，不接受环境凭据或登录名替代。"""
    path = repo_dir / "config" / "publishers.json"
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or set(value) != {"githubUserIds"}:
        raise ValueError("publishers.json 格式无效。")
    return value["githubUserIds"]


def main(argv=None):
    """XMZADD 20260901 为 GitHub Actions 提供离线文件入口并输出延后关闭 Issue 所需结果。"""
    parser = argparse.ArgumentParser(description="Process validated dictionary event issues.")
    parser.add_argument("--repo-dir", default=".")
    parser.add_argument("--issues-file", required=True)
    parser.add_argument("--result-file", required=True)
    arguments = parser.parse_args(argv)
    repo_dir = Path(arguments.repo_dir).resolve()
    issues = json.loads(Path(arguments.issues_file).read_text(encoding="utf-8"))
    publishers = _load_publishers_file(repo_dir)
    result = process_issues(repo_dir, issues, publishers)
    output = {
        "closedIssueNumbers": result.closed_issue_numbers,
        "failedIssues": {str(key): value for key, value in result.failed_issues.items()},
        "eventPaths": result.event_paths,
        "revision": result.revision,
        "processedIssueVersions": {
            str(key): value for key, value in result.processed_issue_versions.items()
        },
    }
    _atomic_write(Path(arguments.result_file), _canonical_json_bytes(output) + b"\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
