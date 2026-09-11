using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 标识服务端事件已应用或因全局操作号重复而安全忽略。</summary>
    public enum DictionaryEventApplyStatus
    {
        Applied = 0,
        DuplicateIgnored = 1
    }

    /// <summary>XMZADD 20260901 返回单个服务端事件的幂等处理状态和最终修订号。</summary>
    public sealed class DictionaryEventApplyResult
    {
        public DictionaryEventApplyStatus Status { get; set; }
        public long Revision { get; set; }
    }

    /// <summary>XMZADD 20260901 保存服务端实际应用前后值、可信作者和类型化结构快照用于不可变审计。</summary>
    public sealed class AppliedDictionaryEvent
    {
        public string OperationId { get; set; }
        public string AuthorGitHubUserId { get; set; }
        public string ObjectKey { get; set; }
        public string FieldKey { get; set; }
        public string PropertyName { get; set; }
        public string ChangeKind { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public long Revision { get; set; }
        public DateTime AppliedAtUtc { get; set; }
        public TableStructurePayload OldTablePayload { get; set; }
        public TableStructurePayload NewTablePayload { get; set; }
        public FieldStructurePayload OldFieldPayload { get; set; }
        public FieldStructurePayload NewFieldPayload { get; set; }
        public RelationStructurePayload OldRelationPayload { get; set; }
        public RelationStructurePayload NewRelationPayload { get; set; }
        public IList<EvidenceItem> Evidence { get; set; }
    }

    /// <summary>XMZADD 20260901 按服务端修订原子应用可信字典事件并实现同属性后写覆盖和 OperationId 幂等。</summary>
    public sealed class DictionaryEventApplyService
    {
        private readonly DictionaryChangeValidator _validator;
        private readonly HashSet<string> _appliedOperationIds;
        private readonly List<AppliedDictionaryEvent> _appliedHistory;

        /// <summary>XMZADD 20260901 初始化独立事件应用上下文，使调用方可按快照修订顺序重放远程历史。</summary>
        public DictionaryEventApplyService()
            : this(null)
        {
        }

        /// <summary>XMZADD 20260901 从持久化事件或本地状态恢复已应用操作号，保证进程重启后仍保持幂等。</summary>
        public DictionaryEventApplyService(IList<string> restoredAppliedOperationIds)
        {
            _validator = new DictionaryChangeValidator();
            _appliedOperationIds = new HashSet<string>(StringComparer.Ordinal);
            _appliedHistory = new List<AppliedDictionaryEvent>();
            if (restoredAppliedOperationIds == null)
            {
                return;
            }
            for (int index = 0; index < restoredAppliedOperationIds.Count; index++)
            {
                string operationId = restoredAppliedOperationIds[index];
                if (!IsRestorableOperationId(operationId))
                {
                    throw new ArgumentException("恢复的操作编号格式无效。", "restoredAppliedOperationIds");
                }
                _appliedOperationIds.Add(operationId);
            }
        }

        public IList<AppliedDictionaryEvent> AppliedHistory
        {
            get
            {
                var copies = new List<AppliedDictionaryEvent>();
                for (int index = 0; index < _appliedHistory.Count; index++)
                {
                    copies.Add(CloneHistory(_appliedHistory[index]));
                }
                return new ReadOnlyCollection<AppliedDictionaryEvent>(copies);
            }
        }

        public IList<string> AppliedOperationIds
        {
            get
            {
                var copies = new List<string>();
                foreach (string operationId in _appliedOperationIds)
                {
                    copies.Add(operationId);
                }
                copies.Sort(StringComparer.Ordinal);
                return new ReadOnlyCollection<string>(copies);
            }
        }

        /// <summary>XMZADD 20260901 先按操作号查重，再用 Issue API 作者身份校验并应用一个严格递增修订事件。</summary>
        public DictionaryEventApplyResult Apply(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            long revision,
            string trustedGitHubUserId,
            IList<string> publisherGitHubUserIds)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (operation == null)
            {
                throw new ArgumentNullException("operation");
            }

            // 网络重试必须先按全局操作号消重，不能因重试携带旧修订或被改写正文而再次应用。
            if (!string.IsNullOrEmpty(operation.OperationId) && _appliedOperationIds.Contains(operation.OperationId))
            {
                return new DictionaryEventApplyResult
                {
                    Status = DictionaryEventApplyStatus.DuplicateIgnored,
                    Revision = snapshot.Revision
                };
            }

            var batch = new DictionaryChangeBatch
            {
                BatchId = operation.OperationId,
                AuthorGitHubUserId = operation.AuthorGitHubUserId,
                CreatedAtUtc = operation.CreatedAtUtc
            };
            batch.Operations.Add(operation);
            DictionaryChangeValidationResult validation = _validator.ValidateAndNormalize(
                batch, trustedGitHubUserId, publisherGitHubUserIds);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException("字典事件未通过安全校验。");
            }

            DictionaryChangeOperation normalized = validation.NormalizedBatch.Operations[0];
            ValidateRevision(snapshot.Revision, revision);
            // 单个事件也先在副本上完成，保证集合损坏或后续端点失败时不会留下半个结构变化。
            SnapshotData workingSnapshot = CloneSnapshot(snapshot);
            var touchedFields = new List<FieldMetadata>();
            TrackTouchedField(workingSnapshot, normalized, touchedFields);
            AppliedDictionaryEvent history = ApplyNormalized(workingSnapshot, normalized, revision);
            ValidateTouchedFieldStructures(workingSnapshot, touchedFields);
            workingSnapshot.Revision = revision;
            CopySnapshotState(workingSnapshot, snapshot);
            _appliedOperationIds.Add(normalized.OperationId);
            _appliedHistory.Add(history);
            return new DictionaryEventApplyResult
            {
                Status = DictionaryEventApplyStatus.Applied,
                Revision = revision
            };
        }

        /// <summary>XMZADD 20260901 在快照副本按列表顺序应用一个服务端修订批次，任一目标失败时回滚全部状态。</summary>
        public IList<DictionaryEventApplyResult> ApplyBatch(
            SnapshotData snapshot,
            DictionaryChangeBatch batch,
            long serverRevision,
            string trustedGitHubUserId,
            IList<string> publisherGitHubUserIds)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (batch == null)
            {
                throw new ArgumentNullException("batch");
            }

            if (batch.Operations == null)
            {
                throw new InvalidOperationException("字典事件批次未通过安全校验。");
            }

            var filteredBatch = new DictionaryChangeBatch
            {
                BatchId = batch.BatchId,
                AuthorGitHubUserId = batch.AuthorGitHubUserId,
                CreatedAtUtc = batch.CreatedAtUtc
            };
            var originalIndexes = new List<int>();
            var results = new List<DictionaryEventApplyResult>();
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                DictionaryChangeOperation operation = batch.Operations[index];
                if (operation != null && !string.IsNullOrEmpty(operation.OperationId) &&
                    _appliedOperationIds.Contains(operation.OperationId))
                {
                    results.Add(new DictionaryEventApplyResult
                    {
                        Status = DictionaryEventApplyStatus.DuplicateIgnored,
                        Revision = snapshot.Revision
                    });
                    continue;
                }
                results.Add(null);
                originalIndexes.Add(index);
                filteredBatch.Operations.Add(operation);
            }

            // 全批重试不再校验正文或修订，避免已确认事件被篡改重发时影响当前快照。
            if (filteredBatch.Operations.Count == 0)
            {
                return new ReadOnlyCollection<DictionaryEventApplyResult>(results);
            }

            DictionaryChangeValidationResult validation = _validator.ValidateAndNormalize(
                filteredBatch, trustedGitHubUserId, publisherGitHubUserIds);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException("字典事件批次未通过安全校验。");
            }
            ValidateRevision(snapshot.Revision, serverRevision);

            SnapshotData workingSnapshot = CloneSnapshot(snapshot);
            var workingIds = new HashSet<string>(_appliedOperationIds, StringComparer.Ordinal);
            var workingHistory = new List<AppliedDictionaryEvent>();
            for (int index = 0; index < _appliedHistory.Count; index++)
            {
                workingHistory.Add(CloneHistory(_appliedHistory[index]));
            }
            var touchedFields = new List<FieldMetadata>();
            for (int index = 0; index < validation.NormalizedBatch.Operations.Count; index++)
            {
                DictionaryChangeOperation operation = validation.NormalizedBatch.Operations[index];
                TrackTouchedField(workingSnapshot, operation, touchedFields);
                AppliedDictionaryEvent history = ApplyNormalized(workingSnapshot, operation, serverRevision);
                workingIds.Add(operation.OperationId);
                workingHistory.Add(history);
                results[originalIndexes[index]] = new DictionaryEventApplyResult
                {
                    Status = DictionaryEventApplyStatus.Applied,
                    Revision = serverRevision
                };
            }
            ValidateTouchedFieldStructures(workingSnapshot, touchedFields);
            workingSnapshot.Revision = serverRevision;

            // 只有全批成功后才替换引用，避免后续事件目标缺失时保留前面事件的半成品。
            CopySnapshotState(workingSnapshot, snapshot);
            _appliedOperationIds.Clear();
            foreach (string operationId in workingIds)
            {
                _appliedOperationIds.Add(operationId);
            }
            _appliedHistory.Clear();
            _appliedHistory.AddRange(workingHistory);
            return new ReadOnlyCollection<DictionaryEventApplyResult>(results);
        }

        /// <summary>XMZADD 20260901 按仓库事件中的真实数字作者分别规范化同一修订，并在一个快照副本原子提交全部结果。</summary>
        public IList<DictionaryEventApplyResult> ApplyTrustedRepositoryBatch(
            SnapshotData snapshot,
            DictionaryChangeBatch batch,
            long serverRevision)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (batch == null)
            {
                throw new ArgumentNullException("batch");
            }
            if (batch.Operations == null)
            {
                throw new InvalidOperationException("可信仓库事件批次未通过安全校验。");
            }

            var normalizedOperations = new List<DictionaryChangeOperation>();
            var originalIndexes = new List<int>();
            var results = new List<DictionaryEventApplyResult>();
            var incomingOperationIds = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                DictionaryChangeOperation operation = batch.Operations[index];
                if (operation != null && !string.IsNullOrEmpty(operation.OperationId) &&
                    _appliedOperationIds.Contains(operation.OperationId))
                {
                    results.Add(new DictionaryEventApplyResult
                    {
                        Status = DictionaryEventApplyStatus.DuplicateIgnored,
                        Revision = snapshot.Revision
                    });
                    continue;
                }
                results.Add(null);
                if (operation == null || !IsValidRepositoryAuthor(operation.AuthorGitHubUserId) ||
                    string.IsNullOrEmpty(operation.OperationId) || !incomingOperationIds.Add(operation.OperationId))
                {
                    throw new InvalidOperationException("可信仓库事件作者或操作号无效。");
                }

                var singleBatch = new DictionaryChangeBatch
                {
                    BatchId = operation.OperationId,
                    AuthorGitHubUserId = operation.AuthorGitHubUserId,
                    CreatedAtUtc = operation.CreatedAtUtc
                };
                singleBatch.Operations.Add(operation);
                var temporaryPublishers = new List<string> { operation.AuthorGitHubUserId };
                DictionaryChangeValidationResult validation = _validator.ValidateAndNormalize(
                    singleBatch,
                    operation.AuthorGitHubUserId,
                    temporaryPublishers);
                if (!validation.IsValid || validation.NormalizedBatch == null ||
                    validation.NormalizedBatch.Operations == null ||
                    validation.NormalizedBatch.Operations.Count != 1)
                {
                    throw new InvalidOperationException("可信仓库事件未通过安全校验。");
                }
                originalIndexes.Add(index);
                normalizedOperations.Add(validation.NormalizedBatch.Operations[0]);
            }

            // 仓库重放整批均已确认时直接保持幂等，不重复校验旧修订或已落地正文。
            if (normalizedOperations.Count == 0)
            {
                return new ReadOnlyCollection<DictionaryEventApplyResult>(results);
            }
            ValidateRevision(snapshot.Revision, serverRevision);

            SnapshotData workingSnapshot = CloneSnapshot(snapshot);
            var workingIds = new HashSet<string>(_appliedOperationIds, StringComparer.Ordinal);
            var workingHistory = new List<AppliedDictionaryEvent>();
            for (int historyIndex = 0; historyIndex < _appliedHistory.Count; historyIndex++)
            {
                workingHistory.Add(CloneHistory(_appliedHistory[historyIndex]));
            }
            var touchedFields = new List<FieldMetadata>();
            for (int operationIndex = 0; operationIndex < normalizedOperations.Count; operationIndex++)
            {
                DictionaryChangeOperation operation = normalizedOperations[operationIndex];
                TrackTouchedField(workingSnapshot, operation, touchedFields);
                AppliedDictionaryEvent history = ApplyNormalized(workingSnapshot, operation, serverRevision);
                workingIds.Add(operation.OperationId);
                workingHistory.Add(history);
                results[originalIndexes[operationIndex]] = new DictionaryEventApplyResult
                {
                    Status = DictionaryEventApplyStatus.Applied,
                    Revision = serverRevision
                };
            }
            ValidateTouchedFieldStructures(workingSnapshot, touchedFields);
            workingSnapshot.Revision = serverRevision;

            // 多作者修订只有全部事件成功后才一次性提交快照、幂等号和真实作者历史。
            CopySnapshotState(workingSnapshot, snapshot);
            _appliedOperationIds.Clear();
            foreach (string operationId in workingIds)
            {
                _appliedOperationIds.Add(operationId);
            }
            _appliedHistory.Clear();
            _appliedHistory.AddRange(workingHistory);
            return new ReadOnlyCollection<DictionaryEventApplyResult>(results);
        }

        /// <summary>XMZADD 20260901 校验仓库事件作者只含非零 ASCII 数字，确保审计身份与 GitHub 数值 ID 一致。</summary>
        private static bool IsValidRepositoryAuthor(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
            {
                return false;
            }
            bool hasNonZeroDigit = false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character < '0' || character > '9')
                {
                    return false;
                }
                if (character != '0')
                {
                    hasNonZeroDigit = true;
                }
            }
            return hasNonZeroDigit;
        }

        /// <summary>XMZADD 20260901 校验恢复操作号只包含事件协议允许的稳定字符和长度。</summary>
        private static bool IsRestorableOperationId(string value)
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

        /// <summary>XMZADD 20260901 要求每个非重复事件使用正数且大于快照游标的服务端修订。</summary>
        private static void ValidateRevision(long currentRevision, long revision)
        {
            if (revision <= 0L)
            {
                throw new ArgumentOutOfRangeException("revision", "服务端修订必须为正数。");
            }
            if (revision <= currentRevision)
            {
                throw new InvalidOperationException("服务端修订没有严格递增。");
            }
        }

        /// <summary>XMZADD 20260901 通过固定事件类型分支应用单个规范事件，拒绝反射或任意属性写入。</summary>
        private static AppliedDictionaryEvent ApplyNormalized(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            long revision)
        {
            if (operation.ChangeKind == "Set")
            {
                return ApplySet(snapshot, operation, revision);
            }
            if (operation.ChangeKind == "AddTable")
            {
                return ApplyAddTable(snapshot, operation, revision);
            }
            if (operation.ChangeKind == "RemoveTable")
            {
                return ApplyRemoveTable(snapshot, operation, revision);
            }
            if (operation.ChangeKind == "AddField")
            {
                return ApplyAddField(snapshot, operation, revision);
            }
            if (operation.ChangeKind == "RemoveField")
            {
                return ApplyRemoveField(snapshot, operation, revision);
            }
            if (operation.ChangeKind == "AddRelation")
            {
                return ApplyAddRelation(snapshot, operation, revision);
            }
            if (operation.ChangeKind == "RemoveRelation")
            {
                return ApplyRemoveRelation(snapshot, operation, revision);
            }
            throw new InvalidOperationException("事件类型不受支持。");
        }

        /// <summary>XMZADD 20260901 应用表或字段的显式白名单标量并记录服务端读取到的真实旧值。</summary>
        private static AppliedDictionaryEvent ApplySet(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            long revision)
        {
            TableMetadata table = FindTable(snapshot, operation.ObjectKey);
            if (table == null)
            {
                throw new InvalidOperationException("事件目标表不存在。");
            }
            FieldMetadata field = string.IsNullOrEmpty(operation.FieldKey) ? null : FindField(table, operation.FieldKey);
            if (!string.IsNullOrEmpty(operation.FieldKey) && field == null)
            {
                throw new InvalidOperationException("事件目标字段不存在。");
            }

            string actualOldValue;
            if (field == null)
            {
                actualOldValue = ApplyTableSet(snapshot, table, operation.PropertyName, operation.NewValue, operation.Evidence);
            }
            else
            {
                actualOldValue = ApplyFieldSet(snapshot, table, field, operation.PropertyName, operation.NewValue, operation.Evidence);
            }
            string actualNewValue = ReadAppliedValue(table, field, operation.PropertyName);
            return CreateHistory(operation, revision, actualOldValue, actualNewValue);
        }

        /// <summary>XMZADD 20260901 类型安全更新表业务解释、分类、空表规则和允许发布的物理标量。</summary>
        private static string ApplyTableSet(
            SnapshotData snapshot,
            TableMetadata table,
            string propertyName,
            string newValue,
            IList<EvidenceItem> publishedEvidence)
        {
            MetadataValue current;
            switch (propertyName)
            {
                case "ChineseName":
                    current = table.ChineseName;
                    table.ChineseName = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "BusinessMeaning":
                    current = table.BusinessMeaning;
                    table.BusinessMeaning = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "ModuleName":
                    current = table.ModuleName;
                    table.ModuleName = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "EntityName":
                    current = table.EntityName;
                    table.EntityName = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "Remark":
                    current = table.Remark;
                    table.Remark = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "Category":
                    string oldCategory = table.Category.ToString();
                    table.Category = ParseCategory(newValue);
                    return oldCategory;
                case "KeepWhenEmpty":
                    string oldKeep = table.KeepWhenEmpty ? bool.TrueString : bool.FalseString;
                    table.KeepWhenEmpty = ParseCanonicalBoolean(newValue);
                    return oldKeep;
                case "ObjectType":
                    string oldObjectType = table.ObjectType ?? string.Empty;
                    table.ObjectType = newValue;
                    return oldObjectType;
                case "ApproximateRowCount":
                    string oldRowCount = table.ApproximateRowCount.ToString(CultureInfo.InvariantCulture);
                    table.ApproximateRowCount = long.Parse(newValue, CultureInfo.InvariantCulture);
                    return oldRowCount;
                case "SchemaName":
                    string oldSchema = table.SchemaName ?? string.Empty;
                    EnsureTableRenameDoesNotCollide(snapshot, table, newValue, table.ObjectName);
                    UpdateTableRelationEndpoints(snapshot, oldSchema, table.ObjectName, newValue, table.ObjectName);
                    table.SchemaName = newValue;
                    return oldSchema;
                case "ObjectName":
                    string oldObjectName = table.ObjectName ?? string.Empty;
                    EnsureTableRenameDoesNotCollide(snapshot, table, table.SchemaName, newValue);
                    UpdateTableRelationEndpoints(snapshot, table.SchemaName, oldObjectName, table.SchemaName, newValue);
                    for (int index = 0; index < table.Fields.Count; index++)
                    {
                        if (table.Fields[index] != null)
                        {
                            table.Fields[index].OwnerTableName = newValue;
                        }
                    }
                    table.ObjectName = newValue;
                    return oldObjectName;
                default:
                    throw new InvalidOperationException("表属性不受支持。");
            }
        }

        /// <summary>XMZADD 20260901 类型安全更新字段业务解释和允许发布的物理约束。</summary>
        private static string ApplyFieldSet(
            SnapshotData snapshot,
            TableMetadata table,
            FieldMetadata field,
            string propertyName,
            string newValue,
            IList<EvidenceItem> publishedEvidence)
        {
            MetadataValue current;
            switch (propertyName)
            {
                case "ChineseName":
                    current = field.ChineseName;
                    field.ChineseName = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "BusinessMeaning":
                    current = field.BusinessMeaning;
                    field.BusinessMeaning = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "Usage":
                    current = field.Usage;
                    field.Usage = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "EntityPropertyName":
                    current = field.EntityPropertyName;
                    field.EntityPropertyName = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "Remark":
                    current = field.Remark;
                    field.Remark = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "RelationSummary":
                    current = field.RelationSummary;
                    field.RelationSummary = CreateRemoteManualValue(current, newValue, publishedEvidence);
                    return GetMetadataText(current);
                case "DataType":
                    string oldDataType = field.DataType ?? string.Empty;
                    string normalizedDataType;
                    if (!DictionaryChangeValidator.TryNormalizeSqlServerType(newValue, out normalizedDataType))
                    {
                        throw new InvalidOperationException("字段类型值无效。");
                    }
                    field.DataType = normalizedDataType;
                    return oldDataType;
                case "LengthText":
                    string oldLength = field.LengthText ?? string.Empty;
                    string normalizedLength;
                    if (!DictionaryChangeValidator.TryNormalizeLengthText(newValue, out normalizedLength))
                    {
                        throw new InvalidOperationException("字段长度值无效。");
                    }
                    field.LengthText = normalizedLength;
                    return oldLength;
                case "IsRequired":
                    string oldRequired = field.IsRequired ? bool.TrueString : bool.FalseString;
                    field.IsRequired = ParseCanonicalBoolean(newValue);
                    return oldRequired;
                case "IsPrimaryKey":
                    string oldPrimary = field.IsPrimaryKey ? bool.TrueString : bool.FalseString;
                    field.IsPrimaryKey = ParseCanonicalBoolean(newValue);
                    return oldPrimary;
                case "IsForeignKey":
                    string oldForeign = field.IsForeignKey ? bool.TrueString : bool.FalseString;
                    field.IsForeignKey = ParseCanonicalBoolean(newValue);
                    return oldForeign;
                case "OwnerTableName":
                    if (!string.Equals(newValue, table.ObjectName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("字段所属表与事件目标不一致。");
                    }
                    string oldOwner = field.OwnerTableName ?? string.Empty;
                    field.OwnerTableName = newValue;
                    return oldOwner;
                case "FieldName":
                    string oldFieldName = field.FieldName ?? string.Empty;
                    if (FindField(table, newValue) != null && !string.Equals(oldFieldName, newValue, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("目标字段名已存在。");
                    }
                    UpdateFieldRelationEndpoints(snapshot, table.SchemaName, table.ObjectName, oldFieldName, newValue);
                    field.FieldName = newValue;
                    return oldFieldName;
                default:
                    throw new InvalidOperationException("字段属性不受支持。");
            }
        }

        /// <summary>XMZADD 20260901 新增无内嵌字段关系的 TABLE 对象，后续结构由独立事件增量补齐。</summary>
        private static AppliedDictionaryEvent ApplyAddTable(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            long revision)
        {
            if (FindTable(snapshot, operation.ObjectKey) != null)
            {
                throw new InvalidOperationException("新增表已经存在。");
            }
            TableStructurePayload payload = operation.TablePayload;
            var table = new TableMetadata
            {
                SchemaName = payload.SchemaName,
                ObjectName = payload.ObjectName,
                ObjectType = payload.ObjectType,
                ApproximateRowCount = payload.ApproximateRowCount,
                Category = DictionaryTableCategory.Unclassified
            };
            if (snapshot.Tables == null)
            {
                throw new InvalidOperationException("快照表集合无效。");
            }
            snapshot.Tables.Add(table);
            AppliedDictionaryEvent history = CreateHistory(operation, revision, null, null);
            history.NewTablePayload = CreateTablePayload(table, false);
            return history;
        }

        /// <summary>XMZADD 20260901 删除目标表并同步清除全快照中引用该表的关系。</summary>
        private static AppliedDictionaryEvent ApplyRemoveTable(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            long revision)
        {
            TableMetadata table = FindTable(snapshot, operation.ObjectKey);
            if (table == null)
            {
                throw new InvalidOperationException("删除表不存在。");
            }
            TableStructurePayload oldPayload = CreateTablePayload(table, true);
            RemoveRelationsForTable(snapshot, table.SchemaName, table.ObjectName);
            snapshot.Tables.Remove(table);
            RecalculateForeignKeyFlags(snapshot);
            AppliedDictionaryEvent history = CreateHistory(operation, revision, null, null);
            history.OldTablePayload = oldPayload;
            return history;
        }

        /// <summary>XMZADD 20260901 向既有表新增类型化字段并拒绝忽略大小写的重复字段键。</summary>
        private static AppliedDictionaryEvent ApplyAddField(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            long revision)
        {
            TableMetadata table = FindTable(snapshot, operation.ObjectKey);
            if (table == null)
            {
                throw new InvalidOperationException("新增字段所属表不存在。");
            }
            if (FindField(table, operation.FieldKey) != null)
            {
                throw new InvalidOperationException("新增字段已经存在。");
            }
            FieldStructurePayload payload = operation.FieldPayload;
            if (!string.Equals(payload.OwnerTableName, table.ObjectName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("新增字段所属表与目标不一致。");
            }
            FieldMetadata field = CreateField(payload);
            table.Fields.Add(field);
            AppliedDictionaryEvent history = CreateHistory(operation, revision, null, null);
            history.NewFieldPayload = CreateFieldPayload(field);
            return history;
        }

        /// <summary>XMZADD 20260901 删除目标字段并清理以该字段作为任一端点的全部关系。</summary>
        private static AppliedDictionaryEvent ApplyRemoveField(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            long revision)
        {
            TableMetadata table = FindTable(snapshot, operation.ObjectKey);
            FieldMetadata field = table == null ? null : FindField(table, operation.FieldKey);
            if (field == null)
            {
                throw new InvalidOperationException("删除字段不存在。");
            }
            FieldStructurePayload oldPayload = CreateFieldPayload(field);
            RemoveRelationsForField(snapshot, table.SchemaName, table.ObjectName, field.FieldName);
            table.Fields.Remove(field);
            RecalculateForeignKeyFlags(snapshot);
            AppliedDictionaryEvent history = CreateHistory(operation, revision, null, null);
            history.OldFieldPayload = oldPayload;
            return history;
        }

        /// <summary>XMZADD 20260901 以完整父子端点新增关系并将同一结构实例挂到父子表。</summary>
        private static AppliedDictionaryEvent ApplyAddRelation(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            long revision)
        {
            RelationStructurePayload payload = operation.RelationPayload;
            TableMetadata parent = FindTable(snapshot, payload.ParentSchemaName + "." + payload.ParentTableName);
            TableMetadata child = FindTable(snapshot, payload.ChildSchemaName + "." + payload.ChildTableName);
            if (parent == null || child == null || FindField(parent, payload.ParentFieldName) == null ||
                FindField(child, payload.ChildFieldName) == null)
            {
                throw new InvalidOperationException("关系端点不存在。");
            }
            if (parent.Relations == null || child.Relations == null)
            {
                throw new InvalidOperationException("关系端点集合无效。");
            }
            if (FindRelation(snapshot, payload) != null)
            {
                throw new InvalidOperationException("关系稳定键已经存在。");
            }

            RelationMetadata relation = CreateRelation(payload);
            parent.Relations.Add(relation);
            if (!object.ReferenceEquals(parent, child))
            {
                child.Relations.Add(relation);
            }
            FieldMetadata childField = FindField(child, payload.ChildFieldName);
            childField.IsForeignKey = true;
            AppliedDictionaryEvent history = CreateHistory(operation, revision, null, null);
            history.NewRelationPayload = CreateRelationPayload(relation);
            return history;
        }

        /// <summary>XMZADD 20260901 按稳定关系身份从快照所有表移除关系并重新核定外键标识。</summary>
        private static AppliedDictionaryEvent ApplyRemoveRelation(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            long revision)
        {
            RelationMetadata existing = FindRelation(snapshot, operation.RelationPayload);
            if (existing == null)
            {
                throw new InvalidOperationException("删除关系不存在。");
            }
            RelationStructurePayload oldPayload = CreateRelationPayload(existing);
            RemoveRelationFromAllTables(snapshot, operation.RelationPayload);
            RecalculateForeignKeyFlags(snapshot);
            AppliedDictionaryEvent history = CreateHistory(operation, revision, null, null);
            history.OldRelationPayload = oldPayload;
            return history;
        }

        /// <summary>XMZADD 20260901 创建远程人工确认值并保留自动值与既有证据的追溯链。</summary>
        private static MetadataValue CreateRemoteManualValue(
            MetadataValue current,
            string newValue,
            IList<EvidenceItem> publishedEvidence)
        {
            string automaticValue = string.Empty;
            if (current != null)
            {
                automaticValue = string.IsNullOrWhiteSpace(current.OriginalAutomaticValue)
                    ? current.Value ?? string.Empty
                    : current.OriginalAutomaticValue;
            }
            if (publishedEvidence != null && publishedEvidence.Count > 0)
            {
                ConfidenceStatus status = ConfidenceStatus.DatabaseEvidence;
                int confidenceScore = 90;
                string sourceType = "数据库依据";
                string sourceSummary = "结构发布数据库说明";
                for (int index = 0; index < publishedEvidence.Count; index++)
                {
                    EvidenceItem item = publishedEvidence[index];
                    if (item != null && string.Equals(item.SourceType, "EOS知识库", StringComparison.Ordinal))
                    {
                        status = ConfidenceStatus.KnowledgeBaseEvidence;
                        confidenceScore = 95;
                        sourceType = "EOS知识库";
                        sourceSummary = "EOS 项目知识库精确条目";
                        break;
                    }
                    if (item != null && string.Equals(item.SourceType, "EOS源码", StringComparison.Ordinal))
                    {
                        status = ConfidenceStatus.CodeEvidence;
                        confidenceScore = 85;
                        sourceType = "EOS源码";
                        sourceSummary = "EOS 源码可靠证据";
                    }
                }
                return new MetadataValue
                {
                    Value = newValue ?? string.Empty,
                    Status = status,
                    ConfidenceScore = confidenceScore,
                    SourceType = sourceType,
                    SourceSummary = sourceSummary,
                    OriginalAutomaticValue = automaticValue,
                    IsManualOverride = false,
                    IsLocked = false,
                    Evidence = CloneEvidence(publishedEvidence)
                };
            }
            return new MetadataValue
            {
                Value = newValue ?? string.Empty,
                Status = ConfidenceStatus.Confirmed,
                ConfidenceScore = 100,
                SourceType = "GitHub人工维护",
                SourceSummary = "GitHub人工确认",
                OriginalAutomaticValue = automaticValue,
                IsManualOverride = true,
                IsLocked = true,
                Evidence = CloneEvidence(current == null ? null : current.Evidence)
            };
        }

        /// <summary>XMZADD 20260901 深拷贝证据集合，防止历史快照和外部对象共享可变列表。</summary>
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
                if (item != null)
                {
                    result.Add(new EvidenceItem
                    {
                        SourceType = item.SourceType,
                        SourcePath = item.SourcePath,
                        SourceLine = item.SourceLine,
                        RuleName = item.RuleName,
                        RawValue = item.RawValue,
                        OriginalText = item.OriginalText,
                        Explanation = item.Explanation
                    });
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260901 将发布协议中的规范分类文本映射为快照枚举，拒绝未知分类。</summary>
        private static DictionaryTableCategory ParseCategory(string value)
        {
            if (value == "Business") return DictionaryTableCategory.Business;
            if (value == "BaseData") return DictionaryTableCategory.BaseData;
            if (value == "Technical") return DictionaryTableCategory.Technical;
            if (value == "Excluded") return DictionaryTableCategory.Excluded;
            throw new InvalidOperationException("表分类值无效。");
        }

        /// <summary>XMZADD 20260901 将发布协议中的规范布尔文本映射为结构标志，拒绝宽松或区域化写法。</summary>
        private static bool ParseCanonicalBoolean(string value)
        {
            if (value == bool.TrueString) return true;
            if (value == bool.FalseString) return false;
            throw new InvalidOperationException("布尔结构值无效。");
        }

        /// <summary>XMZADD 20260901 读取元数据业务文本并统一空值，保证历史前后值比较稳定。</summary>
        private static string GetMetadataText(MetadataValue value)
        {
            return value == null ? string.Empty : value.Value ?? string.Empty;
        }

        /// <summary>XMZADD 20260901 从已应用对象读取规范后值，保证历史不复用客户端提交文本。</summary>
        private static string ReadAppliedValue(TableMetadata table, FieldMetadata field, string propertyName)
        {
            if (field == null)
            {
                if (propertyName == "ChineseName") return GetMetadataText(table.ChineseName);
                if (propertyName == "BusinessMeaning") return GetMetadataText(table.BusinessMeaning);
                if (propertyName == "ModuleName") return GetMetadataText(table.ModuleName);
                if (propertyName == "EntityName") return GetMetadataText(table.EntityName);
                if (propertyName == "Remark") return GetMetadataText(table.Remark);
                if (propertyName == "Category") return table.Category.ToString();
                if (propertyName == "KeepWhenEmpty") return table.KeepWhenEmpty ? bool.TrueString : bool.FalseString;
                if (propertyName == "ApproximateRowCount") return table.ApproximateRowCount.ToString(CultureInfo.InvariantCulture);
                if (propertyName == "SchemaName") return table.SchemaName ?? string.Empty;
                if (propertyName == "ObjectName") return table.ObjectName ?? string.Empty;
                if (propertyName == "ObjectType") return table.ObjectType ?? string.Empty;
                throw new InvalidOperationException("表属性不受支持。");
            }
            if (propertyName == "ChineseName") return GetMetadataText(field.ChineseName);
            if (propertyName == "BusinessMeaning") return GetMetadataText(field.BusinessMeaning);
            if (propertyName == "Usage") return GetMetadataText(field.Usage);
            if (propertyName == "EntityPropertyName") return GetMetadataText(field.EntityPropertyName);
            if (propertyName == "Remark") return GetMetadataText(field.Remark);
            if (propertyName == "RelationSummary") return GetMetadataText(field.RelationSummary);
            if (propertyName == "DataType") return field.DataType ?? string.Empty;
            if (propertyName == "LengthText") return field.LengthText ?? string.Empty;
            if (propertyName == "IsRequired") return field.IsRequired ? bool.TrueString : bool.FalseString;
            if (propertyName == "IsPrimaryKey") return field.IsPrimaryKey ? bool.TrueString : bool.FalseString;
            if (propertyName == "IsForeignKey") return field.IsForeignKey ? bool.TrueString : bool.FalseString;
            if (propertyName == "OwnerTableName") return field.OwnerTableName ?? string.Empty;
            if (propertyName == "FieldName") return field.FieldName ?? string.Empty;
            throw new InvalidOperationException("字段属性不受支持。");
        }

        /// <summary>XMZADD 20260901 按对象稳定键忽略大小写定位快照表，确保 SQL Server 标识符重放一致。</summary>
        private static TableMetadata FindTable(SnapshotData snapshot, string objectKey)
        {
            if (snapshot.Tables == null || string.IsNullOrEmpty(objectKey))
            {
                return null;
            }
            int separator = objectKey.IndexOf('.');
            if (separator <= 0 || separator >= objectKey.Length - 1)
            {
                return null;
            }
            string schemaName = objectKey.Substring(0, separator);
            string objectName = objectKey.Substring(separator + 1);
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                if (table != null && string.Equals(table.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(table.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return table;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 在目标表内忽略大小写定位字段，兼容 SQL Server 默认标识符比较规则。</summary>
        private static FieldMetadata FindField(TableMetadata table, string fieldName)
        {
            if (table == null || table.Fields == null)
            {
                return null;
            }
            for (int index = 0; index < table.Fields.Count; index++)
            {
                FieldMetadata field = table.Fields[index];
                if (field != null && string.Equals(field.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 记录本批次直接修改类型或长度的字段实例，使配套事件可在最终状态统一校验。</summary>
        private static void TrackTouchedField(
            SnapshotData snapshot,
            DictionaryChangeOperation operation,
            IList<FieldMetadata> touchedFields)
        {
            if (operation == null || operation.ChangeKind != "Set" ||
                operation.PropertyName != "DataType" && operation.PropertyName != "LengthText")
            {
                return;
            }

            TableMetadata table = FindTable(snapshot, operation.ObjectKey);
            FieldMetadata field = FindField(table, operation.FieldKey);
            if (field == null)
            {
                return;
            }
            for (int index = 0; index < touchedFields.Count; index++)
            {
                if (object.ReferenceEquals(touchedFields[index], field))
                {
                    return;
                }
            }
            touchedFields.Add(field);
        }

        /// <summary>XMZADD 20260901 在整项或整批应用结束后校验仍存在的触及字段，非法最终组合由快照副本实现整体回滚。</summary>
        private static void ValidateTouchedFieldStructures(
            SnapshotData snapshot,
            IList<FieldMetadata> touchedFields)
        {
            for (int index = 0; index < touchedFields.Count; index++)
            {
                FieldMetadata field = touchedFields[index];
                if (!ContainsFieldReference(snapshot, field))
                {
                    continue;
                }
                string normalizedDataType;
                string normalizedLength;
                if (!DictionaryChangeValidator.TryNormalizeSqlServerType(field.DataType, out normalizedDataType) ||
                    !DictionaryChangeValidator.TryNormalizeLengthText(field.LengthText, out normalizedLength) ||
                    !DictionaryChangeValidator.IsLengthValidForDataType(normalizedDataType, normalizedLength))
                {
                    throw new InvalidOperationException("字段类型与长度的最终组合无效。");
                }
            }
        }

        /// <summary>XMZADD 20260901 仅校验批次结束后仍属于快照的字段实例，已删除对象不再参与最终结构约束。</summary>
        private static bool ContainsFieldReference(SnapshotData snapshot, FieldMetadata expected)
        {
            if (snapshot.Tables == null)
            {
                return false;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    if (object.ReferenceEquals(table.Fields[fieldIndex], expected))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 检查表重命名目标不会覆盖另一张已存在的物理对象。</summary>
        private static void EnsureTableRenameDoesNotCollide(
            SnapshotData snapshot,
            TableMetadata current,
            string schemaName,
            string objectName)
        {
            TableMetadata existing = FindTable(snapshot, schemaName + "." + objectName);
            if (existing != null && !object.ReferenceEquals(existing, current))
            {
                throw new InvalidOperationException("目标表稳定键已经存在。");
            }
        }

        /// <summary>XMZADD 20260901 表重命名时同步更新所有关系端点，保持结构身份可继续增量定位。</summary>
        private static void UpdateTableRelationEndpoints(
            SnapshotData snapshot,
            string oldSchema,
            string oldTable,
            string newSchema,
            string newTable)
        {
            VisitAllRelations(snapshot, delegate(RelationMetadata relation)
            {
                if (string.Equals(relation.ParentSchemaName, oldSchema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(relation.ParentTableName, oldTable, StringComparison.OrdinalIgnoreCase))
                {
                    relation.ParentSchemaName = newSchema;
                    relation.ParentTableName = newTable;
                }
                if (string.Equals(relation.ChildSchemaName, oldSchema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(relation.ChildTableName, oldTable, StringComparison.OrdinalIgnoreCase))
                {
                    relation.ChildSchemaName = newSchema;
                    relation.ChildTableName = newTable;
                }
            });
        }

        /// <summary>XMZADD 20260901 字段重命名时同步更新其全部关系端点，避免产生悬空关系。</summary>
        private static void UpdateFieldRelationEndpoints(
            SnapshotData snapshot,
            string schemaName,
            string tableName,
            string oldField,
            string newField)
        {
            VisitAllRelations(snapshot, delegate(RelationMetadata relation)
            {
                if (EndpointMatches(relation.ParentSchemaName, relation.ParentTableName, relation.ParentFieldName,
                    schemaName, tableName, oldField))
                {
                    relation.ParentFieldName = newField;
                }
                if (EndpointMatches(relation.ChildSchemaName, relation.ChildTableName, relation.ChildFieldName,
                    schemaName, tableName, oldField))
                {
                    relation.ChildFieldName = newField;
                }
            });
        }

        /// <summary>XMZADD 20260901 删除表时清除该表作为父端或子端的全部关系记录。</summary>
        private static void RemoveRelationsForTable(SnapshotData snapshot, string schemaName, string tableName)
        {
            var targets = new List<RelationStructurePayload>();
            CollectUniqueRelations(snapshot, targets, delegate(RelationMetadata relation)
            {
                return TableEndpointMatches(relation.ParentSchemaName, relation.ParentTableName, schemaName, tableName) ||
                    TableEndpointMatches(relation.ChildSchemaName, relation.ChildTableName, schemaName, tableName);
            });
            for (int index = 0; index < targets.Count; index++)
            {
                RemoveRelationFromAllTables(snapshot, targets[index]);
            }
        }

        /// <summary>XMZADD 20260901 删除字段时清除该字段作为父端或子端的全部关系记录。</summary>
        private static void RemoveRelationsForField(
            SnapshotData snapshot,
            string schemaName,
            string tableName,
            string fieldName)
        {
            var targets = new List<RelationStructurePayload>();
            CollectUniqueRelations(snapshot, targets, delegate(RelationMetadata relation)
            {
                return EndpointMatches(relation.ParentSchemaName, relation.ParentTableName, relation.ParentFieldName,
                           schemaName, tableName, fieldName) ||
                    EndpointMatches(relation.ChildSchemaName, relation.ChildTableName, relation.ChildFieldName,
                           schemaName, tableName, fieldName);
            });
            for (int index = 0; index < targets.Count; index++)
            {
                RemoveRelationFromAllTables(snapshot, targets[index]);
            }
        }

        /// <summary>XMZADD 20260901 从父子表及可能的重复挂接中移除同一稳定关系。</summary>
        private static void RemoveRelationFromAllTables(SnapshotData snapshot, RelationStructurePayload payload)
        {
            if (snapshot.Tables == null)
            {
                return;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Relations == null)
                {
                    continue;
                }
                for (int relationIndex = table.Relations.Count - 1; relationIndex >= 0; relationIndex--)
                {
                    if (RelationMatches(table.Relations[relationIndex], payload))
                    {
                        table.Relations.RemoveAt(relationIndex);
                    }
                }
            }
        }

        /// <summary>XMZADD 20260901 根据剩余关系端点重新计算字段外键标识，避免结构删除留下陈旧事实。</summary>
        private static void RecalculateForeignKeyFlags(SnapshotData snapshot)
        {
            if (snapshot.Tables == null)
            {
                return;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    if (table.Fields[fieldIndex] != null)
                    {
                        table.Fields[fieldIndex].IsForeignKey = false;
                    }
                }
            }
            VisitUniqueRelations(snapshot, delegate(RelationMetadata relation)
            {
                TableMetadata child = FindTable(snapshot, relation.ChildSchemaName + "." + relation.ChildTableName);
                FieldMetadata childField = FindField(child, relation.ChildFieldName);
                if (childField != null)
                {
                    childField.IsForeignKey = true;
                }
            });
        }

        /// <summary>XMZADD 20260901 定义关系筛选规则，使重复挂接的关系可按业务条件统一收集。</summary>
        private delegate bool RelationPredicate(RelationMetadata relation);

        /// <summary>XMZADD 20260901 定义关系访问规则，使父子表中的关系副本可执行一致业务处理。</summary>
        private delegate void RelationVisitor(RelationMetadata relation);

        /// <summary>XMZADD 20260901 遍历每个关系实例，使 JSON 往返后的父子独立副本都能同步更新端点。</summary>
        private static void VisitAllRelations(SnapshotData snapshot, RelationVisitor visitor)
        {
            if (snapshot.Tables == null)
            {
                return;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Relations == null)
                {
                    continue;
                }
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    if (table.Relations[relationIndex] != null)
                    {
                        visitor(table.Relations[relationIndex]);
                    }
                }
            }
        }

        /// <summary>XMZADD 20260901 收集满足条件的唯一稳定关系，兼容父子表各自持有独立反序列化实例。</summary>
        private static void CollectUniqueRelations(
            SnapshotData snapshot,
            IList<RelationStructurePayload> result,
            RelationPredicate predicate)
        {
            VisitUniqueRelations(snapshot, delegate(RelationMetadata relation)
            {
                if (predicate(relation))
                {
                    result.Add(CreateRelationPayload(relation));
                }
            });
        }

        /// <summary>XMZADD 20260901 按稳定身份遍历关系一次，避免父子表双挂接造成重复业务处理。</summary>
        private static void VisitUniqueRelations(SnapshotData snapshot, RelationVisitor visitor)
        {
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (snapshot.Tables == null)
            {
                return;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Relations == null)
                {
                    continue;
                }
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = table.Relations[relationIndex];
                    if (relation == null)
                    {
                        continue;
                    }
                    string identity = GetRelationIdentity(CreateRelationPayload(relation));
                    if (identities.Add(identity))
                    {
                        visitor(relation);
                    }
                }
            }
        }

        /// <summary>XMZADD 20260901 按完整稳定身份查找关系，兼容关系同时挂接在父表和子表。</summary>
        private static RelationMetadata FindRelation(SnapshotData snapshot, RelationStructurePayload payload)
        {
            if (snapshot.Tables == null)
            {
                return null;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Relations == null)
                {
                    continue;
                }
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    if (RelationMatches(table.Relations[relationIndex], payload))
                    {
                        return table.Relations[relationIndex];
                    }
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 比较外键名和完整父子端点，避免同名关系或局部端点误匹配。</summary>
        private static bool RelationMatches(RelationMetadata relation, RelationStructurePayload payload)
        {
            return relation != null && payload != null &&
                string.Equals(GetRelationIdentity(CreateRelationPayload(relation)), GetRelationIdentity(payload), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260901 组合关系的外键名和完整父子端点，生成仅供内存去重的稳定身份。</summary>
        private static string GetRelationIdentity(RelationStructurePayload payload)
        {
            if (payload == null)
            {
                return string.Empty;
            }
            return (payload.ForeignKeyName ?? string.Empty) + "\u001f" +
                (payload.ParentSchemaName ?? string.Empty) + "\u001f" +
                (payload.ParentTableName ?? string.Empty) + "\u001f" +
                (payload.ParentFieldName ?? string.Empty) + "\u001f" +
                (payload.ChildSchemaName ?? string.Empty) + "\u001f" +
                (payload.ChildTableName ?? string.Empty) + "\u001f" +
                (payload.ChildFieldName ?? string.Empty);
        }

        /// <summary>XMZADD 20260901 忽略大小写比较表端点，保证关系随表重命名时准确定位。</summary>
        private static bool TableEndpointMatches(string schema, string table, string expectedSchema, string expectedTable)
        {
            return string.Equals(schema, expectedSchema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(table, expectedTable, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260901 比较完整字段端点，保证关系随字段重命名或删除时只处理目标关系。</summary>
        private static bool EndpointMatches(
            string schema,
            string table,
            string field,
            string expectedSchema,
            string expectedTable,
            string expectedField)
        {
            return TableEndpointMatches(schema, table, expectedSchema, expectedTable) &&
                string.Equals(field, expectedField, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260901 从已校验的字段结构载荷创建快照字段，保持服务端物理事实一致。</summary>
        private static FieldMetadata CreateField(FieldStructurePayload payload)
        {
            return new FieldMetadata
            {
                FieldName = payload.FieldName,
                OwnerTableName = payload.OwnerTableName,
                DataType = payload.DataType,
                LengthText = payload.LengthText,
                IsRequired = payload.IsRequired,
                IsPrimaryKey = payload.IsPrimaryKey,
                IsForeignKey = payload.IsForeignKey
            };
        }

        /// <summary>XMZADD 20260901 从已校验的关系结构载荷创建快照关系，保留完整父子端点身份。</summary>
        private static RelationMetadata CreateRelation(RelationStructurePayload payload)
        {
            return new RelationMetadata
            {
                ForeignKeyName = payload.ForeignKeyName,
                ParentSchemaName = payload.ParentSchemaName,
                ParentTableName = payload.ParentTableName,
                ParentFieldName = payload.ParentFieldName,
                ChildSchemaName = payload.ChildSchemaName,
                ChildTableName = payload.ChildTableName,
                ChildFieldName = payload.ChildFieldName
            };
        }

        /// <summary>XMZADD 20260901 从服务端当前表生成历史物理载荷，可选包含删除前的字段和唯一关系。</summary>
        private static TableStructurePayload CreateTablePayload(TableMetadata table, bool includeChildren)
        {
            var payload = new TableStructurePayload
            {
                SchemaName = table.SchemaName,
                ObjectName = table.ObjectName,
                ObjectType = table.ObjectType,
                ApproximateRowCount = table.ApproximateRowCount
            };
            if (includeChildren && table.Fields != null)
            {
                for (int index = 0; index < table.Fields.Count; index++)
                {
                    if (table.Fields[index] != null)
                    {
                        payload.Fields.Add(CreateFieldPayload(table.Fields[index]));
                    }
                }
            }
            if (includeChildren && table.Relations != null)
            {
                var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < table.Relations.Count; index++)
                {
                    if (table.Relations[index] == null)
                    {
                        continue;
                    }
                    RelationStructurePayload relation = CreateRelationPayload(table.Relations[index]);
                    if (identities.Add(GetRelationIdentity(relation)))
                    {
                        payload.Relations.Add(relation);
                    }
                }
            }
            return payload;
        }

        /// <summary>XMZADD 20260901 从快照字段提取独立物理载荷，为删除审计和关系处理保留真实结构。</summary>
        private static FieldStructurePayload CreateFieldPayload(FieldMetadata field)
        {
            return new FieldStructurePayload
            {
                FieldName = field.FieldName,
                OwnerTableName = field.OwnerTableName,
                DataType = field.DataType,
                LengthText = field.LengthText,
                IsRequired = field.IsRequired,
                IsPrimaryKey = field.IsPrimaryKey,
                IsForeignKey = field.IsForeignKey
            };
        }

        /// <summary>XMZADD 20260901 从快照关系提取独立端点载荷，为审计和稳定身份比较保留真实结构。</summary>
        private static RelationStructurePayload CreateRelationPayload(RelationMetadata relation)
        {
            return new RelationStructurePayload
            {
                ForeignKeyName = relation.ForeignKeyName,
                ParentSchemaName = relation.ParentSchemaName,
                ParentTableName = relation.ParentTableName,
                ParentFieldName = relation.ParentFieldName,
                ChildSchemaName = relation.ChildSchemaName,
                ChildTableName = relation.ChildTableName,
                ChildFieldName = relation.ChildFieldName
            };
        }

        /// <summary>XMZADD 20260901 创建只含服务端真实值和可信署名的审计历史，不采用客户端 OldValue。</summary>
        private static AppliedDictionaryEvent CreateHistory(
            DictionaryChangeOperation operation,
            long revision,
            string actualOldValue,
            string actualNewValue)
        {
            return new AppliedDictionaryEvent
            {
                OperationId = operation.OperationId,
                AuthorGitHubUserId = operation.AuthorGitHubUserId,
                ObjectKey = operation.ObjectKey,
                FieldKey = operation.FieldKey,
                PropertyName = operation.PropertyName,
                ChangeKind = operation.ChangeKind,
                OldValue = actualOldValue,
                NewValue = actualNewValue,
                Revision = revision,
                AppliedAtUtc = DateTime.UtcNow,
                Evidence = CloneEvidence(operation.Evidence)
            };
        }

        /// <summary>XMZADD 20260901 深拷贝审计历史和结构载荷，使外部修改返回对象不影响服务内部状态。</summary>
        private static AppliedDictionaryEvent CloneHistory(AppliedDictionaryEvent source)
        {
            return new AppliedDictionaryEvent
            {
                OperationId = source.OperationId,
                AuthorGitHubUserId = source.AuthorGitHubUserId,
                ObjectKey = source.ObjectKey,
                FieldKey = source.FieldKey,
                PropertyName = source.PropertyName,
                ChangeKind = source.ChangeKind,
                OldValue = source.OldValue,
                NewValue = source.NewValue,
                Revision = source.Revision,
                AppliedAtUtc = source.AppliedAtUtc,
                OldTablePayload = DictionaryChangeValidator.CloneTablePayload(source.OldTablePayload),
                NewTablePayload = DictionaryChangeValidator.CloneTablePayload(source.NewTablePayload),
                OldFieldPayload = DictionaryChangeValidator.CloneFieldPayload(source.OldFieldPayload),
                NewFieldPayload = DictionaryChangeValidator.CloneFieldPayload(source.NewFieldPayload),
                OldRelationPayload = DictionaryChangeValidator.CloneRelationPayload(source.OldRelationPayload),
                NewRelationPayload = DictionaryChangeValidator.CloneRelationPayload(source.NewRelationPayload),
                Evidence = CloneEvidence(source.Evidence)
            };
        }

        /// <summary>XMZADD 20260901 通过保留对象引用的数据契约克隆创建批量事务工作副本。</summary>
        private static SnapshotData CloneSnapshot(SnapshotData source)
        {
            var serializer = new DataContractSerializer(typeof(SnapshotData), null, int.MaxValue, false, true, null);
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, source);
                stream.Position = 0;
                SnapshotData clone = (SnapshotData)serializer.ReadObject(stream);
                NormalizeMutableCollections(clone);
                return clone;
            }
        }

        /// <summary>XMZADD 20260901 将数据契约为接口集合恢复出的定长数组转换为可增删列表，保证结构事件可在工作副本执行。</summary>
        private static void NormalizeMutableCollections(SnapshotData snapshot)
        {
            snapshot.Tables = CopyList(snapshot.Tables);
            snapshot.ExcludedObjects = CopyList(snapshot.ExcludedObjects);
            snapshot.Abbreviations = CopyList(snapshot.Abbreviations);
            if (snapshot.Tables == null)
            {
                return;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                table.AlternativeChineseNames = CopyInitializedList(table.AlternativeChineseNames);
                table.RejectedSuggestionFingerprints = CopyInitializedList(table.RejectedSuggestionFingerprints);
                table.UsedByModules = CopyInitializedList(table.UsedByModules);
                table.Fields = CopyList(table.Fields);
                table.Relations = CopyList(table.Relations);
                if (table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field != null)
                    {
                        field.AlternativeChineseNames = CopyInitializedList(field.AlternativeChineseNames);
                        field.RejectedSuggestionFingerprints = CopyInitializedList(field.RejectedSuggestionFingerprints);
                        field.EnumItems = CopyList(field.EnumItems);
                    }
                }
            }
            if (snapshot.Abbreviations == null)
            {
                return;
            }
            for (int abbreviationIndex = 0; abbreviationIndex < snapshot.Abbreviations.Count; abbreviationIndex++)
            {
                AbbreviationEntry abbreviation = snapshot.Abbreviations[abbreviationIndex];
                if (abbreviation != null)
                {
                    abbreviation.Evidence = CopyList(abbreviation.Evidence);
                }
            }
        }

        /// <summary>XMZADD 20260901 复制接口集合到传统 List，空集合按可增删空列表处理。</summary>
        private static IList<T> CopyList<T>(IList<T> source)
        {
            var result = new List<T>();
            if (source == null)
            {
                return null;
            }
            for (int index = 0; index < source.Count; index++)
            {
                result.Add(source[index]);
            }
            return result;
        }

        /// <summary>XMZADD 20260910 将新增审阅集合恢复为非空可变列表，保证事件应用后仍可继续维护参考层。</summary>
        private static IList<T> CopyInitializedList<T>(IList<T> source)
        {
            var result = new List<T>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count; index++)
            {
                result.Add(source[index]);
            }
            return result;
        }

        /// <summary>XMZADD 20260901 批量事件全部成功后一次性替换快照状态，保留调用方持有的根对象引用。</summary>
        private static void CopySnapshotState(SnapshotData source, SnapshotData target)
        {
            target.FormatVersion = source.FormatVersion;
            target.Revision = source.Revision;
            target.RefreshedAt = source.RefreshedAt;
            target.Tables = source.Tables;
            target.ExcludedObjects = source.ExcludedObjects;
            target.Abbreviations = source.Abbreviations;
        }
    }
}
