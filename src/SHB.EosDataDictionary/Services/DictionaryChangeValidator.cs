using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 描述公开字典事件的安全校验失败位置和固定错误代码。</summary>
    public sealed class DictionaryChangeValidationError
    {
        public string Code { get; set; }
        public int OperationIndex { get; set; }
        public string Message { get; set; }
    }

    /// <summary>XMZADD 20260901 返回完整校验错误或已绑定可信 GitHub 作者的独立规范批次。</summary>
    public sealed class DictionaryChangeValidationResult
    {
        /// <summary>XMZADD 20260901 固化校验错误和规范批次，避免调用方绕过可信作者绑定结果。</summary>
        internal DictionaryChangeValidationResult(IList<DictionaryChangeValidationError> errors, DictionaryChangeBatch normalizedBatch)
        {
            Errors = new ReadOnlyCollection<DictionaryChangeValidationError>(errors);
            NormalizedBatch = normalizedBatch;
        }

        public bool IsValid { get { return Errors.Count == 0; } }
        public IList<DictionaryChangeValidationError> Errors { get; private set; }
        public DictionaryChangeBatch NormalizedBatch { get; private set; }
    }

    /// <summary>XMZADD 20260901 校验 GitHub Issue 字典事件白名单、可信作者、载荷组合和公开内容边界。</summary>
    public sealed class DictionaryChangeValidator
    {
        public const int MaximumOperationCount = 100;
        public const int MaximumTextLength = 2000;
        public const int MaximumIssueBodyBytes = 60 * 1024;
        public const int MaximumPayloadBytes = MaximumIssueBodyBytes - 15;
        private const int MaximumIdentifierLength = 256;
        private const int MaximumAuthorLength = 32;
        private const int MaximumEvidenceCount = 5;
        private const int MaximumEvidencePathLength = 512;
        private const int MaximumEvidenceSummaryLength = 240;

        /// <summary>XMZADD 20260901 用 Issue API 作者身份生成规范副本并完整报告所有可审阅的安全错误。</summary>
        public DictionaryChangeValidationResult ValidateAndNormalize(
            DictionaryChangeBatch batch,
            string trustedGitHubUserId,
            IList<string> publisherGitHubUserIds)
        {
            return ValidateAndNormalizeCore(batch, trustedGitHubUserId, publisherGitHubUserIds, true, false);
        }

        /// <summary>XMZADD 20260901 先限制远程原始 UTF-8 字节，再严格拒绝未知成员和本地覆盖后生成可信批次。</summary>
        public DictionaryChangeValidationResult ValidateAndNormalizeJson(
            byte[] utf8Json,
            string trustedGitHubUserId,
            IList<string> publisherGitHubUserIds)
        {
            var errors = new List<DictionaryChangeValidationError>();
            if (utf8Json == null || utf8Json.Length == 0)
            {
                AddError(errors, "PAYLOAD_INVALID", -1, "远程批次 JSON 为空或格式无效。");
                return new DictionaryChangeValidationResult(errors, null);
            }
            if (utf8Json.Length > MaximumPayloadBytes)
            {
                AddError(errors, "PAYLOAD_TOO_LARGE", -1, "批次 JSON 字节数超过限制。");
                return new DictionaryChangeValidationResult(errors, null);
            }

            DictionaryChangeBatch batch;
            try
            {
                batch = DictionaryJsonSerializer.DeserializeRemoteBatch(utf8Json);
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                AddError(errors, "PAYLOAD_INVALID", -1, "远程批次 JSON 无法解析。");
                return new DictionaryChangeValidationResult(errors, null);
            }

            if (batch.Operations != null && batch.Operations.Count > MaximumOperationCount)
            {
                // 数量越界后不再查看任何嵌套载荷，避免恶意大批次放大递归校验成本。
                var limitErrors = new List<DictionaryChangeValidationError>();
                AddError(limitErrors, "OPERATION_LIMIT_EXCEEDED", -1, "单批操作数量超过限制。");
                return new DictionaryChangeValidationResult(limitErrors, null);
            }
            if (DictionaryJsonSerializer.ContainsUnknownRemoteMembers(batch))
            {
                AddError(errors, "UNKNOWN_MEMBER", -1, "远程批次或结构载荷包含未知成员。");
            }
            if (batch.Overrides != null && batch.Overrides.Count > 0)
            {
                AddError(errors, "OVERRIDES_NOT_ALLOWED", -1, "远程事件不得携带本地覆盖记录。");
            }
            if (errors.Count > 0)
            {
                return new DictionaryChangeValidationResult(errors, null);
            }
            return ValidateAndNormalizeCore(batch, trustedGitHubUserId, publisherGitHubUserIds, false, true);
        }

        /// <summary>XMZADD 20260901 共用对象与远程 JSON 校验流程并按来源控制字节和覆盖记录规则。</summary>
        private DictionaryChangeValidationResult ValidateAndNormalizeCore(
            DictionaryChangeBatch batch,
            string trustedGitHubUserId,
            IList<string> publisherGitHubUserIds,
            bool validateSerializedSize,
            bool rejectOverrides)
        {
            var errors = new List<DictionaryChangeValidationError>();
            if (batch == null)
            {
                AddError(errors, "BATCH_INVALID", -1, "修改批次为空。");
                return new DictionaryChangeValidationResult(errors, null);
            }

            ValidateTrustedAuthor(trustedGitHubUserId, errors);
            ValidateBatchText(batch, errors);
            bool isPublisher = IsPublisher(trustedGitHubUserId, publisherGitHubUserIds);
            var normalized = new DictionaryChangeBatch
            {
                BatchId = batch.BatchId,
                AuthorGitHubUserId = IsValidTrustedAuthor(trustedGitHubUserId) ? trustedGitHubUserId : string.Empty,
                CreatedAtUtc = batch.CreatedAtUtc
            };

            if (!IsSafeOperationId(batch.BatchId))
            {
                AddError(errors, "BATCH_ID_INVALID", -1, "批次编号格式无效。");
            }

            IList<DictionaryChangeOperation> operations = batch.Operations;
            if (operations == null)
            {
                AddError(errors, "OPERATIONS_INVALID", -1, "操作集合为空。");
                return new DictionaryChangeValidationResult(errors, null);
            }
            if (operations.Count == 0)
            {
                AddError(errors, "OPERATIONS_INVALID", -1, "操作集合不包含事件。");
            }
            if (operations.Count > MaximumOperationCount)
            {
                AddError(errors, "OPERATION_LIMIT_EXCEEDED", -1, "单批操作数量超过限制。");
                return new DictionaryChangeValidationResult(errors, null);
            }
            if (rejectOverrides && batch.Overrides != null && batch.Overrides.Count > 0)
            {
                AddError(errors, "OVERRIDES_NOT_ALLOWED", -1, "远程事件不得携带本地覆盖记录。");
            }

            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < operations.Count; index++)
            {
                DictionaryChangeOperation operation = operations[index];
                if (operation == null)
                {
                    AddError(errors, "OPERATION_NULL", index, "操作项为空。");
                    continue;
                }

                int errorCountBefore = errors.Count;
                ValidateOperation(operation, index, isPublisher, operationIds, errors);
                if (errors.Count == errorCountBefore)
                {
                    DictionaryChangeOperation clone = CloneOperation(operation, trustedGitHubUserId);
                    NormalizeOperationValues(clone);
                    normalized.Operations.Add(clone);
                }
            }

            if (validateSerializedSize)
            {
                ValidatePayloadByteSize(batch, errors);
            }
            return new DictionaryChangeValidationResult(errors, errors.Count == 0 ? normalized : null);
        }

        /// <summary>XMZADD 20260901 校验单个事件的稳定键、白名单、类型化载荷和结构发布资格。</summary>
        private static void ValidateOperation(
            DictionaryChangeOperation operation,
            int operationIndex,
            bool isPublisher,
            ISet<string> operationIds,
            IList<DictionaryChangeValidationError> errors)
        {
            if (!IsSafeOperationId(operation.OperationId))
            {
                AddError(errors, "OPERATION_ID_INVALID", operationIndex, "操作编号格式无效。");
            }
            else if (!operationIds.Add(operation.OperationId))
            {
                AddError(errors, "OPERATION_ID_DUPLICATE", operationIndex, "批次内操作编号重复。");
            }

            if (!IsSafeObjectKey(operation.ObjectKey))
            {
                AddError(errors, "OBJECT_KEY_INVALID", operationIndex, "对象稳定键格式无效。");
            }
            if (!string.IsNullOrEmpty(operation.FieldKey) && !IsSafeIdentifier(operation.FieldKey))
            {
                AddError(errors, "FIELD_KEY_INVALID", operationIndex, "字段稳定键格式无效。");
            }
            ValidateOperationText(operation, operationIndex, errors);
            ValidatePublishedEvidence(operation, operationIndex, isPublisher, errors);

            string kind = operation.ChangeKind ?? string.Empty;
            if (string.Equals(kind, "Set", StringComparison.Ordinal))
            {
                ValidateSetOperation(operation, operationIndex, isPublisher, errors);
                return;
            }
            if (string.Equals(kind, "AddTable", StringComparison.Ordinal) ||
                string.Equals(kind, "RemoveTable", StringComparison.Ordinal) ||
                string.Equals(kind, "AddField", StringComparison.Ordinal) ||
                string.Equals(kind, "RemoveField", StringComparison.Ordinal) ||
                string.Equals(kind, "AddRelation", StringComparison.Ordinal) ||
                string.Equals(kind, "RemoveRelation", StringComparison.Ordinal))
            {
                if (!isPublisher)
                {
                    AddError(errors, "STRUCTURE_PERMISSION_REQUIRED", operationIndex, "结构事件仅允许结构发布者提交。");
                }
                ValidateStructureOperation(operation, operationIndex, errors);
                return;
            }

            AddError(errors, "CHANGE_KIND_INVALID", operationIndex, "事件类型不在固定白名单中。");
        }

        /// <summary>XMZADD 20260901 仅允许结构发布者提交固定公开字段组成的相对路径短证据。</summary>
        private static void ValidatePublishedEvidence(
            DictionaryChangeOperation operation,
            int operationIndex,
            bool isPublisher,
            IList<DictionaryChangeValidationError> errors)
        {
            IList<EvidenceItem> evidence = operation.Evidence;
            if (evidence == null || evidence.Count == 0)
            {
                return;
            }
            if (!isPublisher)
            {
                AddError(errors, "EVIDENCE_PERMISSION_REQUIRED", operationIndex, "公开来源证据仅允许结构发布者提交。");
                return;
            }
            if (evidence.Count > MaximumEvidenceCount)
            {
                AddError(errors, "EVIDENCE_INVALID", operationIndex, "公开证据数量超过限制。");
                return;
            }
            for (int index = 0; index < evidence.Count; index++)
            {
                if (!IsPublishedEvidenceItemSafe(evidence[index]))
                {
                    AddError(errors, "EVIDENCE_INVALID", operationIndex, "公开证据字段无效。");
                    return;
                }
            }
        }

        /// <summary>XMZADD 20260901 校验证据只含相对路径、行号、规则和短摘要并拒绝凭据形态。</summary>
        private static bool IsPublishedEvidenceItemSafe(EvidenceItem item)
        {
            if (item == null || item.ExtensionData != null || item.RawValue != null || item.OriginalText != null ||
                !IsEvidenceTextSafe(item.SourceType, 32) || !IsEvidenceTextSafe(item.SourcePath, MaximumEvidencePathLength) ||
                !IsEvidenceTextSafe(item.RuleName, 64) || !IsEvidenceTextSafe(item.Explanation, MaximumEvidenceSummaryLength) ||
                item.SourceLine < 0 || item.SourceLine > 10000000 || !IsSafeEvidencePath(item.SourcePath))
            {
                return false;
            }
            string combined = (item.SourceType ?? string.Empty) + " " + (item.SourcePath ?? string.Empty) + " " +
                (item.RuleName ?? string.Empty) + " " + (item.Explanation ?? string.Empty);
            return !ContainsCredentialShape(combined);
        }

        /// <summary>XMZADD 20260901 限制证据短文本长度和控制字符，避免摘要承载源码正文。</summary>
        private static bool IsEvidenceTextSafe(string value, int maximumLength)
        {
            if (value == null || value.Length > maximumLength)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                UnicodeCategory category = char.GetUnicodeCategory(value, index);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 只接受规范正斜线相对路径并拒绝盘符、父目录和路径前缀绕过。</summary>
        private static bool IsSafeEvidencePath(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }
            if (Path.IsPathRooted(value) || value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0 ||
                value.StartsWith("/", StringComparison.Ordinal) || value.EndsWith("/", StringComparison.Ordinal))
            {
                return false;
            }
            string[] parts = value.Split('/');
            for (int index = 0; index < parts.Length; index++)
            {
                if (parts[index].Length == 0 || parts[index] == "." || parts[index] == "..")
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 识别常见密码、Token 和连接字符串标记而不回显原文。</summary>
        private static bool ContainsCredentialShape(string value)
        {
            return SensitiveTextGuard.ContainsCredentialShape(value);
        }

        /// <summary>XMZADD 20260901 校验普通业务赋值与 publisher 物理赋值的属性作用域和规范文本。</summary>
        private static void ValidateSetOperation(
            DictionaryChangeOperation operation,
            int operationIndex,
            bool isPublisher,
            IList<DictionaryChangeValidationError> errors)
        {
            if (operation.NewValue == null)
            {
                AddError(errors, "NEW_VALUE_REQUIRED", operationIndex, "赋值事件必须显式提供新值。");
            }
            else if (ContainsCredentialShape(operation.NewValue))
            {
                AddError(errors, "PUBLIC_CONTENT_CREDENTIAL", operationIndex, "公开赋值包含凭据形态。");
            }
            if (operation.TablePayload != null || operation.FieldPayload != null || operation.RelationPayload != null)
            {
                AddError(errors, "PAYLOAD_INVALID", operationIndex, "赋值事件不能携带结构实体载荷。");
            }

            bool isField = !string.IsNullOrEmpty(operation.FieldKey);
            bool ordinaryTable = IsOrdinaryTableProperty(operation.PropertyName);
            bool ordinaryField = IsOrdinaryFieldProperty(operation.PropertyName);
            bool structureTable = IsStructureTableProperty(operation.PropertyName);
            bool structureField = IsStructureFieldProperty(operation.PropertyName);
            if (!ordinaryTable && !ordinaryField && !structureTable && !structureField)
            {
                AddError(errors, "PROPERTY_NOT_ALLOWED", operationIndex, "属性不在公开字典白名单中。");
                return;
            }

            bool allowedForScope = isField
                ? ordinaryField || structureField
                : ordinaryTable || structureTable;
            if (!allowedForScope)
            {
                AddError(errors, "PROPERTY_SCOPE_INVALID", operationIndex, "字段键与属性作用域不一致。");
            }
            bool isStructureForScope = isField ? structureField : structureTable;
            if (isStructureForScope && !isPublisher)
            {
                AddError(errors, "STRUCTURE_PERMISSION_REQUIRED", operationIndex, "物理属性仅允许结构发布者提交。");
            }

            if (string.Equals(operation.PropertyName, "Category", StringComparison.Ordinal) && !IsPublishedCategory(operation.NewValue))
            {
                AddError(errors, "CATEGORY_INVALID", operationIndex, "表分类不是允许发布的规范名称。");
            }
            if (IsBooleanProperty(operation.PropertyName) && !IsCanonicalBoolean(operation.NewValue))
            {
                AddError(errors, "BOOLEAN_INVALID", operationIndex, "布尔属性必须使用规范值 True 或 False。");
            }
            if (string.Equals(operation.PropertyName, "ApproximateRowCount", StringComparison.Ordinal))
            {
                long rowCount;
                if (!long.TryParse(operation.NewValue, NumberStyles.None, CultureInfo.InvariantCulture, out rowCount) || rowCount < 0L)
                {
                    AddError(errors, "STRUCTURE_VALUE_INVALID", operationIndex, "近似行数必须是非负整数。");
                }
            }
            if (string.Equals(operation.PropertyName, "ObjectType", StringComparison.Ordinal) &&
                !string.Equals(operation.NewValue, "TABLE", StringComparison.Ordinal))
            {
                AddError(errors, "STRUCTURE_VALUE_INVALID", operationIndex, "共享快照只允许 TABLE 对象类型。");
            }
            if ((string.Equals(operation.PropertyName, "SchemaName", StringComparison.Ordinal) ||
                 string.Equals(operation.PropertyName, "ObjectName", StringComparison.Ordinal) ||
                 string.Equals(operation.PropertyName, "FieldName", StringComparison.Ordinal) ||
                 string.Equals(operation.PropertyName, "OwnerTableName", StringComparison.Ordinal)) &&
                !IsSafeIdentifier(operation.NewValue))
            {
                AddError(errors, "STRUCTURE_VALUE_INVALID", operationIndex, "结构标识符格式无效。");
            }
            string normalizedDataType;
            if (string.Equals(operation.PropertyName, "DataType", StringComparison.Ordinal) &&
                !TryNormalizeSqlServerType(operation.NewValue, out normalizedDataType))
            {
                AddError(errors, "STRUCTURE_VALUE_INVALID", operationIndex, "字段类型不在 SQL Server 系统类型白名单中。");
            }
            string normalizedLength;
            if (string.Equals(operation.PropertyName, "LengthText", StringComparison.Ordinal) &&
                !TryNormalizeLengthText(operation.NewValue, out normalizedLength))
            {
                AddError(errors, "STRUCTURE_VALUE_INVALID", operationIndex, "字段长度格式无效。");
            }
        }

        /// <summary>XMZADD 20260901 校验结构增删事件只携带对应实体且稳定键与实体端点完全一致。</summary>
        private static void ValidateStructureOperation(
            DictionaryChangeOperation operation,
            int operationIndex,
            IList<DictionaryChangeValidationError> errors)
        {
            bool payloadValid;
            if (string.Equals(operation.ChangeKind, "AddTable", StringComparison.Ordinal))
            {
                payloadValid = IsAddTablePayloadValid(operation);
            }
            else if (string.Equals(operation.ChangeKind, "RemoveTable", StringComparison.Ordinal))
            {
                payloadValid = string.IsNullOrEmpty(operation.FieldKey) && string.IsNullOrEmpty(operation.PropertyName) &&
                    operation.TablePayload == null && operation.FieldPayload == null && operation.RelationPayload == null;
            }
            else if (string.Equals(operation.ChangeKind, "AddField", StringComparison.Ordinal))
            {
                payloadValid = IsAddFieldPayloadValid(operation);
                if (operation.FieldPayload != null && !IsFieldStructureValueValid(operation.FieldPayload))
                {
                    AddError(errors, "STRUCTURE_VALUE_INVALID", operationIndex, "字段类型或长度不符合 SQL Server 结构规范。");
                }
            }
            else if (string.Equals(operation.ChangeKind, "RemoveField", StringComparison.Ordinal))
            {
                payloadValid = !string.IsNullOrEmpty(operation.FieldKey) && string.IsNullOrEmpty(operation.PropertyName) &&
                    operation.TablePayload == null && operation.FieldPayload == null && operation.RelationPayload == null;
            }
            else
            {
                payloadValid = IsRelationPayloadValid(operation);
            }

            if (!payloadValid)
            {
                AddError(errors, "PAYLOAD_INVALID", operationIndex, "结构载荷与事件类型或稳定键不一致。");
            }
        }

        /// <summary>XMZADD 20260901 判断新增表载荷只含单表物理事实并要求后续事件独立补充字段关系。</summary>
        private static bool IsAddTablePayloadValid(DictionaryChangeOperation operation)
        {
            TableStructurePayload payload = operation.TablePayload;
            if (payload == null || operation.FieldPayload != null || operation.RelationPayload != null ||
                !string.IsNullOrEmpty(operation.FieldKey) || !string.IsNullOrEmpty(operation.PropertyName) ||
                !IsSafeIdentifier(payload.SchemaName) || !IsSafeIdentifier(payload.ObjectName) ||
                !string.Equals(payload.ObjectType, "TABLE", StringComparison.Ordinal) || payload.ApproximateRowCount < 0L ||
                payload.Fields == null || payload.Relations == null || payload.Fields.Count != 0 || payload.Relations.Count != 0)
            {
                return false;
            }

            return string.Equals(operation.ObjectKey, payload.SchemaName + "." + payload.ObjectName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260901 判断新增字段载荷的字段名和所属表与稳定定位完全一致。</summary>
        private static bool IsAddFieldPayloadValid(DictionaryChangeOperation operation)
        {
            FieldStructurePayload payload = operation.FieldPayload;
            string schemaName;
            string objectName;
            if (payload == null || operation.TablePayload != null || operation.RelationPayload != null ||
                string.IsNullOrEmpty(operation.FieldKey) || !string.IsNullOrEmpty(operation.PropertyName) ||
                !TrySplitObjectKey(operation.ObjectKey, out schemaName, out objectName) ||
                !IsSafeIdentifier(payload.FieldName) || !IsSafeIdentifier(payload.OwnerTableName) ||
                !IsFieldStructureValueValid(payload))
            {
                return false;
            }

            return string.Equals(operation.FieldKey, payload.FieldName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(objectName, payload.OwnerTableName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260901 共用字段类型和长度规则，保证新增载荷不会携带任意路径或伪结构文本。</summary>
        private static bool IsFieldStructureValueValid(FieldStructurePayload payload)
        {
            string dataType;
            string lengthText;
            return payload != null && TryNormalizeSqlServerType(payload.DataType, out dataType) &&
                TryNormalizeLengthText(payload.LengthText, out lengthText) &&
                IsLengthValidForDataType(dataType, lengthText);
        }

        /// <summary>XMZADD 20260901 判断关系载荷以外键名和完整父子端点形成唯一稳定身份。</summary>
        private static bool IsRelationPayloadValid(DictionaryChangeOperation operation)
        {
            RelationStructurePayload payload = operation.RelationPayload;
            if (payload == null || operation.TablePayload != null || operation.FieldPayload != null ||
                !string.IsNullOrEmpty(operation.FieldKey) || !string.IsNullOrEmpty(operation.PropertyName) ||
                !IsSafeIdentifier(payload.ForeignKeyName) || !IsSafeIdentifier(payload.ParentSchemaName) ||
                !IsSafeIdentifier(payload.ParentTableName) || !IsSafeIdentifier(payload.ParentFieldName) ||
                !IsSafeIdentifier(payload.ChildSchemaName) || !IsSafeIdentifier(payload.ChildTableName) ||
                !IsSafeIdentifier(payload.ChildFieldName))
            {
                return false;
            }

            return string.Equals(operation.ObjectKey,
                payload.ChildSchemaName + "." + payload.ChildTableName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260901 校验批次正文作者等未采用字段仍不能利用超长或控制字符污染公共事件。</summary>
        private static void ValidateBatchText(DictionaryChangeBatch batch, IList<DictionaryChangeValidationError> errors)
        {
            if (batch.BatchId != null && batch.BatchId.Length > MaximumTextLength)
            {
                AddError(errors, "TEXT_TOO_LONG", -1, "批次文本超过限制。");
            }
            if (!string.IsNullOrEmpty(batch.AuthorGitHubUserId) &&
                (batch.AuthorGitHubUserId.Length > MaximumAuthorLength || HasUnsafeControlCharacters(batch.AuthorGitHubUserId)))
            {
                AddError(errors, "AUTHOR_INVALID", -1, "正文作者字段格式无效。");
            }
        }

        /// <summary>XMZADD 20260901 校验事件所有自由字符串长度和不可见控制字符，避免日志与 JSON 解析边界被污染。</summary>
        private static void ValidateOperationText(
            DictionaryChangeOperation operation,
            int operationIndex,
            IList<DictionaryChangeValidationError> errors)
        {
            string[] values =
            {
                operation.OperationId, operation.ObjectKey, operation.FieldKey, operation.PropertyName,
                operation.OldValue, operation.NewValue, operation.ChangeKind
            };
            for (int index = 0; index < values.Length; index++)
            {
                if (values[index] != null && (values[index].Length > MaximumTextLength || HasUnsafeControlCharacters(values[index])))
                {
                    AddError(errors, "TEXT_TOO_LONG", operationIndex, "事件文本超过长度或字符边界。");
                    break;
                }
            }
            if (!string.IsNullOrEmpty(operation.AuthorGitHubUserId) &&
                (operation.AuthorGitHubUserId.Length > MaximumAuthorLength || HasUnsafeControlCharacters(operation.AuthorGitHubUserId)))
            {
                AddError(errors, "AUTHOR_INVALID", operationIndex, "正文作者字段格式无效。");
            }
            if (!ArePayloadTextsSafe(operation))
            {
                AddError(errors, "TEXT_TOO_LONG", operationIndex, "结构载荷文本超过长度或字符边界。");
            }
        }

        /// <summary>XMZADD 20260901 校验结构载荷全部文本字段但不把原值写入错误信息。</summary>
        private static bool ArePayloadTextsSafe(DictionaryChangeOperation operation)
        {
            if (operation.TablePayload != null &&
                (!IsBoundedText(operation.TablePayload.SchemaName) || !IsBoundedText(operation.TablePayload.ObjectName) ||
                 !IsBoundedText(operation.TablePayload.ObjectType)))
            {
                return false;
            }
            if (operation.FieldPayload != null &&
                (!IsBoundedText(operation.FieldPayload.FieldName) || !IsBoundedText(operation.FieldPayload.OwnerTableName) ||
                 !IsBoundedText(operation.FieldPayload.DataType) || !IsBoundedText(operation.FieldPayload.LengthText)))
            {
                return false;
            }
            if (operation.RelationPayload != null)
            {
                string[] values =
                {
                    operation.RelationPayload.ForeignKeyName, operation.RelationPayload.ParentSchemaName,
                    operation.RelationPayload.ParentTableName, operation.RelationPayload.ParentFieldName,
                    operation.RelationPayload.ChildSchemaName, operation.RelationPayload.ChildTableName,
                    operation.RelationPayload.ChildFieldName
                };
                for (int index = 0; index < values.Length; index++)
                {
                    if (!IsBoundedText(values[index]))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 按 UTF-8 实际 JSON 字节数限制单个公开 Issue 载荷。</summary>
        private static void ValidatePayloadByteSize(DictionaryChangeBatch batch, IList<DictionaryChangeValidationError> errors)
        {
            try
            {
                string json = DictionaryJsonSerializer.SerializeBatch(batch);
                if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes)
                {
                    AddError(errors, "PAYLOAD_TOO_LARGE", -1, "批次 JSON 字节数超过限制。");
                }
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                AddError(errors, "PAYLOAD_INVALID", -1, "批次无法转换为规范 JSON。");
            }
        }

        /// <summary>XMZADD 20260901 校验可信作者必须是 GitHub API 返回的正整数不可变用户 ID。</summary>
        private static void ValidateTrustedAuthor(string trustedGitHubUserId, IList<DictionaryChangeValidationError> errors)
        {
            if (!IsValidTrustedAuthor(trustedGitHubUserId))
            {
                AddError(errors, "AUTHOR_INVALID", -1, "可信 GitHub 用户 ID 格式无效。");
            }
        }

        /// <summary>XMZADD 20260901 使用 Ordinal 精确比较不可变数字用户 ID，避免登录名大小写或别名参与授权。</summary>
        private static bool IsPublisher(string trustedGitHubUserId, IList<string> publisherGitHubUserIds)
        {
            if (!IsValidTrustedAuthor(trustedGitHubUserId) || publisherGitHubUserIds == null)
            {
                return false;
            }
            for (int index = 0; index < publisherGitHubUserIds.Count; index++)
            {
                if (string.Equals(trustedGitHubUserId, publisherGitHubUserIds[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 限定可信作者为有界非零 GitHub 数字用户 ID，避免登录名或异常标识参与授权。</summary>
        private static bool IsValidTrustedAuthor(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaximumAuthorLength)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }
            return value != "0";
        }

        /// <summary>XMZADD 20260901 限定普通维护者可修改的表级业务属性，隔离数据库物理结构字段。</summary>
        private static bool IsOrdinaryTableProperty(string propertyName)
        {
            return propertyName == "ChineseName" || propertyName == "BusinessMeaning" || propertyName == "ModuleName" ||
                propertyName == "EntityName" || propertyName == "Remark" || propertyName == "Category" ||
                propertyName == "KeepWhenEmpty";
        }

        /// <summary>XMZADD 20260901 限定普通维护者可修改的字段级业务属性，隔离数据库物理结构字段。</summary>
        private static bool IsOrdinaryFieldProperty(string propertyName)
        {
            return propertyName == "ChineseName" || propertyName == "BusinessMeaning" || propertyName == "Usage" ||
                propertyName == "EntityPropertyName" || propertyName == "Remark" || propertyName == "RelationSummary";
        }

        /// <summary>XMZADD 20260901 限定发布者可维护的表级物理属性，防止任意属性名进入结构事件。</summary>
        private static bool IsStructureTableProperty(string propertyName)
        {
            return propertyName == "SchemaName" || propertyName == "ObjectName" || propertyName == "ObjectType" ||
                propertyName == "ApproximateRowCount";
        }

        /// <summary>XMZADD 20260901 限定发布者可维护的字段级物理属性，防止任意属性名进入结构事件。</summary>
        private static bool IsStructureFieldProperty(string propertyName)
        {
            return propertyName == "FieldName" || propertyName == "OwnerTableName" || propertyName == "DataType" ||
                propertyName == "LengthText" || propertyName == "IsRequired" || propertyName == "IsPrimaryKey" ||
                propertyName == "IsForeignKey";
        }

        /// <summary>XMZADD 20260901 识别必须采用规范布尔文本的属性，避免按宽松文本转换结构约束。</summary>
        private static bool IsBooleanProperty(string propertyName)
        {
            return propertyName == "KeepWhenEmpty" || propertyName == "IsRequired" ||
                propertyName == "IsPrimaryKey" || propertyName == "IsForeignKey";
        }

        /// <summary>XMZADD 20260901 仅接受框架固定大小写的 True 或 False，保证跨端重放结果一致。</summary>
        private static bool IsCanonicalBoolean(string value)
        {
            return string.Equals(value, bool.TrueString, StringComparison.Ordinal) ||
                string.Equals(value, bool.FalseString, StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260901 限定可公开发布的表分类枚举文本，防止未知分类进入共享快照。</summary>
        private static bool IsPublishedCategory(string value)
        {
            return value == "Business" || value == "BaseData" || value == "Technical" || value == "Excluded";
        }

        /// <summary>XMZADD 20260901 限定全局操作号的长度和安全字符集，确保幂等键可稳定持久化和比较。</summary>
        private static bool IsSafeOperationId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                bool allowed = current >= 'a' && current <= 'z' || current >= 'A' && current <= 'Z' ||
                    current >= '0' && current <= '9' || current == '_' || current == '-';
                if (!allowed)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 校验对象稳定键必须由一个安全架构名和一个安全对象名组成。</summary>
        private static bool IsSafeObjectKey(string value)
        {
            string schemaName;
            string objectName;
            return TrySplitObjectKey(value, out schemaName, out objectName);
        }

        /// <summary>XMZADD 20260901 按唯一分隔点拆分对象稳定键，避免歧义路径定位到错误数据库对象。</summary>
        private static bool TrySplitObjectKey(string value, out string schemaName, out string objectName)
        {
            schemaName = string.Empty;
            objectName = string.Empty;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            int separator = value.IndexOf('.');
            if (separator <= 0 || separator != value.LastIndexOf('.') || separator >= value.Length - 1)
            {
                return false;
            }
            schemaName = value.Substring(0, separator);
            objectName = value.Substring(separator + 1);
            return IsSafeIdentifier(schemaName) && IsSafeIdentifier(objectName);
        }

        /// <summary>XMZADD 20260901 限定共享结构标识符为 SQL Server 常用安全字符，拒绝空白和路径符号。</summary>
        private static bool IsSafeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdentifierLength ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (!char.IsLetterOrDigit(current) && current != '_' && current != '@' && current != '$' &&
                    current != '#')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 将 SQL Server 系统字段类型规范为小写白名单值。</summary>
        internal static bool TryNormalizeSqlServerType(string value, out string normalized)
        {
            normalized = value == null ? string.Empty : value.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "bigint":
                case "binary":
                case "bit":
                case "char":
                case "date":
                case "datetime":
                case "datetime2":
                case "datetimeoffset":
                case "decimal":
                case "float":
                case "geography":
                case "geometry":
                case "hierarchyid":
                case "image":
                case "int":
                case "money":
                case "nchar":
                case "ntext":
                case "numeric":
                case "nvarchar":
                case "real":
                case "rowversion":
                case "smalldatetime":
                case "smallint":
                case "smallmoney":
                case "sql_variant":
                case "text":
                case "time":
                case "timestamp":
                case "tinyint":
                case "uniqueidentifier":
                case "varbinary":
                case "varchar":
                case "xml":
                    return true;
                default:
                    normalized = string.Empty;
                    return false;
            }
        }

        /// <summary>XMZADD 20260901 规范字段长度为 MAX、非负整数、precision,scale 或无长度破折号。</summary>
        internal static bool TryNormalizeLengthText(string value, out string normalized)
        {
            normalized = string.Empty;
            if (value == null)
            {
                return false;
            }
            if (string.Equals(value, "—", StringComparison.Ordinal))
            {
                normalized = "—";
                return true;
            }
            if (string.Equals(value, "MAX", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "MAX";
                return true;
            }
            if (value.Length == 0 || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            int separator = value.IndexOf(',');
            if (separator >= 0)
            {
                if (separator == 0 || separator != value.LastIndexOf(',') || separator == value.Length - 1)
                {
                    return false;
                }
                int precision;
                int scale;
                if (!TryParseDigits(value.Substring(0, separator), out precision) ||
                    !TryParseDigits(value.Substring(separator + 1), out scale) ||
                    precision < 1 || precision > 38 || scale < 0 || scale > precision)
                {
                    return false;
                }
                normalized = precision.ToString(CultureInfo.InvariantCulture) + "," + scale.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            int length;
            if (!TryParseDigits(value, out length) || length < 0)
            {
                return false;
            }
            normalized = length.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>XMZADD 20260901 校验规范长度形态与 SQL Server 字段类型相匹配。</summary>
        internal static bool IsLengthValidForDataType(string normalizedDataType, string normalizedLengthText)
        {
            if (normalizedDataType == "decimal" || normalizedDataType == "numeric")
            {
                return normalizedLengthText != null && normalizedLengthText.IndexOf(',') > 0;
            }
            if (normalizedDataType == "varchar" || normalizedDataType == "varbinary")
            {
                return normalizedLengthText == "MAX" || IsNormalizedIntegerInRange(normalizedLengthText, 1, 8000);
            }
            if (normalizedDataType == "nvarchar")
            {
                return normalizedLengthText == "MAX" || IsNormalizedIntegerInRange(normalizedLengthText, 1, 4000);
            }
            if (normalizedDataType == "char" || normalizedDataType == "binary")
            {
                return IsNormalizedIntegerInRange(normalizedLengthText, 1, 8000);
            }
            if (normalizedDataType == "nchar")
            {
                return IsNormalizedIntegerInRange(normalizedLengthText, 1, 4000);
            }
            return normalizedLengthText == "—";
        }

        /// <summary>XMZADD 20260901 仅解析无符号十进制数字，避免长度和精度接受符号或区域格式。</summary>
        private static bool TryParseDigits(string value, out int result)
        {
            result = 0;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
        }

        /// <summary>XMZADD 20260901 要求数值文本为无前导零的规范形式并位于结构字段允许范围内。</summary>
        private static bool IsNormalizedIntegerInRange(string value, int minimum, int maximum)
        {
            int parsed;
            return TryParseDigits(value, out parsed) &&
                string.Equals(value, parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                parsed >= minimum && parsed <= maximum;
        }

        /// <summary>XMZADD 20260901 限制公开自由文本长度并排除危险控制字符，保护日志和 JSON 边界。</summary>
        private static bool IsBoundedText(string value)
        {
            return value == null || value.Length <= MaximumTextLength && !HasUnsafeControlCharacters(value);
        }

        /// <summary>XMZADD 20260901 识别换行和制表以外的控制字符及 Unicode 隐形格式字符，避免公共事件藏入不可见内容。</summary>
        private static bool HasUnsafeControlCharacters(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                UnicodeCategory category = char.GetUnicodeCategory(value, index);
                if (category == UnicodeCategory.Format || category == UnicodeCategory.LineSeparator ||
                    category == UnicodeCategory.ParagraphSeparator ||
                    category == UnicodeCategory.Control && current != '\r' && current != '\n' && current != '\t')
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 深拷贝规范事件并只使用 GitHub Issue API 提供的可信作者。</summary>
        internal static DictionaryChangeOperation CloneOperation(DictionaryChangeOperation source, string trustedGitHubUserId)
        {
            return new DictionaryChangeOperation
            {
                OperationId = source.OperationId,
                AuthorGitHubUserId = trustedGitHubUserId,
                ObjectKey = source.ObjectKey,
                FieldKey = source.FieldKey,
                PropertyName = source.PropertyName,
                OldValue = source.OldValue,
                NewValue = source.NewValue,
                ChangeKind = source.ChangeKind,
                CreatedAtUtc = source.CreatedAtUtc,
                TablePayload = CloneTablePayload(source.TablePayload),
                FieldPayload = CloneFieldPayload(source.FieldPayload),
                RelationPayload = CloneRelationPayload(source.RelationPayload),
                Evidence = CloneEvidence(source.Evidence)
            };
        }

        /// <summary>XMZADD 20260901 深拷贝已清洗的公开证据，使发布校验不会与调用方共享可变集合。</summary>
        private static IList<EvidenceItem> CloneEvidence(IList<EvidenceItem> source)
        {
            var result = new List<EvidenceItem>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count; index++)
            {
                EvidenceItem item = source[index];
                if (item == null)
                {
                    continue;
                }
                result.Add(new EvidenceItem
                {
                    SourceType = item.SourceType,
                    SourcePath = item.SourcePath,
                    SourceLine = item.SourceLine,
                    RuleName = item.RuleName,
                    Explanation = item.Explanation
                });
            }
            return result;
        }

        /// <summary>XMZADD 20260901 将已通过校验的字段类型和长度写成唯一规范文本供应用与历史复用。</summary>
        private static void NormalizeOperationValues(DictionaryChangeOperation operation)
        {
            string normalized;
            if (operation.ChangeKind == "Set" && operation.PropertyName == "DataType" &&
                TryNormalizeSqlServerType(operation.NewValue, out normalized))
            {
                operation.NewValue = normalized;
            }
            else if (operation.ChangeKind == "Set" && operation.PropertyName == "LengthText" &&
                TryNormalizeLengthText(operation.NewValue, out normalized))
            {
                operation.NewValue = normalized;
            }
            if (operation.FieldPayload != null)
            {
                if (TryNormalizeSqlServerType(operation.FieldPayload.DataType, out normalized))
                {
                    operation.FieldPayload.DataType = normalized;
                }
                if (TryNormalizeLengthText(operation.FieldPayload.LengthText, out normalized))
                {
                    operation.FieldPayload.LengthText = normalized;
                }
            }
        }

        /// <summary>XMZADD 20260901 深拷贝表结构载荷及其子项，避免校验规范化修改调用方原始事件。</summary>
        internal static TableStructurePayload CloneTablePayload(TableStructurePayload source)
        {
            if (source == null)
            {
                return null;
            }
            var clone = new TableStructurePayload
            {
                SchemaName = source.SchemaName,
                ObjectName = source.ObjectName,
                ObjectType = source.ObjectType,
                ApproximateRowCount = source.ApproximateRowCount
            };
            if (source.Fields != null)
            {
                for (int index = 0; index < source.Fields.Count; index++)
                {
                    clone.Fields.Add(CloneFieldPayload(source.Fields[index]));
                }
            }
            if (source.Relations != null)
            {
                for (int index = 0; index < source.Relations.Count; index++)
                {
                    clone.Relations.Add(CloneRelationPayload(source.Relations[index]));
                }
            }
            return clone;
        }

        /// <summary>XMZADD 20260901 复制字段物理事实，为可信批次和历史记录提供独立结构载荷。</summary>
        internal static FieldStructurePayload CloneFieldPayload(FieldStructurePayload source)
        {
            if (source == null)
            {
                return null;
            }
            return new FieldStructurePayload
            {
                FieldName = source.FieldName,
                OwnerTableName = source.OwnerTableName,
                DataType = source.DataType,
                LengthText = source.LengthText,
                IsRequired = source.IsRequired,
                IsPrimaryKey = source.IsPrimaryKey,
                IsForeignKey = source.IsForeignKey
            };
        }

        /// <summary>XMZADD 20260901 复制关系完整父子端点，为稳定身份比较提供独立结构载荷。</summary>
        internal static RelationStructurePayload CloneRelationPayload(RelationStructurePayload source)
        {
            if (source == null)
            {
                return null;
            }
            return new RelationStructurePayload
            {
                ForeignKeyName = source.ForeignKeyName,
                ParentSchemaName = source.ParentSchemaName,
                ParentTableName = source.ParentTableName,
                ParentFieldName = source.ParentFieldName,
                ChildSchemaName = source.ChildSchemaName,
                ChildTableName = source.ChildTableName,
                ChildFieldName = source.ChildFieldName
            };
        }

        /// <summary>XMZADD 20260901 以固定错误代码和事件位置汇总安全失败，避免诊断信息包含不可信原文。</summary>
        private static void AddError(
            IList<DictionaryChangeValidationError> errors,
            string code,
            int operationIndex,
            string message)
        {
            errors.Add(new DictionaryChangeValidationError
            {
                Code = code,
                OperationIndex = operationIndex,
                Message = message
            });
        }
    }
}
