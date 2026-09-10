using System;
using System.Collections.Generic;
using System.IO;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260902 标识完整公开快照存在无字段用户表，避免发布流程依赖异常中文文本分类。</summary>
    internal sealed class IncompletePublicSnapshotException : IOException
    {
        /// <summary>XMZADD 20260902 创建不暴露具体表名的固定结构不完整异常。</summary>
        internal IncompletePublicSnapshotException()
            : base("完整公开快照中的用户表没有可用字段。")
        {
        }
    }

    /// <summary>XMZADD 20260901 返回只包含规范业务对象的新快照及其稳定排除记录。</summary>
    public sealed class SnapshotPruningResult
    {
        public SnapshotData Snapshot { get; internal set; }
        public IList<ExcludedObjectRecord> ExcludedObjects { get; internal set; }
    }

    /// <summary>XMZADD 20260901 按视图、字段、空表依据和人工分类清理统一数据字典快照。</summary>
    public sealed class SnapshotPruningService
    {
        /// <summary>XMZADD 20260902 在没有源码扫描结果时沿用旧业务规则清理快照。</summary>
        public SnapshotPruningResult Prune(SnapshotData snapshot)
        {
            return PruneInternal(snapshot, null, false);
        }

        /// <summary>XMZADD 20260902 一次建立源码证据索引后沿用旧业务规则线性清理快照。</summary>
        public SnapshotPruningResult Prune(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence)
        {
            return PruneInternal(snapshot, sourceEvidence, false);
        }

        /// <summary>XMZADD 20260902 构造保留全部非视图用户表的完整公开快照，并拒绝静默遗漏无字段表。</summary>
        public SnapshotPruningResult PruneCompletePublicSnapshot(SnapshotData snapshot)
        {
            try
            {
                return PruneInternal(snapshot, null, true);
            }
            catch (IncompletePublicSnapshotException exception)
            {
                // 公共裁剪入口保持既有InvalidDataException契约，内部异常仅作为可诊断原因保留。
                throw new InvalidDataException(exception.Message, exception);
            }
        }

        /// <summary>XMZADD 20260902 为发布流程保留结构不完整异常类型，使服务无需依赖公开异常文本分类。</summary>
        internal SnapshotPruningResult PruneCompletePublicSnapshotForPublishing(SnapshotData snapshot)
        {
            return PruneInternal(snapshot, null, true);
        }

        /// <summary>XMZADD 20260902 复用快照复制和引用清理流程，并按调用入口选择旧业务裁剪或完整公开范围。</summary>
        private static SnapshotPruningResult PruneInternal(
            SnapshotData snapshot,
            IList<SourceEvidence> sourceEvidence,
            bool completePublicSnapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            Dictionary<string, ObjectNameResolution> inputObjectNames = BuildInputObjectNameIndex(snapshot.Tables);
            HashSet<string> evidenceObjectKeys = completePublicSnapshot
                ? null
                : BuildEvidenceObjectIndex(sourceEvidence, inputObjectNames);
            var exclusionsByKey = new Dictionary<string, ExcludedObjectRecord>(StringComparer.OrdinalIgnoreCase);
            AddExistingExclusions(snapshot.ExcludedObjects, exclusionsByKey);
            var retainedTables = new List<TableMetadata>();

            if (snapshot.Tables != null)
            {
                for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
                {
                    TableMetadata table = snapshot.Tables[tableIndex];
                    if (table == null)
                    {
                        continue;
                    }

                    string objectKey = MakeObjectKey(table.SchemaName, table.ObjectName);
                    // 当前扫描结果优先于旧排除记录，已恢复对象不能继续显示为排除状态。
                    exclusionsByKey.Remove(objectKey);
                    if (completePublicSnapshot)
                    {
                        if (string.Equals(table.ObjectType, "VIEW", StringComparison.OrdinalIgnoreCase))
                        {
                            // 公开数据字典只承载用户表，视图继续以稳定原因记录为排除对象。
                            exclusionsByKey[objectKey] = CreateExclusion(
                                table,
                                objectKey,
                                "ViewExcluded",
                                "视图不进入规范数据表快照。",
                                snapshot.RefreshedAt);
                            continue;
                        }
                        if (string.Equals(table.ObjectType, "TABLE", StringComparison.OrdinalIgnoreCase) &&
                            !HasUsableField(table.Fields))
                        {
                            // 无字段用户表通常代表扫描不完整，必须中止发布以避免公开快照永久漏表。
                            throw new IncompletePublicSnapshotException();
                        }

                        // 完整公开范围不再用行数、分类或证据裁掉用户表，确保首次和增量结构一致。
                        retainedTables.Add(table);
                        continue;
                    }

                    string reasonCode;
                    string reason;
                    if (TryGetExclusionReason(table, evidenceObjectKeys, out reasonCode, out reason))
                    {
                        exclusionsByKey[objectKey] = CreateExclusion(table, objectKey, reasonCode, reason, snapshot.RefreshedAt);
                    }
                    else
                    {
                        retainedTables.Add(table);
                    }
                }
            }

            var retainedObjectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var retainedFieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BuildRetainedIndexes(retainedTables, retainedObjectKeys, retainedFieldKeys);

            var resultSnapshot = new SnapshotData
            {
                FormatVersion = snapshot.FormatVersion,
                Revision = snapshot.Revision,
                RefreshedAt = snapshot.RefreshedAt,
                Tables = CopyRetainedTables(retainedTables, retainedFieldKeys, inputObjectNames),
                Abbreviations = CopyRetainedAbbreviations(snapshot.Abbreviations, retainedObjectKeys, inputObjectNames),
                ExcludedObjects = CreateOrderedExclusions(exclusionsByKey)
            };

            return new SnapshotPruningResult
            {
                Snapshot = resultSnapshot,
                ExcludedObjects = resultSnapshot.ExcludedObjects
            };
        }

        private static HashSet<string> BuildEvidenceObjectIndex(IList<SourceEvidence> sourceEvidence,
            IDictionary<string, ObjectNameResolution> inputObjectNames)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sourceEvidence == null)
            {
                return result;
            }

            for (int index = 0; index < sourceEvidence.Count; index++)
            {
                SourceEvidence evidence = sourceEvidence[index];
                if (evidence == null || string.IsNullOrWhiteSpace(evidence.ObjectName))
                {
                    continue;
                }

                string objectName = evidence.ObjectName.Trim();
                if (objectName.IndexOf('.') >= 0)
                {
                    result.Add(objectName);
                    continue;
                }

                ObjectNameResolution resolution;
                if (inputObjectNames.TryGetValue(objectName, out resolution) &&
                    resolution.Count == 1 &&
                    !string.IsNullOrWhiteSpace(resolution.SchemaName))
                {
                    // 裸源码表名只有在整个扫描输入中唯一时才可提升为稳定对象键，避免跨 schema 证据串用。
                    result.Add(MakeObjectKey(resolution.SchemaName, objectName));
                }
            }

            return result;
        }

        private static Dictionary<string, ObjectNameResolution> BuildInputObjectNameIndex(
            IList<TableMetadata> tables)
        {
            var result = new Dictionary<string, ObjectNameResolution>(StringComparer.OrdinalIgnoreCase);
            if (tables == null)
            {
                return result;
            }

            for (int index = 0; index < tables.Count; index++)
            {
                TableMetadata table = tables[index];
                if (table == null || string.IsNullOrWhiteSpace(table.ObjectName))
                {
                    continue;
                }

                string objectName = table.ObjectName.Trim();
                ObjectNameResolution resolution;
                if (!result.TryGetValue(objectName, out resolution))
                {
                    resolution = new ObjectNameResolution
                    {
                        Count = 1,
                        SchemaName = table.SchemaName
                    };
                    result.Add(objectName, resolution);
                }
                else
                {
                    resolution.Count++;
                }
            }

            return result;
        }

        private static void AddExistingExclusions(IList<ExcludedObjectRecord> existing,
            IDictionary<string, ExcludedObjectRecord> exclusionsByKey)
        {
            if (existing == null)
            {
                return;
            }

            for (int index = 0; index < existing.Count; index++)
            {
                ExcludedObjectRecord item = existing[index];
                if (item == null || string.IsNullOrWhiteSpace(item.ObjectKey))
                {
                    continue;
                }

                string key = item.ObjectKey.Trim();
                ExcludedObjectRecord candidate = CopyExclusion(item);
                ExcludedObjectRecord current;
                if (!exclusionsByKey.TryGetValue(key, out current) || IsPreferredExclusion(candidate, current))
                {
                    exclusionsByKey[key] = candidate;
                }
            }
        }

        private static bool TryGetExclusionReason(TableMetadata table, ISet<string> evidenceObjectKeys,
            out string reasonCode, out string reason)
        {
            if (string.Equals(table.ObjectType, "VIEW", StringComparison.OrdinalIgnoreCase))
            {
                reasonCode = "ViewExcluded";
                reason = "视图不进入规范数据表快照。";
                return true;
            }

            if (!HasUsableField(table.Fields))
            {
                reasonCode = "NoFields";
                reason = "没有字段的对象不能形成可维护的数据字典。";
                return true;
            }

            if (table.Category == DictionaryTableCategory.Technical)
            {
                reasonCode = "TechnicalCategory";
                reason = "人工分类为技术表，不进入业务数据字典。";
                return true;
            }

            if (table.Category == DictionaryTableCategory.Excluded)
            {
                reasonCode = "ExcludedCategory";
                reason = "人工分类为排除对象。";
                return true;
            }

            if (table.ApproximateRowCount <= 0L &&
                !table.KeepWhenEmpty &&
                !HasDatabaseDescription(table) &&
                !HasSourceEvidence(table, evidenceObjectKeys))
            {
                reasonCode = "EmptyWithoutEvidence";
                reason = "无数据且没有源码、数据库说明或人工保留依据。";
                return true;
            }

            if (table.Category != DictionaryTableCategory.Business &&
                table.Category != DictionaryTableCategory.BaseData)
            {
                reasonCode = "UnclassifiedCategory";
                reason = "尚未确认属于业务表或基础资料表。";
                return true;
            }

            reasonCode = null;
            reason = null;
            return false;
        }

        private static bool HasUsableField(IList<FieldMetadata> fields)
        {
            if (fields == null)
            {
                return false;
            }

            for (int index = 0; index < fields.Count; index++)
            {
                FieldMetadata field = fields[index];
                if (field != null && !string.IsNullOrWhiteSpace(field.FieldName))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasDatabaseDescription(TableMetadata table)
        {
            return table.ChineseName != null &&
                   table.ChineseName.Status == ConfidenceStatus.DatabaseEvidence &&
                   !string.IsNullOrWhiteSpace(table.ChineseName.Value);
        }

        private static bool HasSourceEvidence(TableMetadata table, ISet<string> evidenceObjectKeys)
        {
            if (evidenceObjectKeys == null)
            {
                return false;
            }

            return evidenceObjectKeys.Contains(MakeObjectKey(table.SchemaName, table.ObjectName));
        }

        private static ExcludedObjectRecord CreateExclusion(TableMetadata table, string objectKey,
            string reasonCode, string reason, DateTime excludedAtUtc)
        {
            return new ExcludedObjectRecord
            {
                ObjectKey = objectKey,
                ReasonCode = reasonCode,
                Reason = reason,
                Category = table.Category,
                ApproximateRowCount = table.ApproximateRowCount,
                ExcludedAtUtc = excludedAtUtc
            };
        }

        private static void BuildRetainedIndexes(IList<TableMetadata> retainedTables,
            ISet<string> retainedObjectKeys, ISet<string> retainedFieldKeys)
        {
            for (int tableIndex = 0; tableIndex < retainedTables.Count; tableIndex++)
            {
                TableMetadata table = retainedTables[tableIndex];
                retainedObjectKeys.Add(MakeObjectKey(table.SchemaName, table.ObjectName));
                if (table.Fields == null)
                {
                    continue;
                }

                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field != null && !string.IsNullOrWhiteSpace(field.FieldName))
                    {
                        retainedFieldKeys.Add(MakeFieldKey(table.SchemaName, table.ObjectName, field.FieldName));
                    }
                }
            }
        }

        private static IList<TableMetadata> CopyRetainedTables(IList<TableMetadata> retainedTables,
            ISet<string> retainedFieldKeys, IDictionary<string, ObjectNameResolution> inputObjectNames)
        {
            var result = new List<TableMetadata>();
            for (int index = 0; index < retainedTables.Count; index++)
            {
                result.Add(CopyTable(retainedTables[index], retainedFieldKeys, inputObjectNames));
            }
            return result;
        }

        private static TableMetadata CopyTable(TableMetadata source, ISet<string> retainedFieldKeys,
            IDictionary<string, ObjectNameResolution> inputObjectNames)
        {
            var result = new TableMetadata
            {
                ScopeKey = source.ScopeKey,
                SchemaName = source.SchemaName,
                ObjectName = source.ObjectName,
                ObjectType = source.ObjectType,
                ChineseName = CopyMetadataValue(source.ChineseName),
                ModuleName = CopyMetadataValue(source.ModuleName),
                EntityName = CopyMetadataValue(source.EntityName),
                BusinessMeaning = CopyMetadataValue(source.BusinessMeaning),
                Remark = CopyMetadataValue(source.Remark),
                Category = source.Category,
                ApproximateRowCount = source.ApproximateRowCount,
                KeepWhenEmpty = source.KeepWhenEmpty,
                Fields = new List<FieldMetadata>(),
                Relations = new List<RelationMetadata>()
            };

            if (source.Fields != null)
            {
                for (int index = 0; index < source.Fields.Count; index++)
                {
                    FieldMetadata field = source.Fields[index];
                    if (field != null && !string.IsNullOrWhiteSpace(field.FieldName))
                    {
                        result.Fields.Add(CopyField(field));
                    }
                }
            }

            if (source.Relations != null)
            {
                for (int index = 0; index < source.Relations.Count; index++)
                {
                    RelationMetadata relation = source.Relations[index];
                    string parentSchemaName;
                    string childSchemaName;
                    if (TryResolveRetainedRelation(relation, retainedFieldKeys, inputObjectNames,
                        out parentSchemaName, out childSchemaName))
                    {
                        result.Relations.Add(CopyRelation(relation, parentSchemaName, childSchemaName));
                    }
                }
            }

            return result;
        }

        private static bool TryResolveRetainedRelation(RelationMetadata relation,
            ISet<string> retainedFieldKeys, IDictionary<string, ObjectNameResolution> inputObjectNames,
            out string parentSchemaName, out string childSchemaName)
        {
            parentSchemaName = null;
            childSchemaName = null;
            if (relation == null ||
                !TryResolveSchemaName(relation.ParentSchemaName, relation.ParentTableName,
                    inputObjectNames, out parentSchemaName) ||
                !TryResolveSchemaName(relation.ChildSchemaName, relation.ChildTableName,
                    inputObjectNames, out childSchemaName))
            {
                return false;
            }

            return retainedFieldKeys.Contains(MakeFieldKey(
                       parentSchemaName, relation.ParentTableName, relation.ParentFieldName)) &&
                   retainedFieldKeys.Contains(MakeFieldKey(
                       childSchemaName, relation.ChildTableName, relation.ChildFieldName));
        }

        private static bool TryResolveSchemaName(string schemaName, string objectName,
            IDictionary<string, ObjectNameResolution> inputObjectNames, out string resolvedSchemaName)
        {
            if (!string.IsNullOrWhiteSpace(schemaName))
            {
                resolvedSchemaName = schemaName.Trim();
                return true;
            }

            ObjectNameResolution resolution;
            if (!string.IsNullOrWhiteSpace(objectName) &&
                inputObjectNames.TryGetValue(objectName.Trim(), out resolution) &&
                resolution.Count == 1)
            {
                resolvedSchemaName = resolution.SchemaName;
                return true;
            }

            // 旧快照缺少 schema 且裸表名不唯一时无法确认物理对象，保守丢弃避免错误关联。
            resolvedSchemaName = null;
            return false;
        }

        private static FieldMetadata CopyField(FieldMetadata source)
        {
            var result = new FieldMetadata
            {
                FieldName = source.FieldName,
                ChineseName = CopyMetadataValue(source.ChineseName),
                OwnerTableName = source.OwnerTableName,
                EntityPropertyName = CopyMetadataValue(source.EntityPropertyName),
                BusinessMeaning = CopyMetadataValue(source.BusinessMeaning),
                Usage = CopyMetadataValue(source.Usage),
                DataType = source.DataType,
                LengthText = source.LengthText,
                IsRequired = source.IsRequired,
                IsPrimaryKey = source.IsPrimaryKey,
                IsForeignKey = source.IsForeignKey,
                EnumName = CopyMetadataValue(source.EnumName),
                RelationSummary = CopyMetadataValue(source.RelationSummary),
                Remark = CopyMetadataValue(source.Remark),
                EnumItems = new List<EnumItemMetadata>()
            };

            if (source.EnumItems != null)
            {
                for (int index = 0; index < source.EnumItems.Count; index++)
                {
                    EnumItemMetadata item = source.EnumItems[index];
                    if (item != null)
                    {
                        result.EnumItems.Add(new EnumItemMetadata
                        {
                            Value = item.Value,
                            ChineseName = CopyMetadataValue(item.ChineseName)
                        });
                    }
                }
            }

            return result;
        }

        private static RelationMetadata CopyRelation(RelationMetadata source,
            string parentSchemaName, string childSchemaName)
        {
            return new RelationMetadata
            {
                ScopeKey = source.ScopeKey,
                ForeignKeyName = source.ForeignKeyName,
                ParentSchemaName = parentSchemaName,
                ParentTableName = source.ParentTableName,
                ParentFieldName = source.ParentFieldName,
                ChildSchemaName = childSchemaName,
                ChildTableName = source.ChildTableName,
                ChildFieldName = source.ChildFieldName,
                RelationType = CopyMetadataValue(source.RelationType),
                BusinessMeaning = CopyMetadataValue(source.BusinessMeaning),
                Remark = CopyMetadataValue(source.Remark)
            };
        }

        private static MetadataValue CopyMetadataValue(MetadataValue source)
        {
            if (source == null)
            {
                return null;
            }

            var result = new MetadataValue
            {
                Value = source.Value,
                Description = source.Description,
                Status = source.Status,
                ConfidenceScore = source.ConfidenceScore,
                SourceType = source.SourceType,
                SourceSummary = source.SourceSummary,
                OriginalAutomaticValue = source.OriginalAutomaticValue,
                IsManualOverride = source.IsManualOverride,
                IsLocked = source.IsLocked
            };

            if (source.Evidence == null)
            {
                result.Evidence = null;
                return result;
            }

            result.Evidence = new List<EvidenceItem>();
            for (int index = 0; index < source.Evidence.Count; index++)
            {
                EvidenceItem evidence = source.Evidence[index];
                if (evidence != null)
                {
                    result.Evidence.Add(new EvidenceItem
                    {
                        SourceType = evidence.SourceType,
                        SourcePath = evidence.SourcePath,
                        SourceLine = evidence.SourceLine,
                        RuleName = evidence.RuleName,
                        RawValue = evidence.RawValue,
                        OriginalText = evidence.OriginalText,
                        Explanation = evidence.Explanation
                    });
                }
            }
            return result;
        }

        private static IList<AbbreviationEntry> CopyRetainedAbbreviations(IList<AbbreviationEntry> source,
            ISet<string> retainedObjectKeys, IDictionary<string, ObjectNameResolution> inputObjectNames)
        {
            var result = new List<AbbreviationEntry>();
            if (source == null)
            {
                return result;
            }

            for (int index = 0; index < source.Count; index++)
            {
                AbbreviationEntry item = source[index];
                if (item == null || !IsRetainedAbbreviation(item, retainedObjectKeys, inputObjectNames))
                {
                    continue;
                }

                var copy = new AbbreviationEntry
                {
                    Abbreviation = item.Abbreviation,
                    ChineseMeaning = item.ChineseMeaning,
                    ModuleScope = item.ModuleScope,
                    TableScope = item.TableScope,
                    ConfidenceScore = item.ConfidenceScore,
                    Status = item.Status,
                    Evidence = new List<AbbreviationEvidence>()
                };
                if (item.Evidence != null)
                {
                    for (int evidenceIndex = 0; evidenceIndex < item.Evidence.Count; evidenceIndex++)
                    {
                        AbbreviationEvidence evidence = item.Evidence[evidenceIndex];
                        if (evidence != null)
                        {
                            copy.Evidence.Add(new AbbreviationEvidence
                            {
                                SourceType = evidence.SourceType,
                                RelativePath = evidence.RelativePath,
                                LineNumber = evidence.LineNumber,
                                Summary = evidence.Summary
                            });
                        }
                    }
                }
                result.Add(copy);
            }

            return result;
        }

        private static bool IsRetainedAbbreviation(AbbreviationEntry item,
            ISet<string> retainedObjectKeys, IDictionary<string, ObjectNameResolution> inputObjectNames)
        {
            if (string.IsNullOrWhiteSpace(item.TableScope))
            {
                return true;
            }

            string tableScope = item.TableScope.Trim();
            if (tableScope.IndexOf('.') >= 0)
            {
                return retainedObjectKeys.Contains(tableScope);
            }

            ObjectNameResolution resolution;
            return inputObjectNames.TryGetValue(tableScope, out resolution) &&
                   resolution.Count == 1 &&
                   retainedObjectKeys.Contains(MakeObjectKey(resolution.SchemaName, tableScope));
        }

        private static IList<ExcludedObjectRecord> CreateOrderedExclusions(
            IDictionary<string, ExcludedObjectRecord> exclusionsByKey)
        {
            var result = new List<ExcludedObjectRecord>();
            foreach (KeyValuePair<string, ExcludedObjectRecord> pair in exclusionsByKey)
            {
                result.Add(CopyExclusion(pair.Value));
            }
            result.Sort(CompareExclusions);
            return result;
        }

        private static ExcludedObjectRecord CopyExclusion(ExcludedObjectRecord source)
        {
            return new ExcludedObjectRecord
            {
                ObjectKey = source.ObjectKey,
                ReasonCode = string.IsNullOrWhiteSpace(source.ReasonCode) ? "LegacyExcluded" : source.ReasonCode,
                Reason = string.IsNullOrWhiteSpace(source.Reason) ? "历史排除记录。" : source.Reason,
                Category = source.Category,
                ApproximateRowCount = source.ApproximateRowCount,
                ExcludedAtUtc = source.ExcludedAtUtc
            };
        }

        private static bool IsPreferredExclusion(ExcludedObjectRecord candidate, ExcludedObjectRecord current)
        {
            int comparison = candidate.ExcludedAtUtc.CompareTo(current.ExcludedAtUtc);
            if (comparison != 0)
            {
                // 较新的判断代表对象最后一次已知状态，必须覆盖较早的历史排除原因。
                return comparison > 0;
            }

            comparison = string.Compare(candidate.ReasonCode, current.ReasonCode, StringComparison.Ordinal);
            if (comparison == 0)
            {
                comparison = string.Compare(candidate.Reason, current.Reason, StringComparison.Ordinal);
            }
            if (comparison == 0)
            {
                comparison = candidate.Category.CompareTo(current.Category);
            }
            if (comparison == 0)
            {
                comparison = candidate.ApproximateRowCount.CompareTo(current.ApproximateRowCount);
            }
            if (comparison == 0)
            {
                comparison = string.Compare(candidate.ObjectKey, current.ObjectKey, StringComparison.Ordinal);
            }

            // 同一判断时间使用固定序数规则消除输入顺序影响，确保压缩快照哈希稳定。
            return comparison > 0;
        }

        private static int CompareExclusions(ExcludedObjectRecord left, ExcludedObjectRecord right)
        {
            int comparison = string.Compare(left.ObjectKey, right.ObjectKey, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return comparison;
            }
            return string.Compare(left.ObjectKey, right.ObjectKey, StringComparison.Ordinal);
        }

        private static string MakeObjectKey(string schemaName, string objectName)
        {
            return (schemaName ?? string.Empty) + "." + (objectName ?? string.Empty);
        }

        private static string MakeFieldKey(string schemaName, string objectName, string fieldName)
        {
            return MakeObjectKey(schemaName, objectName) + "\u001F" + (fieldName ?? string.Empty);
        }

        private sealed class ObjectNameResolution
        {
            public int Count { get; set; }
            public string SchemaName { get; set; }
        }
    }
}
