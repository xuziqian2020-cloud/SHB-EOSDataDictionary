using System;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260903 统一判定自动富化候选能否覆盖现有元数据，保护人工和权威证据。</summary>
    public static class MetadataEvidencePolicy
    {
        private static readonly string[] RecognizedBusinessModifiers =
        {
            "货主", "所属", "来源", "目标", "最后", "原始", "操作记录", "操作",
            "创建", "插入", "更新", "删除", "审核", "金蝶", "仓储", "库存"
        };

        /// <summary>XMZADD 20260903 按人工、数据库、确认规则、精确知识、源码和弱推测的顺序判断是否允许替换。</summary>
        public static bool CanReplace(MetadataValue existing, ConfidenceStatus incomingStatus, string incomingRuleName)
        {
            if (existing == null || string.IsNullOrWhiteSpace(existing.Value))
            {
                return true;
            }

            // 人工锁定、数据库注释和已确认值属于权威结论，任何自动批处理都不能覆盖。
            if (existing.IsManualOverride || existing.IsLocked ||
                existing.Status == ConfidenceStatus.LocalOverride ||
                existing.Status == ConfidenceStatus.DatabaseEvidence ||
                existing.Status == ConfidenceStatus.Confirmed)
            {
                return false;
            }

            // 用户明确确认的业务消歧用于纠正既有代码、知识库和命名推测结果。
            if (incomingStatus == ConfidenceStatus.Confirmed &&
                string.Equals(incomingRuleName, "ConfirmedBusinessSemantic", StringComparison.Ordinal))
            {
                return true;
            }

            if (incomingStatus == ConfidenceStatus.DatabaseEvidence || incomingStatus == ConfidenceStatus.LocalOverride)
            {
                return true;
            }

            // 可定位到实体和业务 SQL 用途的上下文证据允许纠正知识库简称，但仍受上方权威值保护。
            if (existing.Status == ConfidenceStatus.KnowledgeBaseEvidence &&
                incomingStatus == ConfidenceStatus.CodeEvidence &&
                string.Equals(incomingRuleName, "BusinessUsageContext", StringComparison.Ordinal))
            {
                return true;
            }

            if (existing.Status == ConfidenceStatus.CodeEvidence)
            {
                return false;
            }

            if (existing.Status == ConfidenceStatus.KnowledgeBaseEvidence)
            {
                // 精确到表或字段的知识条目不可被普通代码候选覆盖，通用词条允许被实体上下文纠正。
                return incomingStatus == ConfidenceStatus.CodeEvidence && !HasExactKnowledgeEvidence(existing);
            }

            if (incomingStatus == ConfidenceStatus.CodeEvidence || incomingStatus == ConfidenceStatus.KnowledgeBaseEvidence)
            {
                return true;
            }

            return existing.Status == ConfidenceStatus.PendingConfirmation ||
                   existing.Status == ConfidenceStatus.Guessed ||
                   existing.Status == ConfidenceStatus.GuessedConflict ||
                   existing.Status == ConfidenceStatus.AiGuessed;
        }

        /// <summary>XMZADD 20260905 按候选完整性、置信度和独立业务证据数量安全细化中文业务名称。</summary>
        public static bool CanReplaceBusinessName(
            MetadataValue existing,
            string candidate,
            int incomingScore,
            int independentEvidenceKindCount,
            string incomingRuleName)
        {
            if (string.IsNullOrWhiteSpace(candidate) || incomingScore < 75)
            {
                return false;
            }

            if (existing == null || string.IsNullOrWhiteSpace(existing.Value))
            {
                return true;
            }

            // 人工维护、人工锁定和已确认名称是业务基线，自动上下文不得改变。
            if (existing.IsManualOverride || existing.IsLocked ||
                existing.Status == ConfidenceStatus.LocalOverride ||
                existing.Status == ConfidenceStatus.Confirmed)
            {
                return false;
            }

            if (string.Equals(existing.Value.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // 弱状态允许在中文文本不变时补齐可靠源码证据，权威状态仍由后续规则保持不变。
                return existing.Status == ConfidenceStatus.PendingConfirmation ||
                       existing.Status == ConfidenceStatus.Guessed ||
                       existing.Status == ConfidenceStatus.GuessedConflict ||
                       existing.Status == ConfidenceStatus.AiGuessed;
            }

            if (existing.Status == ConfidenceStatus.DatabaseEvidence)
            {
                // 纯英文简称和带字段标识符的说明句不是可直接展示的中文注释，只允许精确高分中文短语纠正。
                if (!IdentifierTranslationService.IsReliableChineseName(existing.Value))
                {
                    return incomingScore >= 95 &&
                           IdentifierTranslationService.IsReliableChineseName(candidate);
                }
                return incomingScore >= 85 &&
                       IsCompleteRecognizedRefinement(existing.Value, candidate) &&
                       (IsDirectBusinessEvidence(incomingRuleName) || independentEvidenceKindCount >= 2);
            }

            if (existing.Status == ConfidenceStatus.KnowledgeBaseEvidence)
            {
                // 精确知识只接受保持旧名称完整语义的高置信细化，避免业务代码片段反向改写权威含义。
                return incomingScore >= 85 && IsCompleteRecognizedRefinement(existing.Value, candidate);
            }

            if (existing.Status == ConfidenceStatus.CodeEvidence)
            {
                return incomingScore > existing.ConfidenceScore &&
                       incomingScore >= 85 &&
                       IsCompleteRecognizedRefinement(existing.Value, candidate) &&
                       (IsDirectBusinessEvidence(incomingRuleName) || independentEvidenceKindCount >= 2);
            }

            return existing.Status == ConfidenceStatus.PendingConfirmation ||
                   existing.Status == ConfidenceStatus.Guessed ||
                   existing.Status == ConfidenceStatus.GuessedConflict ||
                   existing.Status == ConfidenceStatus.AiGuessed;
        }

        /// <summary>XMZADD 20260905 判断候选是否完整保留旧中文名称并只增加已识别业务修饰语。</summary>
        private static bool IsCompleteRecognizedRefinement(string existingValue, string candidate)
        {
            string existing = (existingValue ?? string.Empty).Trim();
            string incoming = (candidate ?? string.Empty).Trim();
            if (existing.Length == 0 || incoming.Length <= existing.Length ||
                !incoming.EndsWith(existing, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string addedText = incoming.Substring(0, incoming.Length - existing.Length);
            int offset = 0;
            while (offset < addedText.Length)
            {
                int matchedLength = 0;
                for (int index = 0; index < RecognizedBusinessModifiers.Length; index++)
                {
                    string modifier = RecognizedBusinessModifiers[index];
                    if (modifier.Length > matchedLength &&
                        string.Compare(addedText, offset, modifier, 0, modifier.Length,
                            StringComparison.Ordinal) == 0)
                    {
                        matchedLength = modifier.Length;
                    }
                }
                if (matchedLength == 0)
                {
                    return false;
                }
                offset += matchedLength;
            }
            return offset > 0;
        }

        /// <summary>XMZADD 20260914 识别可单独支撑名称细化的字段关系、界面标题和 SQL 直接列别名。</summary>
        private static bool IsDirectBusinessEvidence(string ruleName)
        {
            return string.Equals(ruleName, "GridColumnCaption", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "DataColumnCaption", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "SqlColumnAlias", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "SqlFieldRelation", StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260903 识别精确到具体表、字段、模块或枚举的项目知识库证据。</summary>
        private static bool HasExactKnowledgeEvidence(MetadataValue value)
        {
            if (value == null || value.Evidence == null)
            {
                return false;
            }
            for (int index = 0; index < value.Evidence.Count; index++)
            {
                EvidenceItem evidence = value.Evidence[index];
                string ruleName = evidence == null ? string.Empty : evidence.RuleName ?? string.Empty;
                if (ruleName == "ExactProjectTable" || ruleName == "ExactProjectField" ||
                    ruleName == "ProjectModuleHeading" || ruleName == "ProjectTableRemark" ||
                    ruleName == "ProjectFieldEnum" || ruleName == "ProjectEnumItem")
                {
                    return true;
                }
            }
            return false;
        }
    }
}
