using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260909 在数据库结构重扫缺少源码结果时保留上一规范中可追溯的 EOS 实体映射。</summary>
    public sealed class EntityMappingRetentionService
    {
        /// <summary>XMZADD 20260909 按稳定表键补回可信实体，避免仅因本次源码扫描不完整而清空项目实体范围。</summary>
        public void Apply(SnapshotData previous, SnapshotData current)
        {
            IDictionary<string, TableMetadata> previousTables = BuildLookup(previous);
            if (current == null || current.Tables == null)
            {
                return;
            }

            for (int index = 0; index < current.Tables.Count; index++)
            {
                TableMetadata currentTable = current.Tables[index];
                if (currentTable == null || !IsEmpty(currentTable.EntityName))
                {
                    continue;
                }

                TableMetadata previousTable;
                if (previousTables.TryGetValue(BuildKey(currentTable), out previousTable) &&
                    previousTable != null && HasEosEntityEvidence(previousTable.EntityName))
                {
                    currentTable.EntityName = CloneMetadataValue(previousTable.EntityName);
                }
            }
        }

        /// <summary>XMZADD 20260909 建立不区分大小写的表稳定键索引，使数据库返回大小写变化不会丢失实体映射。</summary>
        private static IDictionary<string, TableMetadata> BuildLookup(SnapshotData snapshot)
        {
            var result = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            if (snapshot == null || snapshot.Tables == null)
            {
                return result;
            }

            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                if (table == null || string.IsNullOrWhiteSpace(table.ObjectName))
                {
                    continue;
                }

                string key = BuildKey(table);
                if (!result.ContainsKey(key))
                {
                    result.Add(key, table);
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260909 生成架构名和对象名组成的稳定键，防止同名跨架构表互相继承。</summary>
        private static string BuildKey(TableMetadata table)
        {
            string schemaName = table == null || string.IsNullOrWhiteSpace(table.SchemaName)
                ? "dbo"
                : table.SchemaName.Trim();
            string objectName = table == null || table.ObjectName == null
                ? string.Empty
                : table.ObjectName.Trim();
            return schemaName + "." + objectName;
        }

        /// <summary>XMZADD 20260909 只把没有实际实体文本的扫描结果视为可补回，保护当前扫描已确认的映射。</summary>
        private static bool IsEmpty(MetadataValue value)
        {
            return value == null || string.IsNullOrWhiteSpace(value.Value);
        }

        /// <summary>XMZADD 20260909 仅接受界面实体筛选认可的源码证据，拒绝命名规则和普通代码使用猜测。</summary>
        private static bool HasEosEntityEvidence(MetadataValue entityName)
        {
            if (entityName == null || string.IsNullOrWhiteSpace(entityName.Value) ||
                entityName.Status != ConfidenceStatus.CodeEvidence)
            {
                return false;
            }
            if (string.Equals(entityName.SourceSummary, "EOS 源码实体类", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (entityName.Evidence == null)
            {
                return false;
            }

            for (int index = 0; index < entityName.Evidence.Count; index++)
            {
                EvidenceItem evidence = entityName.Evidence[index];
                if (evidence != null &&
                    (string.Equals(evidence.RuleName, "KisEntityClass", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(evidence.RuleName, "TableNameProperty", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260909 深复制实体元数据，避免新旧快照共享可变证据集合。</summary>
        private static MetadataValue CloneMetadataValue(MetadataValue source)
        {
            if (source == null)
            {
                return null;
            }
            return new MetadataValue
            {
                Value = source.Value,
                Description = source.Description,
                Status = source.Status,
                ConfidenceScore = source.ConfidenceScore,
                SourceType = source.SourceType,
                SourceSummary = source.SourceSummary,
                OriginalAutomaticValue = source.OriginalAutomaticValue,
                IsManualOverride = source.IsManualOverride,
                IsLocked = source.IsLocked,
                Evidence = CloneEvidence(source.Evidence)
            };
        }

        /// <summary>XMZADD 20260909 深复制公开证据条目，保证后续清理不会回写上一规范快照。</summary>
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
                    RawValue = item.RawValue,
                    OriginalText = item.OriginalText,
                    Explanation = item.Explanation
                });
            }
            return result;
        }
    }
}
