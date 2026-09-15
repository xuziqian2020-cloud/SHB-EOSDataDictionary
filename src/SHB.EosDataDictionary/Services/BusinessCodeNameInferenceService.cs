using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260904 结合所属实体和源码业务用途生成上下文相关的中文表字段名称。</summary>
    public sealed class BusinessCodeNameInferenceService
    {
        private const string BusinessUsageRule = "BusinessUsageContext";
        private const string MissingChineseName = "暂无可靠中文名称";
        private static readonly Regex IdentifierPartRegex = new Regex(
            @"[A-Z]+(?=[A-Z][a-z]|\d|$)|[A-Z]?[a-z]+|\d+",
            RegexOptions.Compiled);

        /// <summary>XMZADD 20260904 一次建立源码证据索引并按所属表上下文补全名称，避免逐表重复扫描全部源码证据。</summary>
        public void Apply(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence)
        {
            if (snapshot == null || snapshot.Tables == null || sourceEvidence == null)
            {
                return;
            }

            var tableEvidence = new Dictionary<string, SourceEvidence>(StringComparer.OrdinalIgnoreCase);
            var fieldEvidence = new Dictionary<string, List<SourceEvidence>>(StringComparer.OrdinalIgnoreCase);
            BuildEvidenceIndexes(sourceEvidence, tableEvidence, fieldEvidence);

            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || string.IsNullOrWhiteSpace(table.ObjectName))
                {
                    continue;
                }

                SourceEvidence entityEvidence;
                tableEvidence.TryGetValue(table.ObjectName, out entityEvidence);
                if (entityEvidence != null)
                {
                    ApplyKnownTableContext(table, entityEvidence);
                }
                ApplyFieldContexts(table, entityEvidence, fieldEvidence);
            }
        }

        /// <summary>XMZADD 20260905 一次聚合表字段全部业务证据并解析无表名界面标题，控制全量批处理复杂度。</summary>
        private static void BuildEvidenceIndexes(IList<SourceEvidence> sourceEvidence,
            IDictionary<string, SourceEvidence> tableEvidence,
            IDictionary<string, List<SourceEvidence>> fieldEvidence)
        {
            var fieldOwnersByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var existingCaptionConflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < sourceEvidence.Count; index++)
            {
                SourceEvidence evidence = sourceEvidence[index];
                if (evidence == null || evidence.Evidence == null)
                {
                    continue;
                }

                string ruleName = evidence.Evidence.RuleName ?? string.Empty;
                if (ruleName == "GridColumnCaptionConflict" || ruleName == "DataColumnCaptionConflict")
                {
                    existingCaptionConflicts.Add(MakeFileFieldKey(
                        evidence.Evidence.SourcePath, evidence.FieldName));
                }
                bool isEntityEvidence = ruleName == "EntityClassConvention" ||
                                        ruleName == "TableNameProperty" ||
                                        ruleName == "KisEntityClass";
                if (isEntityEvidence && !string.IsNullOrWhiteSpace(evidence.ObjectName) &&
                    !tableEvidence.ContainsKey(evidence.ObjectName))
                {
                    tableEvidence.Add(evidence.ObjectName, evidence);
                }

                if (!string.IsNullOrWhiteSpace(evidence.ObjectName) &&
                    !string.IsNullOrWhiteSpace(evidence.FieldName))
                {
                    if (IsFieldNamingEvidence(ruleName))
                    {
                        AddFieldEvidence(fieldEvidence, evidence.ObjectName, evidence.FieldName, evidence);
                    }
                    if (IsFieldNamingEvidence(ruleName) || ruleName == "SqlFieldUsage")
                    {
                        AddFieldOwner(fieldOwnersByFile, evidence);
                    }
                }
            }

            var conflicts = new List<SourceEvidence>();
            for (int index = 0; index < sourceEvidence.Count; index++)
            {
                SourceEvidence evidence = sourceEvidence[index];
                if (!IsUnboundCaptionEvidence(evidence))
                {
                    continue;
                }

                HashSet<string> owners;
                string ownerKey = MakeFileFieldKey(evidence.Evidence.SourcePath, evidence.FieldName);
                if (!fieldOwnersByFile.TryGetValue(ownerKey, out owners) || owners.Count == 0)
                {
                    continue;
                }

                if (owners.Count == 1)
                {
                    AddFieldEvidence(fieldEvidence, GetOnlyValue(owners), evidence.FieldName, evidence);
                }
                else if (!existingCaptionConflicts.Contains(ownerKey))
                {
                    conflicts.Add(CreateCaptionConflict(evidence));
                    existingCaptionConflicts.Add(ownerKey);
                }
            }

            // 冲突必须回写到共享证据集合，后续审计才能解释为何界面中文标题未参与自动命名。
            if (!sourceEvidence.IsReadOnly)
            {
                for (int index = 0; index < conflicts.Count; index++)
                {
                    sourceEvidence.Add(conflicts[index]);
                }
            }

            foreach (KeyValuePair<string, List<SourceEvidence>> pair in fieldEvidence)
            {
                pair.Value.Sort(CompareEvidencePriority);
            }
        }

        /// <summary>XMZADD 20260905 限定可参与中文命名的字段证据类型，排除仅表示查询出现的弱用途记录。</summary>
        private static bool IsFieldNamingEvidence(string ruleName)
        {
            return IsCaptionRule(ruleName) || ruleName == "SqlFieldRelation" ||
                   ruleName == "EntityFieldAssignment" || ruleName == "DynamicTableFieldUsage" ||
                   ruleName == "EntityProperty";
        }

        /// <summary>XMZADD 20260905 识别字段与中文界面标题的直接绑定规则。</summary>
        private static bool IsCaptionRule(string ruleName)
        {
            return ruleName == "GridColumnCaption" || ruleName == "DataColumnCaption" ||
                   ruleName == "SqlColumnAlias";
        }

        /// <summary>XMZADD 20260905 将一条字段证据追加到大小写不敏感的表字段联合索引。</summary>
        private static void AddFieldEvidence(IDictionary<string, List<SourceEvidence>> fieldEvidence,
            string objectName, string fieldName, SourceEvidence evidence)
        {
            string key = MakeFieldKey(objectName, fieldName);
            List<SourceEvidence> values;
            if (!fieldEvidence.TryGetValue(key, out values))
            {
                values = new List<SourceEvidence>();
                fieldEvidence.Add(key, values);
            }
            values.Add(evidence);
        }

        /// <summary>XMZADD 20260905 记录同文件同字段的表归属，用于解析缺少表名的界面绑定。</summary>
        private static void AddFieldOwner(IDictionary<string, HashSet<string>> fieldOwnersByFile,
            SourceEvidence evidence)
        {
            string key = MakeFileFieldKey(evidence.Evidence.SourcePath, evidence.FieldName);
            HashSet<string> owners;
            if (!fieldOwnersByFile.TryGetValue(key, out owners))
            {
                owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                fieldOwnersByFile.Add(key, owners);
            }
            owners.Add(evidence.ObjectName);
        }

        /// <summary>XMZADD 20260905 判断证据是否为仍需根据同文件字段归属解析的无表名界面标题。</summary>
        private static bool IsUnboundCaptionEvidence(SourceEvidence evidence)
        {
            return evidence != null && evidence.Evidence != null &&
                   string.IsNullOrWhiteSpace(evidence.ObjectName) &&
                   !string.IsNullOrWhiteSpace(evidence.FieldName) &&
                   !string.IsNullOrWhiteSpace(evidence.ChineseNameCandidate) &&
                   IsCaptionRule(evidence.Evidence.RuleName);
        }

        /// <summary>XMZADD 20260905 为多表同名字段构造不参与命名的界面标题冲突审计记录。</summary>
        private static SourceEvidence CreateCaptionConflict(SourceEvidence source)
        {
            EvidenceItem item = source.Evidence;
            return new SourceEvidence
            {
                FieldName = source.FieldName,
                ModulePath = source.ModulePath,
                BusinessIdentifierCandidate = source.FieldName,
                Strength = source.Strength,
                UsageKind = source.UsageKind,
                Evidence = new EvidenceItem
                {
                    SourceType = item.SourceType,
                    SourcePath = item.SourcePath,
                    SourceLine = item.SourceLine,
                    RuleName = item.RuleName + "Conflict",
                    RawValue = item.RawValue,
                    OriginalText = item.OriginalText,
                    Explanation = "同文件同名字段关联多张业务表，界面标题仅保留冲突审计且不参与自动命名。"
                }
            };
        }

        /// <summary>XMZADD 20260905 返回唯一字段归属，调用方已保证集合只有一个元素。</summary>
        private static string GetOnlyValue(IEnumerable<string> values)
        {
            foreach (string value in values)
            {
                return value;
            }
            return null;
        }

        /// <summary>XMZADD 20260905 按业务价值固定字段证据顺序，确保界面绑定和关系证据优先参与候选选择。</summary>
        private static int CompareEvidencePriority(SourceEvidence left, SourceEvidence right)
        {
            int leftPriority = GetEvidencePriority(left);
            int rightPriority = GetEvidencePriority(right);
            if (leftPriority != rightPriority)
            {
                return leftPriority.CompareTo(rightPriority);
            }
            string leftPath = left == null || left.Evidence == null ? string.Empty : left.Evidence.SourcePath ?? string.Empty;
            string rightPath = right == null || right.Evidence == null ? string.Empty : right.Evidence.SourcePath ?? string.Empty;
            int pathComparison = string.Compare(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
            if (pathComparison != 0)
            {
                return pathComparison;
            }
            int leftLine = left == null || left.Evidence == null ? 0 : left.Evidence.SourceLine;
            int rightLine = right == null || right.Evidence == null ? 0 : right.Evidence.SourceLine;
            return leftLine.CompareTo(rightLine);
        }

        /// <summary>XMZADD 20260905 将业务证据规则映射为固定优先级，未知规则不得抢占明确绑定。</summary>
        private static int GetEvidencePriority(SourceEvidence evidence)
        {
            string ruleName = evidence == null || evidence.Evidence == null
                ? string.Empty
                : evidence.Evidence.RuleName ?? string.Empty;
            if (IsCaptionRule(ruleName)) return 0;
            if (ruleName == "SqlFieldRelation") return 1;
            if (ruleName == "EntityFieldAssignment") return 2;
            if (ruleName == "DynamicTableFieldUsage") return 3;
            if (ruleName == "EntityProperty") return 4;
            return 5;
        }

        /// <summary>XMZADD 20260905 生成同文件字段归属键，避免不同窗体中的同名界面字段互相绑定。</summary>
        private static string MakeFileFieldKey(string sourcePath, string fieldName)
        {
            return (sourcePath ?? string.Empty) + "\u001f" + (fieldName ?? string.Empty);
        }

        /// <summary>XMZADD 20260904 应用已由 EOS 动态建表代码证明的仓储配置表语义和业务模块。</summary>
        private static void ApplyKnownTableContext(TableMetadata table, SourceEvidence evidence)
        {
            if (!string.Equals(table.ObjectName, "Account_Storage_Part_Definition", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyValue(
                delegate { return table.ChineseName; },
                delegate(MetadataValue value) { table.ChineseName = value; },
                "仓储区分表配置",
                evidence,
                "EOS 按事业部保存仓储区事件、流水和库存动态表名。");
            ApplyValue(
                delegate { return table.ModuleName; },
                delegate(MetadataValue value) { table.ModuleName = value; },
                "仓储与库存",
                evidence,
                "动态仓储区业务表由仓储与库存流程创建和访问。");
        }

        /// <summary>XMZADD 20260904 按精确业务用途、实体首字母和动态表角色补全字段中文名称。</summary>
        private static void ApplyFieldContexts(TableMetadata table, SourceEvidence entityEvidence,
            IDictionary<string, List<SourceEvidence>> fieldEvidence)
        {
            if (table.Fields == null)
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

                List<SourceEvidence> specificEvidence;
                fieldEvidence.TryGetValue(MakeFieldKey(table.ObjectName, field.FieldName), out specificEvidence);
                if (entityEvidence == null && (specificEvidence == null || specificEvidence.Count == 0))
                {
                    // 没有任何 EOS 业务代码归属时不得仅凭同名字段把外部或历史对象提升为源码结论。
                    continue;
                }
                string knownName = GetKnownFieldName(table.ObjectName, field.FieldName);
                if (!string.IsNullOrWhiteSpace(knownName))
                {
                    ApplyFieldValue(field, knownName, 90, specificEvidence, entityEvidence,
                        "字段名称来自所属仓储配置实体及动态 SQL 的真实用途。");
                    continue;
                }

                BusinessFieldNameResult semanticResult = specificEvidence == null || specificEvidence.Count == 0
                    ? null
                    : InferBusinessFieldName(table, field, specificEvidence);
                if (IsUsableBusinessResult(semanticResult))
                {
                    int evidenceKindCount = CountSupportingEvidenceKinds(
                        specificEvidence, field.FieldName, semanticResult.Value);
                    int confidenceScore = CalculateAggregatedScore(semanticResult.ConfidenceScore, evidenceKindCount);
                    if (CountIndependentCaptionSources(
                            specificEvidence, field.FieldName, semanticResult.Value) >= 2)
                    {
                        // 两个独立业务文件给出相同标题时，可排除单个窗体偶然简写造成的低可信结论。
                        confidenceScore = Math.Max(confidenceScore, 90);
                    }
                    string directRuleName = GetHighestPrioritySupportingRule(
                        specificEvidence, field.FieldName, semanticResult.Value);
                    ApplyFieldValue(field, semanticResult.Value, confidenceScore, specificEvidence, entityEvidence,
                        semanticResult.Explanation, directRuleName, evidenceKindCount);
                    ApplyFieldUsage(field, specificEvidence, confidenceScore);
                    continue;
                }

                string acronymName = InferAcronymFieldName(table, field.FieldName);
                if (!string.IsNullOrWhiteSpace(acronymName))
                {
                    ApplyFieldValue(field, acronymName, 85, specificEvidence, entityEvidence,
                        "字段前缀与所属实体英文名称首字母完全匹配。");
                    continue;
                }

                if (specificEvidence != null && specificEvidence.Count > 0 &&
                    field.FieldName.StartsWith("Table_", StringComparison.OrdinalIgnoreCase))
                {
                    string translated = IdentifierTranslationService.TranslateTableName(field.FieldName.Substring(6));
                    if (!string.Equals(translated, MissingChineseName, StringComparison.Ordinal))
                    {
                        ApplyFieldValue(field, RemoveTrailingTable(translated) + "表名", 88,
                            specificEvidence, entityEvidence,
                            "字段在业务代码中作为动态 SQL 的目标表名。");
                    }
                }
            }
        }

        /// <summary>XMZADD 20260905 综合字段完整语法、关系目标和界面中文标题生成可用于安全覆盖的业务候选。</summary>
        private static BusinessFieldNameResult InferBusinessFieldName(TableMetadata table, FieldMetadata field,
            IList<SourceEvidence> evidence)
        {
            string relatedTableName = null;
            string relatedFieldName = null;
            string businessIdentifierCandidate = null;
            string captionCandidate = null;
            bool relationConflict = false;
            if (evidence != null)
            {
                for (int index = 0; index < evidence.Count; index++)
                {
                    SourceEvidence item = evidence[index];
                    if (item == null || item.Evidence == null)
                    {
                        continue;
                    }

                    if (item.Evidence.RuleName == "SqlFieldRelation" &&
                        !string.IsNullOrWhiteSpace(item.RelationTargetObjectName))
                    {
                        if (relatedTableName == null)
                        {
                            relatedTableName = item.RelationTargetObjectName;
                            relatedFieldName = item.RelationTargetFieldName;
                        }
                        else if (!string.Equals(relatedTableName, item.RelationTargetObjectName,
                                     StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(relatedFieldName, item.RelationTargetFieldName,
                                     StringComparison.OrdinalIgnoreCase))
                        {
                            relationConflict = true;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(businessIdentifierCandidate) &&
                        !string.IsNullOrWhiteSpace(item.BusinessIdentifierCandidate))
                    {
                        businessIdentifierCandidate = item.BusinessIdentifierCandidate;
                    }

                    if (IsCaptionRule(item.Evidence.RuleName) &&
                        !string.IsNullOrWhiteSpace(item.ChineseNameCandidate))
                    {
                        string completedCaption = CompleteCaptionValue(item.ChineseNameCandidate, field.FieldName);
                        if (captionCandidate == null)
                        {
                            captionCandidate = completedCaption;
                        }
                        else if (!string.Equals(captionCandidate, completedCaption, StringComparison.Ordinal))
                        {
                            // 两个直接绑定标题含义不一致时不选择任一标题，保留字段语法和关系的保守结论。
                            captionCandidate = string.Empty;
                        }
                    }
                }
            }

            if (relationConflict)
            {
                relatedTableName = "__RelationConflict";
                relatedFieldName = "__RelationConflict";
            }

            var context = new BusinessFieldNameContext
            {
                TableName = table.ObjectName,
                TableChineseName = table.ChineseName == null ? null : table.ChineseName.Value,
                ModuleName = table.ModuleName == null ? null : table.ModuleName.Value,
                FieldName = field.FieldName,
                DataType = field.DataType,
                RelatedTableName = relatedTableName,
                RelatedFieldName = relatedFieldName,
                BusinessIdentifierCandidate = businessIdentifierCandidate
            };
            BusinessFieldNameResult semanticResult = new BusinessIdentifierSemanticService().Infer(context);
            if (!string.IsNullOrWhiteSpace(captionCandidate) &&
                IdentifierTranslationService.IsReliableChineseName(captionCandidate))
            {
                if (IsUsableBusinessResult(semanticResult) &&
                    (string.Equals(captionCandidate, semanticResult.Value, StringComparison.Ordinal) ||
                     captionCandidate.IndexOf(semanticResult.Value, StringComparison.Ordinal) >= 0 ||
                     semanticResult.Value.IndexOf(captionCandidate, StringComparison.Ordinal) >= 0))
                {
                    return new BusinessFieldNameResult(
                        semanticResult.Value.Length >= captionCandidate.Length
                            ? semanticResult.Value
                            : captionCandidate,
                        Math.Max(semanticResult.ConfidenceScore, 88),
                        "GridColumnCaption",
                        semanticResult.Explanation + " 界面列标题与完整字段语义一致。",
                        semanticResult.UnknownTokens);
                }

                return new BusinessFieldNameResult(captionCandidate, 88, "GridColumnCaption",
                    "字段与中文界面列标题直接绑定，并按字段值类型保留完整后缀。", new List<string>());
            }
            return semanticResult;
        }

        /// <summary>XMZADD 20260905 判断语义候选是否达到自动写入门槛且不包含未解释英文片段。</summary>
        private static bool IsUsableBusinessResult(BusinessFieldNameResult result)
        {
            return result != null && result.ConfidenceScore >= 75 &&
                   result.UnknownTokens != null && result.UnknownTokens.Count == 0 &&
                   IdentifierTranslationService.IsReliableChineseName(result.Value);
        }

        /// <summary>XMZADD 20260905 在界面标题遗漏技术值后缀时依据字段结构补齐ID，防止“货主公司”丢失字段类型。</summary>
        private static string CompleteCaptionValue(string caption, string fieldName)
        {
            string value = (caption ?? string.Empty).Trim();
            string name = (fieldName ?? string.Empty).Trim();
            bool isIdField = name.EndsWith("_ID", StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith("ID", StringComparison.OrdinalIgnoreCase);
            if (isIdField && !value.EndsWith("ID", StringComparison.OrdinalIgnoreCase))
            {
                return value + "ID";
            }
            return value;
        }

        /// <summary>XMZADD 20260905 根据独立业务证据类型数量提升一致结论分数，同时保留精确短语原有高分。</summary>
        private static int CalculateAggregatedScore(int baseScore, int evidenceKindCount)
        {
            if (baseScore >= 95)
            {
                return baseScore;
            }
            if (evidenceKindCount >= 3)
            {
                return Math.Max(baseScore, 92);
            }
            if (evidenceKindCount >= 2)
            {
                return Math.Max(baseScore, 90);
            }
            return baseScore;
        }

        /// <summary>XMZADD 20260905 统计真正支持当前候选的独立业务证据类别，排除实体结构和同类控件重复计数。</summary>
        private static int CountSupportingEvidenceKinds(IList<SourceEvidence> evidence,
            string fieldName, string candidate)
        {
            if (evidence == null)
            {
                return 0;
            }
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < evidence.Count; index++)
            {
                string kind = GetSupportingEvidenceKind(evidence[index], fieldName, candidate);
                if (!string.IsNullOrWhiteSpace(kind))
                {
                    kinds.Add(kind);
                }
            }
            return kinds.Count;
        }

        /// <summary>XMZADD 20260914 统计给出同一字段标题的独立业务文件，避免同文件重复控件虚增可信度。</summary>
        private static int CountIndependentCaptionSources(IList<SourceEvidence> evidence,
            string fieldName, string candidate)
        {
            if (evidence == null || string.IsNullOrWhiteSpace(candidate))
            {
                return 0;
            }
            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceFingerprints = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < evidence.Count; index++)
            {
                SourceEvidence item = evidence[index];
                if (item == null || item.Evidence == null ||
                    !IsCaptionRule(item.Evidence.RuleName) ||
                    string.IsNullOrWhiteSpace(item.Evidence.SourcePath))
                {
                    continue;
                }
                string caption = CompleteCaptionValue(item.ChineseNameCandidate, fieldName);
                if (string.Equals(caption, candidate.Trim(), StringComparison.Ordinal))
                {
                    string fingerprint = BuildCaptionSourceFingerprint(item.Evidence);
                    if (!sourcePaths.Contains(item.Evidence.SourcePath) &&
                        !sourceFingerprints.Contains(fingerprint))
                    {
                        sourcePaths.Add(item.Evidence.SourcePath);
                        sourceFingerprints.Add(fingerprint);
                    }
                }
            }
            return sourcePaths.Count;
        }

        /// <summary>XMZADD 20260915 归一化标题所在源码文本，防止复制到多个文件的同一 SQL 虚增独立证据数。</summary>
        private static string BuildCaptionSourceFingerprint(EvidenceItem evidence)
        {
            string value = evidence == null ? string.Empty : evidence.OriginalText;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = evidence == null ? string.Empty : evidence.RawValue;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                return "PATH:" + (evidence == null ? string.Empty : evidence.SourcePath ?? string.Empty).ToUpperInvariant();
            }

            var result = new StringBuilder();
            bool pendingSpace = false;
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsWhiteSpace(value[index]))
                {
                    pendingSpace = result.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    result.Append(' ');
                    pendingSpace = false;
                }
                result.Append(char.ToUpperInvariant(value[index]));
            }
            return result.ToString();
        }

        /// <summary>XMZADD 20260905 返回按优先级排序后第一条真正支持当前候选的业务证据规则。</summary>
        private static string GetHighestPrioritySupportingRule(IList<SourceEvidence> evidence,
            string fieldName, string candidate)
        {
            if (evidence == null)
            {
                return BusinessUsageRule;
            }
            for (int index = 0; index < evidence.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(GetSupportingEvidenceKind(evidence[index], fieldName, candidate)))
                {
                    return evidence[index].Evidence.RuleName ?? BusinessUsageRule;
                }
            }
            return BusinessUsageRule;
        }

        /// <summary>XMZADD 20260905 将候选相关证据归并为界面绑定、字段关系、实体赋值或动态配置四类。</summary>
        private static string GetSupportingEvidenceKind(SourceEvidence evidence,
            string fieldName, string candidate)
        {
            if (evidence == null || evidence.Evidence == null || string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            string ruleName = evidence.Evidence.RuleName ?? string.Empty;
            if (IsCaptionRule(ruleName))
            {
                string caption = CompleteCaptionValue(evidence.ChineseNameCandidate, fieldName);
                return string.Equals(caption, candidate.Trim(), StringComparison.Ordinal)
                    ? "InterfaceCaption"
                    : null;
            }
            if (ruleName == "SqlFieldRelation")
            {
                string targetFieldName = evidence.RelationTargetFieldName;
                if (string.IsNullOrWhiteSpace(targetFieldName) ||
                    !IdentifierTranslationService.IsFieldNameFullyTranslated(targetFieldName))
                {
                    return null;
                }
                string targetName = IdentifierTranslationService.TranslateFieldName(targetFieldName);
                return candidate.EndsWith(targetName, StringComparison.Ordinal)
                    ? "SqlFieldRelation"
                    : null;
            }
            if (ruleName == "EntityFieldAssignment" || ruleName == "DynamicTableFieldUsage")
            {
                string identifier = evidence.BusinessIdentifierCandidate;
                if (string.IsNullOrWhiteSpace(identifier) ||
                    !IdentifierTranslationService.IsFieldNameFullyTranslated(identifier))
                {
                    return null;
                }
                string translated = IdentifierTranslationService.TranslateFieldName(identifier);
                if (!string.Equals(translated, candidate.Trim(), StringComparison.Ordinal))
                {
                    return null;
                }
                return ruleName == "EntityFieldAssignment"
                    ? "EntityFieldAssignment"
                    : "DynamicTableFieldUsage";
            }
            return null;
        }

        /// <summary>XMZADD 20260904 返回经仓储动态建表和库存写入代码确认的字段业务名称。</summary>
        private static string GetKnownFieldName(string objectName, string fieldName)
        {
            if (!string.Equals(objectName, "Account_Storage_Part_Definition", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(fieldName, "ASPD_ID", StringComparison.OrdinalIgnoreCase)) return "仓储区分表配置ID";
            if (string.Equals(fieldName, "Bu_ID", StringComparison.OrdinalIgnoreCase)) return "事业部ID";
            if (string.Equals(fieldName, "Table_Storage_Account", StringComparison.OrdinalIgnoreCase)) return "仓储区出入库流水表名";
            if (string.Equals(fieldName, "Table_Storage_Event", StringComparison.OrdinalIgnoreCase)) return "仓储区事件表名";
            if (string.Equals(fieldName, "Table_Storage_Event_Item", StringComparison.OrdinalIgnoreCase)) return "仓储区事件明细表名";
            if (string.Equals(fieldName, "Table_Storage_Inv", StringComparison.OrdinalIgnoreCase)) return "仓储区库存表名";
            return null;
        }

        /// <summary>XMZADD 20260904 将 ASPD、POI 等完整实体首字母缩写恢复为所属业务对象中文名称。</summary>
        private static string InferAcronymFieldName(TableMetadata table, string fieldName)
        {
            int separator = fieldName.IndexOf('_');
            if (separator <= 0 || separator >= fieldName.Length - 1)
            {
                return null;
            }

            string prefix = fieldName.Substring(0, separator);
            string initials = CreateInitials(table.ObjectName);
            if (!string.Equals(prefix, initials, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string ownerName = table.ChineseName == null ? string.Empty : table.ChineseName.Value ?? string.Empty;
            ownerName = RemoveTrailingTable(ownerName);
            if (!IdentifierTranslationService.IsReliableChineseName(ownerName))
            {
                return null;
            }

            string suffixIdentifier = fieldName.Substring(separator + 1);
            if (!IdentifierTranslationService.IsFieldNameFullyTranslated(suffixIdentifier))
            {
                // 任一未知后缀都可能承载关键业务限定，不能丢弃后再把局部译名提升为高置信源码名称。
                return null;
            }

            string suffix = IdentifierTranslationService.TranslateFieldName(suffixIdentifier);
            return ownerName + suffix;
        }

        /// <summary>XMZADD 20260904 按物理表名各业务段首字母生成实体缩写，数字和空段不参与匹配。</summary>
        private static string CreateInitials(string objectName)
        {
            var result = new StringBuilder();
            string[] sections = (objectName ?? string.Empty).Split('_');
            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                MatchCollection parts = IdentifierPartRegex.Matches(sections[sectionIndex]);
                for (int partIndex = 0; partIndex < parts.Count; partIndex++)
                {
                    string part = parts[partIndex].Value;
                    if (part.Length > 0 && !char.IsDigit(part[0]))
                    {
                        result.Append(char.ToUpperInvariant(part[0]));
                    }
                }
            }
            return result.ToString();
        }

        /// <summary>XMZADD 20260905 写入聚合字段候选并保存旧值、全部参与证据和安全覆盖判定依据。</summary>
        private static bool ApplyFieldValue(FieldMetadata field, string value, int confidenceScore,
            IList<SourceEvidence> evidence, SourceEvidence fallbackEvidence, string explanation,
            string incomingRuleName = null, int evidenceKindCount = -1)
        {
            IList<SourceEvidence> participatingEvidence = SelectParticipatingEvidence(evidence, fallbackEvidence);
            int kinds = evidenceKindCount < 0
                ? CountSupportingEvidenceKinds(participatingEvidence, field.FieldName, value)
                : evidenceKindCount;
            string ruleName = string.IsNullOrWhiteSpace(incomingRuleName)
                ? GetHighestPrioritySupportingRule(participatingEvidence, field.FieldName, value)
                : incomingRuleName;
            if (!MetadataEvidencePolicy.CanReplaceBusinessName(
                    field.ChineseName, value, confidenceScore, kinds, ruleName))
            {
                return false;
            }

            field.ChineseName = CreateCodeValue(
                value, confidenceScore, participatingEvidence, explanation, field.ChineseName, true);
            return true;
        }

        /// <summary>XMZADD 20260905 按聚合证据类型写入字段用途，帮助新人区分界面展示、关联查询和实体读写。</summary>
        private static void ApplyFieldUsage(FieldMetadata field, IList<SourceEvidence> evidence, int confidenceScore)
        {
            string usage = CreateUsageSummary(evidence);
            if (string.IsNullOrWhiteSpace(usage) ||
                !MetadataEvidencePolicy.CanReplace(field.Usage, ConfidenceStatus.CodeEvidence, BusinessUsageRule))
            {
                return;
            }
            field.Usage = CreateCodeValue(usage, confidenceScore,
                SelectParticipatingEvidence(evidence, null),
                "用途由字段在 EOS 业务代码中的直接使用方式聚合得到。", field.Usage, true);
        }

        /// <summary>XMZADD 20260904 统一把上下文候选写入表级元数据，避免各规则产生不同覆盖行为。</summary>
        private static void ApplyValue(Func<MetadataValue> readExisting, Action<MetadataValue> writeValue,
            string value, SourceEvidence evidence, string explanation)
        {
            if (MetadataEvidencePolicy.CanReplace(readExisting(), ConfidenceStatus.CodeEvidence, BusinessUsageRule))
            {
                writeValue(CreateCodeValue(value, evidence, explanation));
            }
        }

        /// <summary>XMZADD 20260904 创建可回溯到 EOS 文件和行号的业务代码名称证据。</summary>
        private static MetadataValue CreateCodeValue(string value, SourceEvidence evidence, string explanation)
        {
            var evidenceList = new List<SourceEvidence>();
            if (evidence != null)
            {
                evidenceList.Add(evidence);
            }
            return CreateCodeValue(value, 90, evidenceList, explanation, null, false);
        }

        /// <summary>XMZADD 20260905 创建包含旧自动值、全部源码位置及原始用途规则的可审计代码名称。</summary>
        private static MetadataValue CreateCodeValue(string value, int confidenceScore,
            IList<SourceEvidence> evidence, string explanation, MetadataValue existing,
            bool preserveSourceRuleNames)
        {
            var items = new List<EvidenceItem>();
            var summary = new StringBuilder(explanation ?? string.Empty);
            var summaryLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (evidence != null)
            {
                for (int index = 0; index < evidence.Count; index++)
                {
                    SourceEvidence sourceEvidence = evidence[index];
                    EvidenceItem source = sourceEvidence == null ? null : sourceEvidence.Evidence;
                    if (source == null || string.IsNullOrWhiteSpace(source.SourcePath) || source.SourceLine <= 0)
                    {
                        continue;
                    }

                    string originalRule = source.RuleName ?? string.Empty;
                    items.Add(new EvidenceItem
                    {
                        SourceType = string.IsNullOrWhiteSpace(source.SourceType) ? "EOS源码" : source.SourceType,
                        SourcePath = source.SourcePath,
                        SourceLine = source.SourceLine,
                        RuleName = preserveSourceRuleNames ? originalRule : BusinessUsageRule,
                        RawValue = source.RawValue,
                        OriginalText = source.OriginalText,
                        Explanation = (explanation ?? string.Empty) +
                                      (preserveSourceRuleNames && !string.IsNullOrWhiteSpace(originalRule)
                                          ? " 原始证据规则：" + originalRule + "。"
                                          : string.Empty)
                    });

                    string location = source.SourcePath + ":" + source.SourceLine.ToString();
                    if (summaryLocations.Add(location))
                    {
                        summary.Append(summaryLocations.Count == 1 ? " 证据：" : "；");
                        summary.Append(location);
                        if (!string.IsNullOrWhiteSpace(originalRule))
                        {
                            summary.Append("(");
                            summary.Append(originalRule);
                            summary.Append(")");
                        }
                    }
                }
            }

            string originalAutomaticValue = null;
            if (existing != null)
            {
                originalAutomaticValue = existing.OriginalAutomaticValue;
                if (string.IsNullOrWhiteSpace(originalAutomaticValue) &&
                    !string.Equals(existing.Value == null ? null : existing.Value.Trim(),
                        value == null ? null : value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // 只有展示名称确实变化时才记录旧自动值，单纯证据晋升不伪造名称变更。
                    originalAutomaticValue = existing.Value;
                }
            }

            return new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.CodeEvidence,
                ConfidenceScore = confidenceScore,
                SourceType = "EOS业务代码",
                SourceSummary = summary.ToString(),
                OriginalAutomaticValue = originalAutomaticValue,
                Evidence = items
            };
        }

        /// <summary>XMZADD 20260905 选择全部字段证据；无字段证据时使用实体证据保证代码结论具备有效来源。</summary>
        private static IList<SourceEvidence> SelectParticipatingEvidence(
            IList<SourceEvidence> evidence, SourceEvidence fallbackEvidence)
        {
            if (evidence != null && evidence.Count > 0)
            {
                return evidence;
            }
            var result = new List<SourceEvidence>();
            if (fallbackEvidence != null)
            {
                result.Add(fallbackEvidence);
            }
            return result;
        }

        /// <summary>XMZADD 20260905 将字段证据规则转换为不重复的中文用途类别。</summary>
        private static string CreateUsageSummary(IList<SourceEvidence> evidence)
        {
            if (evidence == null || evidence.Count == 0)
            {
                return null;
            }
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < evidence.Count; index++)
            {
                string ruleName = evidence[index] == null || evidence[index].Evidence == null
                    ? string.Empty
                    : evidence[index].Evidence.RuleName ?? string.Empty;
                string value = GetUsageName(ruleName);
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                {
                    values.Add(value);
                }
            }
            return values.Count == 0 ? null : string.Join("、", values.ToArray());
        }

        /// <summary>XMZADD 20260905 把源码字段证据规则映射为面向开发人员的业务用途名称。</summary>
        private static string GetUsageName(string ruleName)
        {
            if (IsCaptionRule(ruleName)) return "界面展示";
            if (ruleName == "SqlFieldRelation") return "关联查询";
            if (ruleName == "EntityFieldAssignment") return "实体读写";
            if (ruleName == "DynamicTableFieldUsage") return "动态表配置";
            if (ruleName == "EntityProperty") return "实体字段定义";
            return null;
        }

        /// <summary>XMZADD 20260904 去除所属对象名称末尾的技术“表”字，便于组合字段后缀。</summary>
        private static string RemoveTrailingTable(string value)
        {
            string result = (value ?? string.Empty).Trim();
            return result.EndsWith("表", StringComparison.Ordinal)
                ? result.Substring(0, result.Length - 1)
                : result;
        }

        /// <summary>XMZADD 20260904 生成大小写不敏感的表字段联合键，防止不同对象同名字段串用证据。</summary>
        private static string MakeFieldKey(string objectName, string fieldName)
        {
            return (objectName ?? string.Empty) + "" + (fieldName ?? string.Empty);
        }
    }
}
