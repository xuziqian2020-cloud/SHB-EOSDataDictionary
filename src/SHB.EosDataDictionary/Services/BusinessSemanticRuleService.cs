using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260916 发布经业务代码与用户共同确认的表字段语义，纠正通用直译产生的错误正式名称。</summary>
    public static class BusinessSemanticRuleService
    {
        /// <summary>XMZADD 20260916 按精确物理对象应用已确认的中文名和模块，同时保护人工锁定结论。</summary>
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
                    ApplyRule(table, "非托盘出入库流水账", "仓储与库存", "ACCOUNT 在托盘出入库上下文中表示库存流水账，不表示财务");
                }
                else if (string.Equals(table.ObjectName, "DA_Account", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyRule(table, "财务流水账", "财务管理", "DA_Account 经业务确认表示财务流水账");
                }
                ApplyConfirmedFieldRules(table);
            }
        }

        /// <summary>XMZADD 20260915 应用已由用户和 EOS 业务上下文共同确认的精确字段名称。</summary>
        private static void ApplyConfirmedFieldRules(TableMetadata table)
        {
            if (table == null || table.Fields == null)
            {
                return;
            }
            for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
            {
                FieldMetadata field = table.Fields[fieldIndex];
                if (field == null || string.IsNullOrWhiteSpace(field.FieldName))
                {
                    continue;
                }
                if (string.Equals(table.ObjectName, "DA_Acceptance", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(field.FieldName, "Owner_Company_ID", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyFieldRule(field, "货主公司ID",
                        "DA_Acceptance 按持有公司筛选承兑汇票，并关联 Company.Company_ID，Owner 表示货主。");
                }
                else if (string.Equals(field.FieldName, "op_createtime", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(field.FieldName, "op_create_time", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyFieldRule(field, "操作记录创建时间",
                        "op 是 EOS 操作记录前缀，createtime 表示该操作记录的创建时间。");
                }
            }
        }

        /// <summary>XMZADD 20260915 将精确字段结论送入统一名称分层并保留被替换的自动名称供审阅。</summary>
        private static void ApplyFieldRule(FieldMetadata field, string chineseName, string explanation)
        {
            MetadataValue previousName = SelectPreviousFieldName(field);
            if ((previousName == null || string.IsNullOrWhiteSpace(previousName.Value)) &&
                field.SuggestedChineseName != null &&
                !string.IsNullOrWhiteSpace(field.SuggestedChineseName.Value))
            {
                // 名称准入已把历史伪中文移到参考列时，仍需保留它作为人工确认前的自动值。
                previousName = field.SuggestedChineseName;
            }
            MetadataValue candidate = CreateConfirmedValue(chineseName, previousName, explanation);
            new BusinessNameLayerService().ApplyFieldCandidate(field, candidate);
        }

        /// <summary>XMZADD 20260916 从正式名和参考层追溯规则修正前的代码名称，避免多轮翻译掩盖最初伪中文。</summary>
        private static MetadataValue SelectPreviousFieldName(FieldMetadata field)
        {
            if (field == null)
            {
                return null;
            }
            if (field.ChineseName != null && !string.IsNullOrWhiteSpace(field.ChineseName.Value))
            {
                return field.ChineseName;
            }
            if (IsUntranslatedCodeReference(field.SuggestedChineseName))
            {
                return field.SuggestedChineseName;
            }
            if (field.AlternativeChineseNames != null)
            {
                for (int index = 0; index < field.AlternativeChineseNames.Count; index++)
                {
                    MetadataValue alternative = field.AlternativeChineseNames[index];
                    if (IsUntranslatedCodeReference(alternative))
                    {
                        return alternative;
                    }
                }
            }
            return field.SuggestedChineseName;
        }

        /// <summary>XMZADD 20260916 判断参考候选是否为源码形成但仍带英文残片的历史自动名称。</summary>
        private static bool IsUntranslatedCodeReference(MetadataValue value)
        {
            return value != null && value.Status == ConfidenceStatus.CodeEvidence &&
                   !string.IsNullOrWhiteSpace(value.Value) &&
                   !IdentifierTranslationService.IsReliableChineseName(value.Value);
        }

        /// <summary>XMZADD 20260916 将单条用户确认结论同时应用到业务中文名和模块归属。</summary>
        private static void ApplyRule(TableMetadata table, string chineseName, string moduleName, string explanation)
        {
            // 人工确认是最高等级名称证据，应纠正历史数据库误注释，同时由名称分层继续保护人工锁定值。
            MetadataValue nameCandidate = CreateConfirmedValue(chineseName, table.ChineseName, explanation);
            new BusinessNameLayerService().ApplyTableCandidate(table, nameCandidate);
            if (MetadataEvidencePolicy.CanReplace(table.ModuleName, ConfidenceStatus.Confirmed, "ConfirmedBusinessSemantic"))
            {
                table.ModuleName = CreateConfirmedValue(moduleName, table.ModuleName, explanation);
            }
        }

        /// <summary>XMZADD 20260916 创建带用户确认来源、最初自动值和满分置信度的业务元数据。</summary>
        private static MetadataValue CreateConfirmedValue(string value, MetadataValue existing, string explanation)
        {
            string originalAutomaticValue = string.Empty;
            if (existing != null)
            {
                // 多阶段细化必须追溯到最初自动名称，不能把上一阶段已纠正的同名结果当成原值。
                originalAutomaticValue = string.IsNullOrWhiteSpace(existing.OriginalAutomaticValue)
                    ? existing.Value
                    : existing.OriginalAutomaticValue;
            }
            return new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.Confirmed,
                ConfidenceScore = 100,
                SourceType = "用户确认",
                SourceSummary = explanation,
                OriginalAutomaticValue = originalAutomaticValue,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "用户确认",
                        SourcePath = "docs/superpowers/specs/2026-09-09-eos-dictionary-accuracy-v6-design.md",
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
