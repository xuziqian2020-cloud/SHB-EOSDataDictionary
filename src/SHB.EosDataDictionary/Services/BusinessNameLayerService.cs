using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260915 将名称候选按证据强度分入正式名称、参考译名或冲突候选。</summary>
    public sealed class BusinessNameLayerService
    {
        private readonly NameSuggestionFingerprintService fingerprintService =
            new NameSuggestionFingerprintService();

        /// <summary>XMZADD 20260915 将一个表名候选应用到正式名称与参考译名双层模型。</summary>
        public void ApplyTableCandidate(TableMetadata table, MetadataValue candidate)
        {
            var candidates = new List<MetadataValue>();
            if (candidate != null)
            {
                candidates.Add(candidate);
            }
            ApplyTableCandidates(table, candidates);
        }

        /// <summary>XMZADD 20260915 合并同一表的全部名称候选并显式保留冲突。</summary>
        public void ApplyTableCandidates(TableMetadata table, IList<MetadataValue> candidates)
        {
            if (table == null)
            {
                return;
            }
            IList<MetadataValue> allCandidates = CollectCandidates(
                table.SuggestedChineseName, table.AlternativeChineseNames, candidates);
            NameLayerResult result = ApplyCandidates(
                table.ChineseName, table.RejectedSuggestionFingerprints, allCandidates);
            table.ChineseName = result.OfficialName;
            table.SuggestedChineseName = result.SuggestedName;
            table.AlternativeChineseNames = result.AlternativeNames;
            table.RejectedSuggestionFingerprints = CopyStrings(table.RejectedSuggestionFingerprints);
        }

        /// <summary>XMZADD 20260915 将一个字段名候选应用到正式名称与参考译名双层模型。</summary>
        public void ApplyFieldCandidate(FieldMetadata field, MetadataValue candidate)
        {
            var candidates = new List<MetadataValue>();
            if (candidate != null)
            {
                candidates.Add(candidate);
            }
            ApplyFieldCandidates(field, candidates);
        }

        /// <summary>XMZADD 20260915 合并同一字段的全部名称候选并显式保留冲突。</summary>
        public void ApplyFieldCandidates(FieldMetadata field, IList<MetadataValue> candidates)
        {
            if (field == null)
            {
                return;
            }
            IList<MetadataValue> allCandidates = CollectCandidates(
                field.SuggestedChineseName, field.AlternativeChineseNames, candidates);
            NameLayerResult result = ApplyCandidates(
                field.ChineseName, field.RejectedSuggestionFingerprints, allCandidates);
            field.ChineseName = result.OfficialName;
            field.SuggestedChineseName = result.SuggestedName;
            field.AlternativeChineseNames = result.AlternativeNames;
            field.RejectedSuggestionFingerprints = CopyStrings(field.RejectedSuggestionFingerprints);
        }

        /// <summary>XMZADD 20260915 清理旧快照中占用正式名称列的弱推测并迁移到参考译名。</summary>
        public void NormalizeSnapshot(SnapshotData snapshot)
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
                ApplyTableCandidates(table, new List<MetadataValue>());
                if (table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    if (table.Fields[fieldIndex] != null)
                    {
                        ApplyFieldCandidates(table.Fields[fieldIndex], new List<MetadataValue>());
                    }
                }
            }
        }

        /// <summary>XMZADD 20260915 判断候选是否达到新人可直接依赖的正式中文名称标准。</summary>
        public bool CanPromoteToOfficial(MetadataValue candidate,
            IList<MetadataValue> agreeingEvidence, bool hasConflict)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Value) || hasConflict)
            {
                return false;
            }
            if (MetadataEvidencePolicy.IsAuthoritativeBusinessName(candidate))
            {
                return true;
            }
            if (!IdentifierTranslationService.IsReliableChineseName(candidate.Value))
            {
                return false;
            }
            if (candidate.Status == ConfidenceStatus.KnowledgeBaseEvidence)
            {
                return MetadataEvidencePolicy.HasExactKnowledgeEvidence(candidate);
            }
            if (candidate.Status != ConfidenceStatus.CodeEvidence &&
                candidate.Status != ConfidenceStatus.GuessedConflict)
            {
                return false;
            }
            if (HasDirectBusinessEvidence(candidate))
            {
                return true;
            }
            return CountIndependentSources(candidate, agreeingEvidence) >= 2;
        }

        /// <summary>XMZADD 20260915 对候选分组、去重、冲突检测并选择正式名称和参考译名。</summary>
        private NameLayerResult ApplyCandidates(MetadataValue existingOfficial,
            IList<string> rejectedFingerprints, IList<MetadataValue> incomingCandidates)
        {
            var candidates = new List<MetadataValue>();
            bool existingIsOfficial = CanPromoteToOfficial(
                existingOfficial, new List<MetadataValue>(), false);
            bool preserveExisting = existingIsOfficial &&
                !HasHigherGradeCandidate(existingOfficial, incomingCandidates);
            if (!preserveExisting && existingOfficial != null &&
                !string.IsNullOrWhiteSpace(existingOfficial.Value))
            {
                candidates.Add(existingOfficial);
            }
            if (incomingCandidates != null)
            {
                for (int index = 0; index < incomingCandidates.Count; index++)
                {
                    if (incomingCandidates[index] != null &&
                        !string.IsNullOrWhiteSpace(incomingCandidates[index].Value))
                    {
                        candidates.Add(incomingCandidates[index]);
                    }
                }
            }

            List<CandidateGroup> groups = BuildCandidateGroups(candidates, rejectedFingerprints);
            if (preserveExisting)
            {
                RemoveSameValueGroup(groups, existingOfficial.Value);
            }

            int promotableCount = 0;
            int highestAdmissionRank = 0;
            int highestRankCount = 0;
            CandidateGroup highestRankCandidate = null;
            for (int index = 0; index < groups.Count; index++)
            {
                CandidateGroup group = groups[index];
                group.CanPromote = CanPromoteToOfficial(group.MergedValue, group.SourceValues, false);
                if (group.CanPromote)
                {
                    promotableCount++;
                    group.AdmissionRank = GetAdmissionRank(group.MergedValue);
                    if (group.AdmissionRank > highestAdmissionRank)
                    {
                        highestAdmissionRank = group.AdmissionRank;
                        highestRankCount = 1;
                        highestRankCandidate = group;
                    }
                    else if (group.AdmissionRank == highestAdmissionRank)
                    {
                        highestRankCount++;
                    }
                }
            }

            // 只有同等级强证据互相冲突时才不选胜者；跨等级冲突保留最高等级正式名。
            bool candidateConflict = highestRankCount > 1;
            CandidateGroup selectedOfficial = !preserveExisting && !candidateConflict
                ? highestRankCandidate
                : null;
            if (promotableCount > 1 || (preserveExisting && promotableCount > 0))
            {
                for (int index = 0; index < groups.Count; index++)
                {
                    if (groups[index].CanPromote && groups[index] != selectedOfficial)
                    {
                        MarkConflict(groups[index].MergedValue);
                    }
                }
            }

            MetadataValue official = preserveExisting
                ? existingOfficial
                : CreateEmptyOfficialName();
            if (selectedOfficial != null)
            {
                official = selectedOfficial.MergedValue;
                groups.Remove(selectedOfficial);
            }

            SortCandidateGroups(groups);
            MetadataValue suggested = groups.Count == 0 ? null : groups[0].MergedValue;
            PrepareReferenceName(suggested);
            var alternatives = new List<MetadataValue>();
            for (int index = 1; index < groups.Count; index++)
            {
                MetadataValue alternative = groups[index].MergedValue;
                PrepareReferenceName(alternative);
                alternatives.Add(alternative);
            }
            return new NameLayerResult(official, suggested, alternatives);
        }

        /// <summary>XMZADD 20260915 清除参考译名中的人工覆盖前自动值，避免复用正式名称专用语义。</summary>
        private static void PrepareReferenceName(MetadataValue value)
        {
            if (value != null)
            {
                value.OriginalAutomaticValue = null;
            }
        }

        /// <summary>XMZADD 20260915 判断本轮是否出现高于既有自动正式名的单项权威证据。</summary>
        private bool HasHigherGradeCandidate(MetadataValue existingOfficial,
            IList<MetadataValue> incomingCandidates)
        {
            if (existingOfficial == null || incomingCandidates == null)
            {
                return false;
            }
            int existingRank = GetAdmissionRank(existingOfficial);
            for (int index = 0; index < incomingCandidates.Count; index++)
            {
                MetadataValue candidate = incomingCandidates[index];
                if (CanPromoteToOfficial(candidate, new List<MetadataValue>(), false) &&
                    GetAdmissionRank(candidate) > existingRank)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260915 按人工、权威直接证据和业务代码证据确定冲突裁决等级。</summary>
        private static int GetAdmissionRank(MetadataValue value)
        {
            if (value == null)
            {
                return 0;
            }
            if (value.IsManualOverride || value.IsLocked ||
                value.Status == ConfidenceStatus.LocalOverride ||
                value.Status == ConfidenceStatus.Confirmed)
            {
                return 300;
            }
            if (MetadataEvidencePolicy.IsAuthoritativeBusinessName(value))
            {
                return 200;
            }
            return 100;
        }

        /// <summary>XMZADD 20260915 按规范化中文值聚合同义候选并合并不重复的来源证据。</summary>
        private List<CandidateGroup> BuildCandidateGroups(IList<MetadataValue> candidates,
            IList<string> rejectedFingerprints)
        {
            var groupsByValue = new Dictionary<string, CandidateGroup>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < candidates.Count; index++)
            {
                MetadataValue candidate = candidates[index];
                NameCandidate normalized = IdentifierTranslationService.NormalizeNameCandidate(candidate.Value);
                if (!IsUsableSuggestion(normalized.Value))
                {
                    continue;
                }
                MetadataValue normalizedCandidate = CloneValue(candidate);
                normalizedCandidate.Value = normalized.Value;
                if (fingerprintService.IsRejected(rejectedFingerprints, normalizedCandidate))
                {
                    continue;
                }

                CandidateGroup group;
                if (!groupsByValue.TryGetValue(normalized.Value, out group))
                {
                    group = new CandidateGroup(normalized.Value);
                    groupsByValue.Add(normalized.Value, group);
                }
                group.Add(normalizedCandidate);
            }
            var groups = new List<CandidateGroup>();
            foreach (KeyValuePair<string, CandidateGroup> pair in groupsByValue)
            {
                pair.Value.Finish();
                groups.Add(pair.Value);
            }
            return groups;
        }

        /// <summary>XMZADD 20260915 排除仅表示待确认状态的占位文本，避免占用参考译名和其他候选。</summary>
        private static bool IsUsableSuggestion(string value)
        {
            string candidate = (value ?? string.Empty).Trim();
            return candidate.Length > 0 &&
                   !string.Equals(candidate, "待确认", StringComparison.Ordinal) &&
                   !string.Equals(candidate, "暂无可靠中文名称", StringComparison.Ordinal) &&
                   !string.Equals(candidate, "暂无中文名称", StringComparison.Ordinal) &&
                   !candidate.StartsWith("推测：", StringComparison.Ordinal) &&
                   !candidate.StartsWith("推测:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260915 从参考候选中移除与既有正式名称相同的重复项。</summary>
        private static void RemoveSameValueGroup(IList<CandidateGroup> groups, string officialValue)
        {
            for (int index = groups.Count - 1; index >= 0; index--)
            {
                if (string.Equals(groups[index].Value, officialValue,
                        StringComparison.OrdinalIgnoreCase))
                {
                    groups.RemoveAt(index);
                }
            }
        }

        /// <summary>XMZADD 20260915 标记强证据候选冲突且保留原始证据供人工审阅。</summary>
        private static void MarkConflict(MetadataValue value)
        {
            if (value == null)
            {
                return;
            }
            // 候选均为深复制值，改为冲突状态不会修改现有正式名，但能让界面准确提示人工裁决。
            value.Status = ConfidenceStatus.GuessedConflict;
            value.Description = "存在不同的强业务证据候选，需人工确认。";
        }

        /// <summary>XMZADD 20260915 识别唯一绑定字段或对象的直接业务代码命名证据。</summary>
        private static bool HasDirectBusinessEvidence(MetadataValue value)
        {
            if (value == null || value.Evidence == null)
            {
                return false;
            }
            for (int index = 0; index < value.Evidence.Count; index++)
            {
                string ruleName = value.Evidence[index] == null
                    ? string.Empty
                    : value.Evidence[index].RuleName ?? string.Empty;
                if (ruleName == "GridColumnCaption" || ruleName == "DataColumnCaption" ||
                    ruleName == "SqlColumnAlias" || ruleName == "SqlFieldRelation" ||
                    ruleName == "EntityFieldAssignment" ||
                    (ruleName == "BusinessUsageContext" && value.ConfidenceScore >= 90))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260915 计算去除同位置和复制 SQL 后的独立业务证据来源数。</summary>
        private static int CountIndependentSources(MetadataValue candidate,
            IList<MetadataValue> agreeingEvidence)
        {
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sqlContents = new HashSet<string>(StringComparer.Ordinal);
            AddIndependentSources(candidate, locations, sqlContents);
            if (agreeingEvidence != null)
            {
                for (int index = 0; index < agreeingEvidence.Count; index++)
                {
                    AddIndependentSources(agreeingEvidence[index], locations, sqlContents);
                }
            }
            return locations.Count;
        }

        /// <summary>XMZADD 20260915 将一个候选的相对源码位置加入独立证据集合并折叠复制 SQL。</summary>
        private static void AddIndependentSources(MetadataValue candidate,
            ISet<string> locations, ISet<string> sqlContents)
        {
            if (candidate == null || candidate.Evidence == null)
            {
                return;
            }
            for (int index = 0; index < candidate.Evidence.Count; index++)
            {
                EvidenceItem evidence = candidate.Evidence[index];
                if (evidence == null || string.IsNullOrWhiteSpace(evidence.SourcePath))
                {
                    continue;
                }
                string ruleName = evidence.RuleName ?? string.Empty;
                if (ruleName.StartsWith("Sql", StringComparison.Ordinal))
                {
                    string sqlText = !string.IsNullOrWhiteSpace(evidence.OriginalText)
                        ? evidence.OriginalText.Trim()
                        : (evidence.RawValue ?? string.Empty).Trim();
                    string sqlKey = ruleName + "|" + sqlText;
                    if (sqlText.Length > 0 && !sqlContents.Add(sqlKey))
                    {
                        continue;
                    }
                }
                string path = evidence.SourcePath.Replace('\\', '/');
                locations.Add(path + "|" + evidence.SourceLine.ToString());
            }
        }

        /// <summary>XMZADD 20260915 按可升级级别、评分、证据数量和中文值稳定排序参考候选。</summary>
        private static void SortCandidateGroups(List<CandidateGroup> groups)
        {
            groups.Sort(CompareCandidateGroups);
        }

        /// <summary>XMZADD 20260915 比较两个候选组并保证相同输入产生确定顺序。</summary>
        private static int CompareCandidateGroups(CandidateGroup left, CandidateGroup right)
        {
            if (left.CanPromote != right.CanPromote)
            {
                return left.CanPromote ? -1 : 1;
            }
            int score = right.MergedValue.ConfidenceScore.CompareTo(left.MergedValue.ConfidenceScore);
            if (score != 0)
            {
                return score;
            }
            int evidence = right.MergedValue.Evidence.Count.CompareTo(left.MergedValue.Evidence.Count);
            if (evidence != 0)
            {
                return evidence;
            }
            return string.Compare(left.Value, right.Value, StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260915 创建不占用正式业务名称的空元数据值。</summary>
        private static MetadataValue CreateEmptyOfficialName()
        {
            return new MetadataValue
            {
                Value = string.Empty,
                Status = ConfidenceStatus.PendingConfirmation,
                SourceSummary = "未达到正式名称证据门槛",
                Evidence = new List<EvidenceItem>()
            };
        }

        /// <summary>XMZADD 20260915 深复制名称值和证据列表以避免分层时修改输入或只读集合。</summary>
        private static MetadataValue CloneValue(MetadataValue source)
        {
            var result = new MetadataValue
            {
                Value = source == null ? string.Empty : source.Value,
                Description = source == null ? null : source.Description,
                Status = source == null ? ConfidenceStatus.PendingConfirmation : source.Status,
                ConfidenceScore = source == null ? 0 : source.ConfidenceScore,
                SourceType = source == null ? null : source.SourceType,
                SourceSummary = source == null ? null : source.SourceSummary,
                OriginalAutomaticValue = source == null ? null : source.OriginalAutomaticValue,
                IsManualOverride = source != null && source.IsManualOverride,
                IsLocked = source != null && source.IsLocked,
                Evidence = new List<EvidenceItem>()
            };
            if (source != null && source.Evidence != null)
            {
                for (int index = 0; index < source.Evidence.Count; index++)
                {
                    if (source.Evidence[index] != null)
                    {
                        result.Evidence.Add(CloneEvidence(source.Evidence[index]));
                    }
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260915 复制单条公开证据，避免后续路径脱敏修改扫描器或其他候选持有的实例。</summary>
        private static EvidenceItem CloneEvidence(EvidenceItem source)
        {
            return new EvidenceItem
            {
                SourceType = source.SourceType,
                SourcePath = source.SourcePath,
                SourceLine = source.SourceLine,
                RuleName = source.RuleName,
                RawValue = source.RawValue,
                OriginalText = source.OriginalText,
                Explanation = source.Explanation,
                ExtensionData = source.ExtensionData
            };
        }

        /// <summary>XMZADD 20260915 复制拒绝指纹集合以兼容旧快照中的只读数组。</summary>
        private static IList<string> CopyStrings(IList<string> values)
        {
            var result = new List<string>();
            if (values != null)
            {
                for (int index = 0; index < values.Count; index++)
                {
                    if (!string.IsNullOrWhiteSpace(values[index]))
                    {
                        result.Add(values[index]);
                    }
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260915 合并既有参考候选和本次输入以支持分批富化而不丢失审阅依据。</summary>
        private static IList<MetadataValue> CollectCandidates(MetadataValue suggested,
            IList<MetadataValue> alternatives, IList<MetadataValue> incoming)
        {
            var result = new List<MetadataValue>();
            if (suggested != null && !string.IsNullOrWhiteSpace(suggested.Value))
            {
                result.Add(suggested);
            }
            if (alternatives != null)
            {
                for (int index = 0; index < alternatives.Count; index++)
                {
                    if (alternatives[index] != null &&
                        !string.IsNullOrWhiteSpace(alternatives[index].Value))
                    {
                        result.Add(alternatives[index]);
                    }
                }
            }
            if (incoming != null)
            {
                for (int index = 0; index < incoming.Count; index++)
                {
                    if (incoming[index] != null &&
                        !string.IsNullOrWhiteSpace(incoming[index].Value))
                    {
                        result.Add(incoming[index]);
                    }
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260915 保存一次名称分层计算得到的正式名、参考名和其他候选。</summary>
        private sealed class NameLayerResult
        {
            /// <summary>XMZADD 20260915 创建完整名称分层结果。</summary>
            public NameLayerResult(MetadataValue officialName, MetadataValue suggestedName,
                IList<MetadataValue> alternativeNames)
            {
                OfficialName = officialName;
                SuggestedName = suggestedName;
                AlternativeNames = alternativeNames;
            }

            public MetadataValue OfficialName { get; private set; }
            public MetadataValue SuggestedName { get; private set; }
            public IList<MetadataValue> AlternativeNames { get; private set; }
        }

        /// <summary>XMZADD 20260915 聚合同一中文值的候选状态、评分和独立证据。</summary>
        private sealed class CandidateGroup
        {
            private readonly List<MetadataValue> sourceValues = new List<MetadataValue>();

            /// <summary>XMZADD 20260915 创建待合并的同义候选组。</summary>
            public CandidateGroup(string value)
            {
                Value = value;
            }

            public string Value { get; private set; }
            public MetadataValue MergedValue { get; private set; }
            public IList<MetadataValue> SourceValues { get { return sourceValues; } }
            public bool CanPromote { get; set; }
            public int AdmissionRank { get; set; }

            /// <summary>XMZADD 20260915 将一个同义候选加入当前聚合组。</summary>
            public void Add(MetadataValue value)
            {
                sourceValues.Add(value);
            }

            /// <summary>XMZADD 20260915 选择最高状态和评分并合并不重复证据。</summary>
            public void Finish()
            {
                MetadataValue selected = sourceValues[0];
                for (int index = 1; index < sourceValues.Count; index++)
                {
                    if (GetStatusPriority(sourceValues[index]) > GetStatusPriority(selected) ||
                        (GetStatusPriority(sourceValues[index]) == GetStatusPriority(selected) &&
                         sourceValues[index].ConfidenceScore > selected.ConfidenceScore))
                    {
                        selected = sourceValues[index];
                    }
                }
                MergedValue = CloneValue(selected);
                MergedValue.Value = Value;
                for (int index = 0; index < sourceValues.Count; index++)
                {
                    MergeEvidence(MergedValue.Evidence, sourceValues[index].Evidence);
                    if (sourceValues[index].ConfidenceScore > MergedValue.ConfidenceScore)
                    {
                        MergedValue.ConfidenceScore = sourceValues[index].ConfidenceScore;
                    }
                }
                RestoreConflictSourceStatus(MergedValue);
            }

            /// <summary>XMZADD 20260915 为候选状态提供只用于同义合并的稳定优先级。</summary>
            private static int GetStatusPriority(MetadataValue value)
            {
                if (value == null) return 0;
                if (value.IsManualOverride || value.IsLocked ||
                    value.Status == ConfidenceStatus.LocalOverride) return 100;
                if (value.Status == ConfidenceStatus.Confirmed ||
                    HasEvidenceRule(value, "ConfirmedBusinessSemantic")) return 90;
                if (value.Status == ConfidenceStatus.DatabaseEvidence ||
                    HasEvidenceRule(value, "DatabaseComment") ||
                    HasEvidenceRule(value, "SqlExtendedDescription")) return 80;
                if (value.Status == ConfidenceStatus.KnowledgeBaseEvidence ||
                    MetadataEvidencePolicy.HasExactKnowledgeEvidence(value)) return 70;
                if (value.Status == ConfidenceStatus.CodeEvidence ||
                    value.Status == ConfidenceStatus.GuessedConflict) return 60;
                if (value.Status == ConfidenceStatus.AiGuessed) return 20;
                return 10;
            }

            /// <summary>XMZADD 20260915 从候选证据恢复冲突前来源等级，使后续分批刷新仍按原等级裁决。</summary>
            private static void RestoreConflictSourceStatus(MetadataValue value)
            {
                if (value == null || value.Status != ConfidenceStatus.GuessedConflict)
                {
                    return;
                }
                if (value.IsManualOverride || value.IsLocked)
                {
                    value.Status = ConfidenceStatus.LocalOverride;
                }
                else if (HasEvidenceRule(value, "ConfirmedBusinessSemantic"))
                {
                    value.Status = ConfidenceStatus.Confirmed;
                }
                else if (HasEvidenceRule(value, "DatabaseComment") ||
                         HasEvidenceRule(value, "SqlExtendedDescription"))
                {
                    value.Status = ConfidenceStatus.DatabaseEvidence;
                }
                else if (MetadataEvidencePolicy.HasExactKnowledgeEvidence(value))
                {
                    value.Status = ConfidenceStatus.KnowledgeBaseEvidence;
                }
                else
                {
                    value.Status = ConfidenceStatus.CodeEvidence;
                }
            }

            /// <summary>XMZADD 20260915 判断候选是否保留指定来源规则，用于恢复冲突前的证据等级。</summary>
            private static bool HasEvidenceRule(MetadataValue value, string ruleName)
            {
                if (value == null || value.Evidence == null)
                {
                    return false;
                }
                for (int index = 0; index < value.Evidence.Count; index++)
                {
                    EvidenceItem evidence = value.Evidence[index];
                    if (evidence != null && string.Equals(evidence.RuleName, ruleName,
                            StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }

            /// <summary>XMZADD 20260915 合并来源位置相同的证据时只保留一份。</summary>
            private static void MergeEvidence(IList<EvidenceItem> target, IList<EvidenceItem> source)
            {
                if (source == null)
                {
                    return;
                }
                for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
                {
                    EvidenceItem incoming = source[sourceIndex];
                    if (incoming == null)
                    {
                        continue;
                    }
                    bool exists = false;
                    for (int targetIndex = 0; targetIndex < target.Count; targetIndex++)
                    {
                        EvidenceItem existing = target[targetIndex];
                        if (existing != null &&
                            string.Equals(existing.SourcePath, incoming.SourcePath,
                                StringComparison.OrdinalIgnoreCase) &&
                            existing.SourceLine == incoming.SourceLine &&
                            string.Equals(existing.RuleName, incoming.RuleName,
                                StringComparison.Ordinal) &&
                            string.Equals(existing.RawValue, incoming.RawValue,
                                StringComparison.Ordinal))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        target.Add(CloneEvidence(incoming));
                    }
                }
            }
        }
    }
}
