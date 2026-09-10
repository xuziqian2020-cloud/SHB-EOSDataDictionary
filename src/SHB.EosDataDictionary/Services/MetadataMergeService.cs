using System;
using System.Collections.Generic;
using System.Text;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 按证据优先级合并 EOS 数据库和项目源码解释。</summary>
    public static class MetadataMergeService
    {
        /// <summary>XMZADD 20260828 将一个已有值和一组候选值合并并保留冲突信息。</summary>
        public static MetadataValue MergeValue(MetadataCandidate first, MetadataCandidate[] rest)
        {
            int restCount = rest == null ? 0 : rest.Length;
            var all = new MetadataCandidate[restCount + 1];
            all[0] = first;
            for (int i = 0; i < restCount; i++)
            {
                all[i + 1] = rest[i];
            }
            return MergeValue(all);
        }

        /// <summary>XMZADD 20260828 合并一个自动候选和一个人工或其他候选并保留不确定性。</summary>
        public static MetadataValue MergeValue(params MetadataCandidate[] candidates)
        {
            if (candidates == null || candidates.Length == 0)
            {
                return new MetadataValue
                {
                    Value = "推测：待确认",
                    Status = ConfidenceStatus.PendingConfirmation,
                    SourceSummary = "没有可用来源",
                    Evidence = new List<EvidenceItem>()
                };
            }

            MetadataCandidate manual = null;
            int i;
            for (i = candidates.Length - 1; i >= 0; i--)
            {
                if (candidates[i] != null && candidates[i].Status == ConfidenceStatus.Confirmed)
                {
                    manual = candidates[i];
                    break;
                }
            }

            if (manual != null)
            {
                return new MetadataValue
                {
                    Value = manual.Value,
                    Status = ConfidenceStatus.Confirmed,
                    SourceSummary = manual.SourceSummary,
                    OriginalAutomaticValue = FindFirstAutomaticValue(candidates),
                    Evidence = new List<EvidenceItem>()
                };
            }

            string firstValue = null;
            string firstSource = null;
            bool conflict = false;
            StringBuilder values = new StringBuilder();
            for (i = 0; i < candidates.Length; i++)
            {
                MetadataCandidate candidate = candidates[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Value))
                {
                    continue;
                }

                if (firstValue == null)
                {
                    firstValue = candidate.Value;
                    firstSource = candidate.SourceSummary;
                    values.Append(candidate.Value);
                }
                else if (!string.Equals(firstValue, candidate.Value, StringComparison.OrdinalIgnoreCase))
                {
                    conflict = true;
                    if (values.Length > 0)
                    {
                        values.Append(" / ");
                    }
                    values.Append(candidate.Value);
                }
            }

            if (firstValue == null)
            {
                return new MetadataValue
                {
                    Value = "推测：待确认",
                    Status = ConfidenceStatus.PendingConfirmation,
                    SourceSummary = "没有可用值",
                    Evidence = new List<EvidenceItem>()
                };
            }

            return new MetadataValue
            {
                Value = conflict ? values.ToString() : firstValue,
                Status = conflict ? ConfidenceStatus.GuessedConflict : candidates[0].Status,
                SourceSummary = conflict ? "多个来源存在冲突" : firstSource,
                OriginalAutomaticValue = firstValue,
                Evidence = new List<EvidenceItem>()
            };
        }

        private static string FindFirstAutomaticValue(IList<MetadataCandidate> candidates)
        {
            int i;
            for (i = 0; i < candidates.Count; i++)
            {
                MetadataCandidate candidate = candidates[i];
                if (candidate != null && candidate.Status != ConfidenceStatus.Confirmed)
                {
                    return candidate.Value;
                }
            }

            return null;
        }
    }
}
