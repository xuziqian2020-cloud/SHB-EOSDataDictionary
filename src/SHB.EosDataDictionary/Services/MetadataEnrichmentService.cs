using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 将 EOS 源码证据合并到数据库快照并生成可见推测。</summary>
    public static class MetadataEnrichmentService
    {
        private static readonly List<SourceEvidence> EmptyEvidence = new List<SourceEvidence>();

        /// <summary>XMZADD 20260828 按表和字段精确匹配源码证据并补齐中文候选、实体和模块。</summary>
        public static void Enrich(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }

            var objectEvidence = new Dictionary<string, List<SourceEvidence>>(StringComparer.OrdinalIgnoreCase);
            var fieldEvidence = new Dictionary<string, List<SourceEvidence>>(StringComparer.OrdinalIgnoreCase);
            var enumEvidence = new Dictionary<string, List<SourceEvidence>>(StringComparer.OrdinalIgnoreCase);
            // 先建立索引，避免真实库的每个字段都重复扫描全部源码证据。
            BuildEvidenceIndex(sourceEvidence, objectEvidence, fieldEvidence, enumEvidence);

            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                IList<SourceEvidence> tableEvidence = GetEvidence(objectEvidence, MakeObjectKey(table.ObjectName));
                ApplyTableEvidence(table, tableEvidence);

                if (table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field == null)
                    {
                        continue;
                    }
                    IList<SourceEvidence> currentFieldEvidence = GetEvidence(
                        fieldEvidence,
                        MakeFieldKey(table.ObjectName, field.FieldName));
                    ApplyFieldEvidence(table, field, currentFieldEvidence, enumEvidence);
                }
            }
        }

        /// <summary>XMZADD 20260831 按对象和字段建立大小写不敏感的源码证据索引，降低大批量快照合并复杂度。</summary>
        private static void BuildEvidenceIndex(IList<SourceEvidence> sourceEvidence,
            IDictionary<string, List<SourceEvidence>> objectEvidence,
            IDictionary<string, List<SourceEvidence>> fieldEvidence,
            IDictionary<string, List<SourceEvidence>> enumEvidence)
        {
            if (sourceEvidence == null)
            {
                return;
            }

            for (int i = 0; i < sourceEvidence.Count; i++)
            {
                SourceEvidence item = sourceEvidence[i];
                if (item == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.FieldName) && IsTableEvidenceRule(item))
                {
                    AddEvidence(objectEvidence, MakeObjectKey(item.ObjectName), item);
                }
                else
                {
                    AddEvidence(fieldEvidence, MakeFieldKey(item.ObjectName, item.FieldName), item);
                }
                if (!string.IsNullOrWhiteSpace(item.EnumName) && item.Evidence != null && item.Evidence.RuleName == "EnumMember")
                {
                    AddEvidence(enumEvidence, item.EnumName, item);
                }
            }
        }

        /// <summary>XMZADD 20260831 仅允许明确表级源码规则参与表名和模块投票，防止枚举注释污染表中文名称。</summary>
        private static bool IsTableEvidenceRule(SourceEvidence evidence)
        {
            if (evidence.Evidence == null)
            {
                return false;
            }
            string ruleName = evidence.Evidence.RuleName;
            return string.Equals(ruleName, "KisEntityClass", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "TableNameProperty", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "SqlTableUsage", StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260831 将一条源码证据追加到对应索引键，保持原始扫描顺序供可信度合并使用。</summary>
        private static void AddEvidence(IDictionary<string, List<SourceEvidence>> index,
            string key, SourceEvidence evidence)
        {
            List<SourceEvidence> items;
            if (!index.TryGetValue(key, out items))
            {
                items = new List<SourceEvidence>();
                index.Add(key, items);
            }
            items.Add(evidence);
        }

        /// <summary>XMZADD 20260831 从证据索引读取对象或字段证据，避免无匹配项反复分配空集合。</summary>
        private static IList<SourceEvidence> GetEvidence(
            IDictionary<string, List<SourceEvidence>> index, string key)
        {
            List<SourceEvidence> evidence;
            return index.TryGetValue(key, out evidence) ? evidence : EmptyEvidence;
        }

        /// <summary>XMZADD 20260831 生成源码对象证据的统一匹配键，保证空对象名也能安全处理。</summary>
        private static string MakeObjectKey(string objectName)
        {
            return objectName ?? string.Empty;
        }

        /// <summary>XMZADD 20260831 生成源码字段证据的复合匹配键，区分同名字段所属对象。</summary>
        private static string MakeFieldKey(string objectName, string fieldName)
        {
            return MakeObjectKey(objectName) + "\u001F" + (fieldName ?? string.Empty);
        }

        /// <summary>XMZADD 20260903 仅接受 EOS 表类和既有金蝶实体前缀，防止普通窗体控件污染实体映射。</summary>
        private static bool IsBusinessEntityName(string entityName)
        {
            return !string.IsNullOrWhiteSpace(entityName) &&
                   (entityName.StartsWith("t_", StringComparison.OrdinalIgnoreCase) ||
                    entityName.StartsWith("Kis_", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>XMZADD 20260831 合并表级源码证据并生成中文名称、实体和模块的可信元数据。</summary>
        private static void ApplyTableEvidence(TableMetadata table, IList<SourceEvidence> evidence)
        {
            NormalizeExistingAutomaticName(table.ChineseName);
            var nameCandidates = new List<MetadataCandidate>();
            var moduleVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < evidence.Count; i++)
            {
                SourceEvidence item = evidence[i];
                NameCandidate normalized = IdentifierTranslationService.NormalizeNameCandidate(item.ChineseNameCandidate);
                if (IdentifierTranslationService.IsReliableChineseName(normalized.Value))
                {
                    nameCandidates.Add(new MetadataCandidate(normalized.Value, ConfidenceStatus.CodeEvidence,
                        "EOS 源码注释", CreatePublishedEvidence(item.Evidence, normalized.RawEvidence)));
                }

                if (IsBusinessEntityName(item.EntityName) && item.Evidence != null &&
                    (item.Evidence.RuleName == "KisEntityClass" || item.Evidence.RuleName == "TableNameProperty") &&
                    (table.EntityName == null || (!table.EntityName.IsManualOverride && !table.EntityName.IsLocked &&
                     table.EntityName.Status != ConfidenceStatus.LocalOverride && table.EntityName.Status != ConfidenceStatus.Confirmed)))
                {
                    table.EntityName = new MetadataValue
                    {
                        Value = item.EntityName,
                        Status = ConfidenceStatus.CodeEvidence,
                        SourceSummary = "EOS 源码实体类",
                        Evidence = item.Evidence == null
                            ? new List<EvidenceItem>()
                            : new List<EvidenceItem> { CreatePublishedEvidence(item.Evidence, item.EntityName) }
                    };
                }

                if (!string.IsNullOrWhiteSpace(item.ModulePath))
                {
                    AddModuleVote(moduleVotes, TranslateModule(item.ModulePath));
                }
            }

            if (MetadataEvidencePolicy.CanReplace(table.ModuleName, ConfidenceStatus.Guessed, "SourceModuleUsage"))
            {
                string selectedModule = SelectModule(moduleVotes);
                table.ModuleName = new MetadataValue
                {
                    Value = selectedModule,
                    Status = ConfidenceStatus.Guessed,
                    SourceSummary = selectedModule == "其他" ? "未找到可靠模块证据" : "EOS 源码使用位置",
                    Evidence = new List<EvidenceItem>()
                };
            }

            if (nameCandidates.Count > 0)
            {
                table.ChineseName = SelectPreferredName(table.ChineseName, nameCandidates);
            }
            else if (table.ChineseName == null || table.ChineseName.Status == ConfidenceStatus.PendingConfirmation)
            {
                bool isFullyTranslated = IdentifierTranslationService.IsTableNameFullyTranslated(table.ObjectName);
                string guessedName = GuessObjectName(table.ObjectName);
                bool isMissing = !IdentifierTranslationService.IsReliableChineseName(guessedName);
                table.ChineseName = new MetadataValue
                {
                    Value = guessedName,
                    Status = isMissing ? ConfidenceStatus.PendingConfirmation : ConfidenceStatus.Guessed,
                    SourceType = isFullyTranslated && !isMissing ? "名称翻译" : "规则推测",
                    SourceSummary = isMissing
                        ? "未形成可靠中文名称"
                        : isFullyTranslated ? "英文表名拆分翻译" : "英文表名保守推测",
                    Evidence = new List<EvidenceItem>()
                };
            }

            if (table.ModuleName == null)
            {
                table.ModuleName = new MetadataValue
                {
                    Value = "其他",
                    Status = ConfidenceStatus.Guessed,
                    SourceSummary = "没有明确模块证据",
                    Evidence = new List<EvidenceItem>()
                };
            }
            if (table.EntityName == null || table.EntityName.Status == ConfidenceStatus.PendingConfirmation)
            {
                table.EntityName = null;
            }
        }

        /// <summary>XMZADD 20260831 合并字段级源码证据并生成字段名称、实体属性、枚举和关联说明。</summary>
        private static void ApplyFieldEvidence(TableMetadata table, FieldMetadata field, IList<SourceEvidence> evidence,
            IDictionary<string, List<SourceEvidence>> enumEvidence)
        {
            NormalizeExistingAutomaticName(field.ChineseName);
            var nameCandidates = new List<MetadataCandidate>();
            for (int i = 0; i < evidence.Count; i++)
            {
                SourceEvidence item = evidence[i];
                NameCandidate normalized = IdentifierTranslationService.NormalizeNameCandidate(item.ChineseNameCandidate);
                if (IdentifierTranslationService.IsReliableChineseName(normalized.Value))
                {
                    nameCandidates.Add(new MetadataCandidate(normalized.Value, ConfidenceStatus.CodeEvidence,
                        "EOS 源码字段注释", CreatePublishedEvidence(item.Evidence, normalized.RawEvidence)));
                }
                if (IsBusinessEntityName(item.EntityName) &&
                    (item.Evidence == null || item.Evidence.RuleName == "EntityProperty"))
                {
                    string propertyName = string.IsNullOrWhiteSpace(item.PropertyName) ? field.FieldName : item.PropertyName;
                    field.EntityPropertyName = new MetadataValue
                    {
                        Value = item.EntityName + "." + propertyName,
                        Status = ConfidenceStatus.CodeEvidence,
                        SourceSummary = "EOS 源码实体属性",
                        Evidence = item.Evidence == null
                            ? new List<EvidenceItem>()
                            : new List<EvidenceItem> { CreatePublishedEvidence(item.Evidence, propertyName) }
                    };
                }
            }

            if (nameCandidates.Count > 0)
            {
                field.ChineseName = SelectPreferredName(field.ChineseName, nameCandidates);
            }
            else if (field.ChineseName == null || field.ChineseName.Status == ConfidenceStatus.PendingConfirmation)
            {
                bool isFullyTranslated = IdentifierTranslationService.IsFieldNameFullyTranslated(field.FieldName);
                string guessedName = GuessFieldName(field.FieldName);
                bool isMissing = !IdentifierTranslationService.IsReliableChineseName(guessedName);
                field.ChineseName = new MetadataValue
                {
                    Value = guessedName,
                    Status = isMissing ? ConfidenceStatus.PendingConfirmation : ConfidenceStatus.Guessed,
                    SourceType = isFullyTranslated && !isMissing ? "名称翻译" : "规则推测",
                    SourceSummary = isMissing
                        ? "未形成可靠中文名称"
                        : isFullyTranslated ? "英文字段名拆分翻译" : "英文字段名保守推测",
                    Evidence = new List<EvidenceItem>()
                };
            }

            if (field.EntityPropertyName == null)
            {
                field.EntityPropertyName = null;
            }
            ApplyEnumEvidence(field, evidence, enumEvidence);
            if (field.RelationSummary == null || field.RelationSummary.Status == ConfidenceStatus.PendingConfirmation)
            {
                if (field.IsForeignKey)
                {
                    field.RelationSummary = new MetadataValue
                    {
                        Value = "已发现数据库外键",
                        Status = ConfidenceStatus.DatabaseEvidence,
                        SourceSummary = "SQL Server 外键约束",
                        Evidence = new List<EvidenceItem>()
                    };
                }
                else
                {
                    field.RelationSummary = new MetadataValue
                    {
                        Value = "推测：未发现明确关联",
                        Status = ConfidenceStatus.Guessed,
                        SourceSummary = "没有真实外键约束",
                        Evidence = new List<EvidenceItem>()
                    };
                }
            }
        }

        /// <summary>XMZADD 20260831 按数据库、代码、AI 和命名规则优先级选择单一名称，避免把冲突候选直接拼接给用户。</summary>
        private static MetadataValue SelectPreferredName(MetadataValue existing, IList<MetadataCandidate> codeCandidates)
        {
            if (!MetadataEvidencePolicy.CanReplace(existing, ConfidenceStatus.CodeEvidence, "SourceCodeName"))
            {
                return existing;
            }

            var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firstCandidates = new Dictionary<string, MetadataCandidate>(StringComparer.OrdinalIgnoreCase);
            string selectedValue = null;
            int selectedCount = 0;
            for (int i = 0; i < codeCandidates.Count; i++)
            {
                MetadataCandidate candidate = codeCandidates[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Value))
                {
                    continue;
                }
                int count;
                votes.TryGetValue(candidate.Value, out count);
                count++;
                votes[candidate.Value] = count;
                if (!firstCandidates.ContainsKey(candidate.Value))
                {
                    firstCandidates.Add(candidate.Value, candidate);
                }
                if (count > selectedCount)
                {
                    selectedValue = candidate.Value;
                    selectedCount = count;
                }
            }
            if (!string.IsNullOrWhiteSpace(selectedValue))
            {
                MetadataCandidate selected = firstCandidates[selectedValue];
                return new MetadataValue
                {
                    Value = selected.Value,
                    Status = ConfidenceStatus.CodeEvidence,
                    SourceType = "代码依据",
                    SourceSummary = selected.SourceSummary,
                    OriginalAutomaticValue = existing == null ? string.Empty : existing.Value,
                    Evidence = selected.Evidence == null
                        ? new List<EvidenceItem>()
                        : new List<EvidenceItem> { selected.Evidence }
                };
            }
            return existing;
        }

        /// <summary>XMZADD 20260901 只清理自动数据库或源码名称中的等号表达，人工和锁定值保持原样。</summary>
        private static void NormalizeExistingAutomaticName(MetadataValue existing)
        {
            if (existing == null || existing.IsManualOverride || existing.IsLocked ||
                (existing.Status != ConfidenceStatus.DatabaseEvidence && existing.Status != ConfidenceStatus.CodeEvidence &&
                 existing.Status != ConfidenceStatus.KnowledgeBaseEvidence) || string.IsNullOrWhiteSpace(existing.Value) ||
                existing.Value.IndexOf('=') < 0)
            {
                return;
            }
            NameCandidate normalized = IdentifierTranslationService.NormalizeNameCandidate(existing.Value);
            if (string.IsNullOrWhiteSpace(normalized.Value))
            {
                return;
            }
            existing.Value = normalized.Value;
            if (existing.Evidence == null)
            {
                existing.Evidence = new List<EvidenceItem>();
            }
            existing.Evidence.Add(new EvidenceItem
            {
                SourceType = existing.SourceType,
                RuleName = "NormalizeNameCandidate",
                RawValue = normalized.RawEvidence,
                OriginalText = normalized.RawEvidence,
                Explanation = "名称候选取首个等号右侧非空文本"
            });
        }

        /// <summary>XMZADD 20260901 复制并脱敏源码名称证据，确保公开快照不包含本机绝对目录。</summary>
        private static EvidenceItem CreatePublishedEvidence(EvidenceItem source, string originalText)
        {
            string sourcePath = source == null ? string.Empty : (source.SourcePath ?? string.Empty);
            if (Path.IsPathRooted(sourcePath) || sourcePath.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                sourcePath = Path.GetFileName(sourcePath);
            }
            else
            {
                sourcePath = sourcePath.Replace('\\', '/');
            }
            return new EvidenceItem
            {
                SourceType = source == null || string.IsNullOrWhiteSpace(source.SourceType) ? "EOS源码" : source.SourceType,
                SourcePath = sourcePath,
                SourceLine = source == null ? 0 : source.SourceLine,
                RuleName = source == null ? "NameCandidate" : source.RuleName,
                RawValue = originalText,
                OriginalText = originalText,
                Explanation = LimitEvidenceText(source == null ? "源码中文名称候选" : source.Explanation)
            };
        }

        /// <summary>XMZADD 20260901 截断源码证据摘要，避免长代码注释进入公开快照。</summary>
        private static string LimitEvidenceText(string value)
        {
            string text = (value ?? string.Empty).Trim();
            const int maximumLength = 240;
            return text.Length <= maximumLength ? text : text.Substring(0, maximumLength);
        }

        /// <summary>XMZADD 20260831 将源码路径归并为业务模块，并排除技术目录和文件名。</summary>
        private static string TranslateModule(string modulePath)
        {
            string value = (modulePath ?? string.Empty).Trim();
            string key = value.ToUpperInvariant();
            string businessModule = TranslateBusinessModule(key);
            if (!string.IsNullOrWhiteSpace(businessModule)) return businessModule;
            if (IdentifierTranslationService.IsTechnicalModuleName(value)) return "其他";
            MatchCollection chineseMatches = Regex.Matches(value, "[\\u4e00-\\u9fff]+");
            if (chineseMatches.Count > 0)
            {
                var chinese = new StringBuilder();
                for (int i = 0; i < chineseMatches.Count; i++) chinese.Append(chineseMatches[i].Value);
                return chinese.ToString();
            }
            return "其他";
        }

        /// <summary>XMZADD 20260831 识别源码路径中的真实业务域，避免技术目录覆盖采购等业务模块。</summary>
        private static string TranslateBusinessModule(string key)
        {
            if (key.IndexOf("物料", StringComparison.Ordinal) >= 0 || key.Contains("ITEM") || key.Contains("MATERIAL")) return "物料";
            if (key.IndexOf("采购", StringComparison.Ordinal) >= 0 || key.Contains("PURCHASE")) return "采购";
            if (key.IndexOf("生产", StringComparison.Ordinal) >= 0 || key.Contains("MANUFACTURE") || key.Contains("PRODUCTION")) return "生产制造";
            if (key.IndexOf("质量", StringComparison.Ordinal) >= 0 || key.Contains("QUALITY")) return "质量";
            // 财务域必须有财务、会计或凭证上下文，ACCOUNT 单词本身仅表示一类流水记录。
            if (key.IndexOf("财务", StringComparison.Ordinal) >= 0 || key.IndexOf("会计", StringComparison.Ordinal) >= 0 ||
                key.IndexOf("凭证", StringComparison.Ordinal) >= 0 || key.Contains("FINANCE") || key.Contains("VOUCHER")) return "财务";
            if (key.IndexOf("仓储", StringComparison.Ordinal) >= 0 || key.Contains("WAREHOUSE") ||
                key.Contains("STOCK") || key.Contains("INVENTORY") || key.Contains("PALLET")) return "仓储";
            if (key.IndexOf("销售", StringComparison.Ordinal) >= 0 || key.Contains("SALES")) return "销售";
            if (key.IndexOf("工艺", StringComparison.Ordinal) >= 0 || key.Contains("PROCESS")) return "工艺";
            return null;
        }

        /// <summary>XMZADD 20260831 累计同一表在源码目录中的模块使用次数，避免最后一条证据覆盖主要业务模块。</summary>
        private static void AddModuleVote(IDictionary<string, int> votes, string module)
        {
            int count;
            votes.TryGetValue(module, out count);
            votes[module] = count + 1;
        }

        /// <summary>XMZADD 20260831 优先选择使用次数最多的明确中文模块，无法确认时统一归入其他。</summary>
        private static string SelectModule(IDictionary<string, int> votes)
        {
            string selected = "其他";
            int selectedCount = 0;
            foreach (KeyValuePair<string, int> pair in votes)
            {
                if (pair.Key != "其他" && pair.Value > selectedCount)
                {
                    selected = pair.Key;
                    selectedCount = pair.Value;
                }
            }
            return selected;
        }

        /// <summary>XMZADD 20260831 按实体属性声明类型匹配源码枚举成员，未匹配时保持枚举为空。</summary>
        private static void ApplyEnumEvidence(FieldMetadata field, IList<SourceEvidence> fieldEvidence,
            IDictionary<string, List<SourceEvidence>> enumEvidence)
        {
            string enumName = null;
            SourceEvidence enumTypeEvidence = null;
            for (int i = 0; i < fieldEvidence.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(fieldEvidence[i].PropertyTypeName) && enumEvidence.ContainsKey(fieldEvidence[i].PropertyTypeName))
                {
                    enumName = fieldEvidence[i].PropertyTypeName;
                    enumTypeEvidence = fieldEvidence[i];
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(enumName))
            {
                if (field.EnumName == null || field.EnumName.Status == ConfidenceStatus.PendingConfirmation)
                {
                    field.EnumName = null;
                }
                return;
            }

            field.EnumName = new MetadataValue
            {
                Value = enumName,
                Status = ConfidenceStatus.CodeEvidence,
                SourceType = "EOS源码",
                SourceSummary = "EOS 源码枚举",
                Evidence = enumTypeEvidence == null || enumTypeEvidence.Evidence == null
                    ? new List<EvidenceItem>()
                    : new List<EvidenceItem> { CreatePublishedEvidence(enumTypeEvidence.Evidence, enumName) }
            };
            // 快照反序列化可能返回数组形式的只读 IList，源码枚举重建必须换成独立可写集合。
            field.EnumItems = new List<EnumItemMetadata>();
            List<SourceEvidence> members = enumEvidence[enumName];
            for (int i = 0; i < members.Count; i++)
            {
                string itemValue = string.IsNullOrWhiteSpace(members[i].EnumRawValue) ? members[i].EnumValue : members[i].EnumRawValue;
                string itemName = IdentifierTranslationService.IsReliableChineseName(members[i].EnumChineseName) ? members[i].EnumChineseName : members[i].EnumValue;
                field.EnumItems.Add(new EnumItemMetadata
                {
                    Value = itemValue,
                    ChineseName = new MetadataValue
                    {
                        Value = itemName,
                        Status = ConfidenceStatus.CodeEvidence,
                        SourceType = "EOS源码",
                        SourceSummary = "EOS 源码枚举成员",
                        Evidence = members[i].Evidence == null
                            ? new List<EvidenceItem>()
                            : new List<EvidenceItem> { CreatePublishedEvidence(members[i].Evidence, itemValue) }
                    }
                });
            }
        }

        /// <summary>XMZADD 20260831 为缺少证据的表名生成可显示的保守中文业务表名称。</summary>
        private static string GuessObjectName(string objectName)
        {
            string translated = IdentifierTranslationService.TranslateTableName(objectName);
            return string.IsNullOrWhiteSpace(translated) ? "待确认" : translated;
        }

        /// <summary>XMZADD 20260831 为缺少证据的字段名生成可显示的保守中文字段名称。</summary>
        private static string GuessFieldName(string fieldName)
        {
            string translated = IdentifierTranslationService.TranslateFieldName(fieldName);
            return string.IsNullOrWhiteSpace(translated) ? "待确认" : translated;
        }
    }
}
