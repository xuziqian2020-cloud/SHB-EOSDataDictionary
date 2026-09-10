using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260903 按明确业务与低价值信号保守重建表分类，避免七万张未知表全部伪装成业务表。</summary>
    public sealed class BusinessTableClassificationService
    {
        /// <summary>XMZADD 20260903 优先保护实体、知识库、数据库和 SQL 使用证据，再识别技术表与可排除表。</summary>
        public void Classify(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence,
            EosProjectKnowledgeCatalog knowledgeCatalog)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }
            ISet<string> sourceBusinessTables = BuildSourceBusinessTableSet(sourceEvidence);
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                bool hasKnowledge = knowledgeCatalog != null && knowledgeCatalog.FindTable(table.ObjectName) != null;
                if (HasPositiveBusinessEvidence(table, sourceBusinessTables, hasKnowledge))
                {
                    table.Category = IsBaseDataModule(table.ModuleName)
                        ? DictionaryTableCategory.BaseData
                        : DictionaryTableCategory.Business;
                    continue;
                }
                if (IsLowValueTableName(table.ObjectName))
                {
                    table.Category = DictionaryTableCategory.Excluded;
                    ApplyClassificationRemark(table, "低价值表名规则", "LowValueTableName");
                }
                else if (IsTechnicalTableName(table.ObjectName))
                {
                    table.Category = DictionaryTableCategory.Technical;
                    ApplyClassificationRemark(table, "技术记录表名规则", "TechnicalTableName");
                }
                else
                {
                    // 没有证据不等于没有业务价值，未知表只降为待识别而不直接排除。
                    table.Category = DictionaryTableCategory.Unclassified;
                }
            }
        }

        /// <summary>XMZADD 20260903 汇总实体映射和实际 SQL 使用表，形成分类阶段的源码正向证据。</summary>
        private static ISet<string> BuildSourceBusinessTableSet(IList<SourceEvidence> sourceEvidence)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sourceEvidence == null)
            {
                return result;
            }
            for (int index = 0; index < sourceEvidence.Count; index++)
            {
                SourceEvidence evidence = sourceEvidence[index];
                if (evidence == null || evidence.Evidence == null || string.IsNullOrWhiteSpace(evidence.ObjectName))
                {
                    continue;
                }
                string ruleName = evidence.Evidence.RuleName;
                if (ruleName == "KisEntityClass" || ruleName == "TableNameProperty" ||
                    ruleName == "SqlTableUsage" || ruleName == "EntityObjectRelation")
                {
                    result.Add(evidence.ObjectName);
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260903 判断表是否已有足够强的业务证据，任何一项成立都会阻止自动排除。</summary>
        private static bool HasPositiveBusinessEvidence(TableMetadata table, ISet<string> sourceTables, bool hasKnowledge)
        {
            if (hasKnowledge || sourceTables.Contains(table.ObjectName ?? string.Empty) ||
                HasEntity(table.EntityName) || HasDatabaseValue(table.ChineseName))
            {
                return true;
            }
            if (table.Relations == null)
            {
                return false;
            }
            for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
            {
                RelationMetadata relation = table.Relations[relationIndex];
                if (relation != null && relation.RelationType != null &&
                    relation.RelationType.Status == ConfidenceStatus.DatabaseEvidence)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260903 识别已成功写入的 EOS 或金蝶实体名。</summary>
        private static bool HasEntity(MetadataValue entityName)
        {
            string value = entityName == null ? string.Empty : entityName.Value ?? string.Empty;
            return value.StartsWith("t_", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Kis_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260903 判断中文名是否来自真实数据库说明。</summary>
        private static bool HasDatabaseValue(MetadataValue value)
        {
            return value != null && !string.IsNullOrWhiteSpace(value.Value) &&
                   (value.Status == ConfidenceStatus.DatabaseEvidence ||
                    value.Status == ConfidenceStatus.Confirmed ||
                    value.Status == ConfidenceStatus.LocalOverride ||
                    value.IsManualOverride);
        }

        /// <summary>XMZADD 20260903 识别明确的临时、备份和复制表命名，仅在无正向证据时生效。</summary>
        private static bool IsLowValueTableName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim().ToUpperInvariant();
            return value.StartsWith("TMP_", StringComparison.Ordinal) ||
                   value.StartsWith("TEMP_", StringComparison.Ordinal) ||
                   value.EndsWith("_TMP", StringComparison.Ordinal) ||
                   value.EndsWith("_TEMP", StringComparison.Ordinal) ||
                   value.EndsWith("_BAK", StringComparison.Ordinal) ||
                   value.EndsWith("_BACKUP", StringComparison.Ordinal) ||
                   value.EndsWith("_COPY", StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260903 识别无业务正向证据的日志、审计、历史和缓存类技术对象。</summary>
        private static bool IsTechnicalTableName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim().ToUpperInvariant();
            return value.StartsWith("LOG_", StringComparison.Ordinal) ||
                   value.EndsWith("_LOG", StringComparison.Ordinal) ||
                   value.IndexOf("_AUDIT", StringComparison.Ordinal) >= 0 ||
                   value.IndexOf("_HISTORY", StringComparison.Ordinal) >= 0 ||
                   value.IndexOf("_CACHE", StringComparison.Ordinal) >= 0;
        }

        /// <summary>XMZADD 20260903 将明确基础资料模块单独分类，便于和业务单据表区分。</summary>
        private static bool IsBaseDataModule(MetadataValue moduleName)
        {
            return moduleName != null && (moduleName.Value ?? string.Empty).IndexOf("基础资料", StringComparison.Ordinal) >= 0;
        }

        /// <summary>XMZADD 20260903 保存自动分类原因，同时保护人工或数据库备注。</summary>
        private static void ApplyClassificationRemark(TableMetadata table, string value, string ruleName)
        {
            if (HasDatabaseValue(table.Remark) || (table.Remark != null && table.Remark.IsLocked))
            {
                return;
            }
            table.Remark = new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.Guessed,
                SourceType = "分类规则",
                SourceSummary = "第一版保守业务分层",
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "分类规则",
                        RuleName = ruleName,
                        RawValue = table.ObjectName,
                        Explanation = value
                    }
                }
            };
        }
    }
}
