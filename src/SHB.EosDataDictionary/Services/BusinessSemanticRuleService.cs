using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260903 发布经业务确认的表名消歧规则，避免通用英文直译覆盖真实 EOS 语义。</summary>
    public static class BusinessSemanticRuleService
    {
        /// <summary>XMZADD 20260903 按精确物理表名应用已确认的中文名和模块，同时保护人工及数据库结论。</summary>
        public static void Apply(SnapshotData snapshot)
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
                if (string.Equals(table.ObjectName, "Account_Pallet_FA_Not_IO", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyRule(table, "非托盘出入库流水账", "仓储", "ACCOUNT 在托盘出入库上下文中表示库存流水账，不表示财务");
                }
                else if (string.Equals(table.ObjectName, "DA_Account", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyRule(table, "财务流水账", "财务", "DA_Account 经业务确认表示财务流水账");
                }
            }
        }

        /// <summary>XMZADD 20260903 将单条用户确认结论同时应用到业务中文名和模块归属。</summary>
        private static void ApplyRule(TableMetadata table, string chineseName, string moduleName, string explanation)
        {
            if (MetadataEvidencePolicy.CanReplace(table.ChineseName, ConfidenceStatus.Confirmed, "ConfirmedBusinessSemantic"))
            {
                table.ChineseName = CreateConfirmedValue(chineseName, table.ChineseName, explanation);
            }
            if (MetadataEvidencePolicy.CanReplace(table.ModuleName, ConfidenceStatus.Confirmed, "ConfirmedBusinessSemantic"))
            {
                table.ModuleName = CreateConfirmedValue(moduleName, table.ModuleName, explanation);
            }
        }

        /// <summary>XMZADD 20260903 创建带用户确认来源、原自动值和满分置信度的业务元数据。</summary>
        private static MetadataValue CreateConfirmedValue(string value, MetadataValue existing, string explanation)
        {
            return new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.Confirmed,
                ConfidenceScore = 100,
                SourceType = "用户确认",
                SourceSummary = explanation,
                OriginalAutomaticValue = existing == null ? string.Empty : existing.Value,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "用户确认",
                        SourcePath = "docs/superpowers/specs/2026-09-03-search-entity-scope-semantic-accuracy-design.md",
                        RuleName = "ConfirmedBusinessSemantic",
                        RawValue = value,
                        OriginalText = explanation,
                        Explanation = explanation
                    }
                }
            };
        }
    }
}
