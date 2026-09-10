using System;
using System.IO;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260909 清理本地审阅证据中的源码原文和本机路径，使完整快照可安全公开。</summary>
    public sealed class PublicSnapshotEvidenceSanitizer
    {
        private const int MaximumEvidencePathLength = 512;

        /// <summary>XMZADD 20260909 遍历快照全部业务元数据并仅移除不可公开的证据细节。</summary>
        public void Sanitize(SnapshotData snapshot)
        {
            if (snapshot == null || snapshot.Tables == null)
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
                SanitizeMetadataValue(table.ChineseName);
                SanitizeMetadataValue(table.ModuleName);
                SanitizeMetadataValue(table.EntityName);
                SanitizeMetadataValue(table.BusinessMeaning);
                SanitizeMetadataValue(table.Remark);
                if (table.Fields != null)
                {
                    for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                    {
                        SanitizeField(table.Fields[fieldIndex]);
                    }
                }
                if (table.Relations != null)
                {
                    for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                    {
                        SanitizeRelation(table.Relations[relationIndex]);
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
                if (abbreviation == null || abbreviation.Evidence == null)
                {
                    continue;
                }
                for (int evidenceIndex = 0; evidenceIndex < abbreviation.Evidence.Count; evidenceIndex++)
                {
                    AbbreviationEvidence evidence = abbreviation.Evidence[evidenceIndex];
                    if (evidence != null)
                    {
                        evidence.RelativePath = SanitizeEvidencePath(evidence.RelativePath);
                    }
                }
            }
        }

        /// <summary>XMZADD 20260909 清理字段名称、用途、枚举和关系摘要中的本地证据细节。</summary>
        private static void SanitizeField(FieldMetadata field)
        {
            if (field == null)
            {
                return;
            }
            SanitizeMetadataValue(field.ChineseName);
            SanitizeMetadataValue(field.EntityPropertyName);
            SanitizeMetadataValue(field.BusinessMeaning);
            SanitizeMetadataValue(field.Usage);
            SanitizeMetadataValue(field.EnumName);
            SanitizeMetadataValue(field.RelationSummary);
            SanitizeMetadataValue(field.Remark);
            if (field.EnumItems == null)
            {
                return;
            }
            for (int enumIndex = 0; enumIndex < field.EnumItems.Count; enumIndex++)
            {
                EnumItemMetadata enumItem = field.EnumItems[enumIndex];
                if (enumItem != null)
                {
                    SanitizeMetadataValue(enumItem.ChineseName);
                }
            }
        }

        /// <summary>XMZADD 20260909 清理关系类型、业务含义和备注中的本地证据细节。</summary>
        private static void SanitizeRelation(RelationMetadata relation)
        {
            if (relation == null)
            {
                return;
            }
            SanitizeMetadataValue(relation.RelationType);
            SanitizeMetadataValue(relation.BusinessMeaning);
            SanitizeMetadataValue(relation.Remark);
        }

        /// <summary>XMZADD 20260909 保留可审计规则和摘要，同时清除源码原文、原值及不安全路径。</summary>
        private static void SanitizeMetadataValue(MetadataValue value)
        {
            if (value == null || value.Evidence == null)
            {
                return;
            }
            for (int evidenceIndex = 0; evidenceIndex < value.Evidence.Count; evidenceIndex++)
            {
                EvidenceItem evidence = value.Evidence[evidenceIndex];
                if (evidence == null)
                {
                    continue;
                }
                evidence.SourcePath = SanitizeEvidencePath(evidence.SourcePath);
                evidence.RawValue = null;
                evidence.OriginalText = null;
            }
        }

        /// <summary>XMZADD 20260909 只保留无盘符、协议和父级跳转的短相对路径供公开追溯。</summary>
        private static string SanitizeEvidencePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEvidencePathLength ||
                Path.IsPathRooted(value) || value.IndexOf(':') >= 0 ||
                value.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return string.Empty;
            }
            return value.Replace('\\', '/');
        }
    }
}
