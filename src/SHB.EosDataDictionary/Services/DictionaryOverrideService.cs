using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260831 将本地人工维护的业务解释覆盖到自动快照，确保人工结论始终拥有最高优先级。</summary>
    public static class DictionaryOverrideService
    {
        /// <summary>XMZADD 20260831 为指定作用域的快照应用全部人工维护项，不修改任何 EOS 数据库内容。</summary>
        public static void ApplyOverrides(SnapshotData snapshot, LocalDictionaryStore store, string scopeKey)
        {
            if (snapshot == null || snapshot.Tables == null || store == null)
            {
                return;
            }

            IList<DictionaryOverride> overrides = store.LoadOverrides(scopeKey);
            for (int i = 0; i < overrides.Count; i++)
            {
                DictionaryOverride item = overrides[i];
                TableMetadata table = FindTable(snapshot, item.ObjectName);
                if (table == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(item.FieldName))
                {
                    ApplyTableOverride(table, item);
                    continue;
                }

                FieldMetadata field = FindField(table, item.FieldName);
                if (field != null)
                {
                    ApplyFieldOverride(field, item);
                }
            }
        }

        /// <summary>XMZADD 20260831 查找已不在当前结构快照中的人工维护项，以便界面保留并标识而不是静默删除用户结论。</summary>
        public static IList<DictionaryOverride> FindOrphanedOverrides(SnapshotData snapshot, LocalDictionaryStore store, string scopeKey)
        {
            var result = new List<DictionaryOverride>();
            if (store == null)
            {
                return result;
            }

            IList<DictionaryOverride> overrides = store.LoadOverrides(scopeKey);
            for (int i = 0; i < overrides.Count; i++)
            {
                DictionaryOverride item = overrides[i];
                TableMetadata table = snapshot == null || snapshot.Tables == null ? null : FindTable(snapshot, item.ObjectName);
                bool isOrphaned = table == null;
                if (!isOrphaned && string.IsNullOrEmpty(item.FieldName))
                {
                    isOrphaned = !IsTablePropertySupported(item.PropertyName);
                }
                else if (!isOrphaned)
                {
                    isOrphaned = FindField(table, item.FieldName) == null || !IsFieldPropertySupported(item.PropertyName);
                }

                item.IsOrphaned = isOrphaned;
                if (isOrphaned)
                {
                    // 原结构已删除时仍保留人工语义，避免历史业务判断随刷新不可恢复地消失。
                    item.IsOrphaned = true;
                    result.Add(item);
                }
            }

            return result;
        }

        /// <summary>XMZADD 20260831 查找快照中的业务对象，支持数据库标识符常见的大小写差异。</summary>
        private static TableMetadata FindTable(SnapshotData snapshot, string objectName)
        {
            for (int i = 0; i < snapshot.Tables.Count; i++)
            {
                TableMetadata table = snapshot.Tables[i];
                if (table != null && string.Equals(table.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return table;
                }
            }

            return null;
        }

        /// <summary>XMZADD 20260831 查找业务对象中的字段，保证人工维护可覆盖大小写不一致的字段名。</summary>
        private static FieldMetadata FindField(TableMetadata table, string fieldName)
        {
            if (table.Fields == null)
            {
                return null;
            }

            for (int i = 0; i < table.Fields.Count; i++)
            {
                FieldMetadata field = table.Fields[i];
                if (field != null && string.Equals(field.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
            }

            return null;
        }

        /// <summary>XMZADD 20260831 将表级人工解释写回指定可维护属性，物理对象名始终保持只读。</summary>
        private static void ApplyTableOverride(TableMetadata table, DictionaryOverride item)
        {
            switch (item.PropertyName)
            {
                case "ChineseName":
                    table.ChineseName = CreateManualValue(table.ChineseName, item);
                    break;
                case "ModuleName":
                    table.ModuleName = CreateManualValue(table.ModuleName, item);
                    break;
                case "EntityName":
                    table.EntityName = CreateManualValue(table.EntityName, item);
                    break;
                case "BusinessMeaning":
                    table.BusinessMeaning = CreateManualValue(table.BusinessMeaning, item);
                    break;
                case "Remark":
                    table.Remark = CreateManualValue(table.Remark, item);
                    break;
                case "Category":
                    DictionaryTableCategory category;
                    if (TryParsePublishedCategory(item.ManualValue, out category))
                    {
                        // 人工发布必须选择明确分类，未分类只用于尚未完成人工判断的自动结果。
                        table.Category = category;
                    }
                    break;
                case "KeepWhenEmpty":
                    bool keepWhenEmpty;
                    if (bool.TryParse(item.ManualValue, out keepWhenEmpty))
                    {
                        // 空表保留规则影响后续共享快照收录范围，仅接受明确布尔值。
                        table.KeepWhenEmpty = keepWhenEmpty;
                    }
                    break;
                case "RejectedSuggestionFingerprint":
                    if (table.RejectedSuggestionFingerprints == null)
                    {
                        table.RejectedSuggestionFingerprints = new List<string>();
                    }
                    ApplyRejectedSuggestionOverride(table.RejectedSuggestionFingerprints, table.SuggestedChineseName, item, delegate
                    {
                        table.SuggestedChineseName = null;
                    });
                    break;
            }
        }

        /// <summary>XMZADD 20260831 将字段级人工解释写回指定可维护属性，物理字段结构始终不被改写。</summary>
        private static void ApplyFieldOverride(FieldMetadata field, DictionaryOverride item)
        {
            switch (item.PropertyName)
            {
                case "ChineseName":
                    field.ChineseName = CreateManualValue(field.ChineseName, item);
                    break;
                case "EntityPropertyName":
                    field.EntityPropertyName = CreateManualValue(field.EntityPropertyName, item);
                    break;
                case "BusinessMeaning":
                    field.BusinessMeaning = CreateManualValue(field.BusinessMeaning, item);
                    break;
                case "Usage":
                    field.Usage = CreateManualValue(field.Usage, item);
                    break;
                case "RelationSummary":
                    field.RelationSummary = CreateManualValue(field.RelationSummary, item);
                    break;
                case "Remark":
                    field.Remark = CreateManualValue(field.Remark, item);
                    break;
                case "RejectedSuggestionFingerprint":
                    if (field.RejectedSuggestionFingerprints == null)
                    {
                        field.RejectedSuggestionFingerprints = new List<string>();
                    }
                    ApplyRejectedSuggestionOverride(field.RejectedSuggestionFingerprints, field.SuggestedChineseName, item, delegate
                    {
                        field.SuggestedChineseName = null;
                    });
                    break;
            }
        }

        /// <summary>XMZADD 20260917 恢复本地否决指纹并仅清除证据版本完全匹配的当前参考译名。</summary>
        private static void ApplyRejectedSuggestionOverride(
            IList<string> rejectedFingerprints,
            MetadataValue suggestion,
            DictionaryOverride item,
            Action clearSuggestion)
        {
            if (rejectedFingerprints == null || item == null)
            {
                return;
            }

            string[] values = (item.ManualValue ?? string.Empty).Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < values.Length; index++)
            {
                string fingerprint = values[index].Trim();
                if (IsSha256Fingerprint(fingerprint))
                {
                    AddRejectedFingerprint(rejectedFingerprints, fingerprint);
                }
            }
            if (suggestion == null)
            {
                return;
            }

            string currentFingerprint = new NameSuggestionFingerprintService().CreateFingerprint(suggestion);
            for (int index = 0; index < rejectedFingerprints.Count; index++)
            {
                if (string.Equals(rejectedFingerprints[index], currentFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    clearSuggestion();
                    return;
                }
            }
        }

        /// <summary>XMZADD 20260917 追加唯一否决指纹，使重复加载本地覆盖不会产生重复记录。</summary>
        private static void AddRejectedFingerprint(IList<string> fingerprints, string fingerprint)
        {
            for (int index = 0; index < fingerprints.Count; index++)
            {
                if (string.Equals(fingerprints[index], fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            fingerprints.Add(fingerprint);
        }

        /// <summary>XMZADD 20260917 验证本地否决键是完整 SHA-256 十六进制值，损坏覆盖不得隐藏候选名称。</summary>
        private static bool IsSha256Fingerprint(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                bool isDigit = current >= '0' && current <= '9';
                bool isLower = current >= 'a' && current <= 'f';
                bool isUpper = current >= 'A' && current <= 'F';
                if (!isDigit && !isLower && !isUpper)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 仅从明确分类名称恢复人工规则，避免数字枚举文本被误认为已审核分类。</summary>
        private static bool TryParsePublishedCategory(string value, out DictionaryTableCategory category)
        {
            string normalized = value == null ? string.Empty : value.Trim();
            if (string.Equals(normalized, "Business", StringComparison.OrdinalIgnoreCase))
            {
                category = DictionaryTableCategory.Business;
                return true;
            }
            if (string.Equals(normalized, "BaseData", StringComparison.OrdinalIgnoreCase))
            {
                category = DictionaryTableCategory.BaseData;
                return true;
            }
            if (string.Equals(normalized, "Technical", StringComparison.OrdinalIgnoreCase))
            {
                category = DictionaryTableCategory.Technical;
                return true;
            }
            if (string.Equals(normalized, "Excluded", StringComparison.OrdinalIgnoreCase))
            {
                category = DictionaryTableCategory.Excluded;
                return true;
            }

            category = DictionaryTableCategory.Unclassified;
            return false;
        }

        /// <summary>XMZADD 20260831 判断表级人工维护属性是否仍属于允许编辑的业务解释，避免废弃属性在刷新后被误认为可用。</summary>
        private static bool IsTablePropertySupported(string propertyName)
        {
            switch (propertyName)
            {
                case "ChineseName":
                case "ModuleName":
                case "EntityName":
                case "BusinessMeaning":
                case "Remark":
                case "Category":
                case "KeepWhenEmpty":
                case "RejectedSuggestionFingerprint":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>XMZADD 20260831 判断字段级人工维护属性是否仍属于允许编辑的业务解释，避免废弃属性在刷新后被误认为可用。</summary>
        private static bool IsFieldPropertySupported(string propertyName)
        {
            switch (propertyName)
            {
                case "ChineseName":
                case "EntityPropertyName":
                case "BusinessMeaning":
                case "Usage":
                case "RelationSummary":
                case "Remark":
                case "RejectedSuggestionFingerprint":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>XMZADD 20260831 生成带来源和原自动值的人工维护结果，便于用户追溯覆盖前的推测依据。</summary>
        private static MetadataValue CreateManualValue(MetadataValue automaticValue, DictionaryOverride item)
        {
            string originalAutomaticValue = string.Empty;
            if (automaticValue != null)
            {
                originalAutomaticValue = automaticValue.OriginalAutomaticValue;
                if (string.IsNullOrWhiteSpace(originalAutomaticValue))
                {
                    originalAutomaticValue = automaticValue.Value;
                }
            }

            if (string.IsNullOrWhiteSpace(originalAutomaticValue))
            {
                // 刷新后的自动值缺失时才使用人工维护时保留的旧自动结果，避免人工记录覆盖新的可追溯依据。
                originalAutomaticValue = item.OriginalAutomaticValue;
            }

            return new MetadataValue
            {
                Value = item.ManualValue ?? string.Empty,
                Description = item.Remark ?? string.Empty,
                Status = ConfidenceStatus.LocalOverride,
                ConfidenceScore = 100,
                SourceType = "本地人工维护",
                SourceSummary = "本地人工维护（最高优先级）",
                OriginalAutomaticValue = originalAutomaticValue,
                IsManualOverride = true,
                IsLocked = item.IsLocked,
                Evidence = automaticValue == null ? new List<EvidenceItem>() : automaticValue.Evidence
            };
        }
    }
}
