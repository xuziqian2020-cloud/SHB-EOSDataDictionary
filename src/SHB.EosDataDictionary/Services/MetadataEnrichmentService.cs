using System;
using System.Collections.Generic;
using System.IO;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260916 将 EOS 源码证据合并到快照，并分别生成名称、实体、模块及字段语义。</summary>
    public static class MetadataEnrichmentService
    {
        private static readonly List<SourceEvidence> EmptyEvidence = new List<SourceEvidence>();

        /// <summary>XMZADD 20260916 按表和字段精确匹配源码证据，并独立归并名称、实体与跨模块用途。</summary>
        public static void Enrich(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }

            var objectEvidence = new Dictionary<string, List<SourceEvidence>>(StringComparer.OrdinalIgnoreCase);
            var fieldEvidence = new Dictionary<string, List<SourceEvidence>>(StringComparer.OrdinalIgnoreCase);
            var enumEvidence = new Dictionary<string, List<SourceEvidence>>(StringComparer.OrdinalIgnoreCase);
            var moduleEvidence = new Dictionary<string, List<SourceEvidence>>(StringComparer.OrdinalIgnoreCase);
            // 先建立索引，避免真实库的每个字段都重复扫描全部源码证据。
            BuildEvidenceIndex(sourceEvidence, objectEvidence, fieldEvidence, enumEvidence, moduleEvidence);

            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                IList<SourceEvidence> tableEvidence = GetEvidence(objectEvidence, MakeObjectKey(table.ObjectName));
                IList<SourceEvidence> currentModuleEvidence = GetEvidence(
                    moduleEvidence, MakeObjectKey(table.ObjectName));
                ApplyTableEvidence(table, tableEvidence, currentModuleEvidence);

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

        /// <summary>XMZADD 20260916 按对象、字段、枚举和模块用途建立索引，避免批量快照重复扫描全部证据。</summary>
        private static void BuildEvidenceIndex(IList<SourceEvidence> sourceEvidence,
            IDictionary<string, List<SourceEvidence>> objectEvidence,
            IDictionary<string, List<SourceEvidence>> fieldEvidence,
            IDictionary<string, List<SourceEvidence>> enumEvidence,
            IDictionary<string, List<SourceEvidence>> moduleEvidence)
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
                if (!string.IsNullOrWhiteSpace(item.ObjectName))
                {
                    // 字段读写、界面展示和关系证据同样能证明整张表被哪些业务模块使用。
                    AddEvidence(moduleEvidence, MakeObjectKey(item.ObjectName), item);
                }
                // 生成实体枚举可能只是代码生成器镜像，必须存在更高强度的上下文或业务证据才能发布。
                if (!string.IsNullOrWhiteSpace(item.EnumName) &&
                    item.Evidence != null &&
                    item.Evidence.RuleName == "EnumMember" &&
                    item.Strength != SourceEvidenceStrength.NamingOnly)
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

        /// <summary>XMZADD 20260916 合并表级名称与实体证据，并用全部字段用途证据计算主模块和消费模块。</summary>
        private static void ApplyTableEvidence(TableMetadata table, IList<SourceEvidence> evidence,
            IList<SourceEvidence> moduleEvidence)
        {
            NormalizeExistingAutomaticName(table.ChineseName);
            var nameCandidates = new List<MetadataCandidate>();
            for (int i = 0; i < evidence.Count; i++)
            {
                SourceEvidence item = evidence[i];
                NameCandidate normalized = IdentifierTranslationService.NormalizeNameCandidate(item.ChineseNameCandidate);
                if (IdentifierTranslationService.IsReliableChineseName(normalized.Value))
                {
                    nameCandidates.Add(new MetadataCandidate(normalized.Value, ConfidenceStatus.CodeEvidence,
                        "EOS 源码注释", CreatePublishedEvidence(item.Evidence, normalized.RawEvidence),
                        item.Strength == SourceEvidenceStrength.Authoritative ? 98 : 0,
                        item.Strength == SourceEvidenceStrength.Authoritative ? "权威源码注释" : "代码依据"));
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

            }
            new BusinessModuleAttributionService().Apply(table, moduleEvidence);

            var layeredCandidates = CreateLayerCandidates(nameCandidates);
            if (layeredCandidates.Count == 0 &&
                (table.ChineseName == null || table.ChineseName.Status == ConfidenceStatus.PendingConfirmation))
            {
                bool isFullyTranslated = IdentifierTranslationService.IsTableNameFullyTranslated(table.ObjectName);
                string guessedName = GuessObjectName(table.ObjectName);
                bool isMissing = !IdentifierTranslationService.IsReliableChineseName(guessedName);
                if (!isMissing)
                {
                    layeredCandidates.Add(new MetadataValue
                    {
                        Value = guessedName,
                        Status = ConfidenceStatus.Guessed,
                        ConfidenceScore = isFullyTranslated ? 72 : 65,
                        SourceType = isFullyTranslated ? "名称翻译" : "规则推测",
                        SourceSummary = isFullyTranslated ? "英文表名拆分翻译" : "英文表名保守推测",
                        Evidence = new List<EvidenceItem>
                        {
                            new EvidenceItem
                            {
                                SourceType = isFullyTranslated ? "名称翻译" : "规则推测",
                                RuleName = "IdentifierTranslation",
                                RawValue = table.ObjectName,
                                OriginalText = table.ObjectName,
                                Explanation = "英文表名拆分形成参考译名。"
                            }
                        }
                    });
                }
            }
            new BusinessNameLayerService().ApplyTableCandidates(table, layeredCandidates);

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
                        "EOS 源码字段注释", CreatePublishedEvidence(item.Evidence, normalized.RawEvidence),
                        item.Strength == SourceEvidenceStrength.Authoritative ? 98 : 0,
                        item.Strength == SourceEvidenceStrength.Authoritative ? "权威源码注释" : "代码依据"));
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

            var layeredCandidates = CreateLayerCandidates(nameCandidates);
            if (layeredCandidates.Count == 0 &&
                (field.ChineseName == null || field.ChineseName.Status == ConfidenceStatus.PendingConfirmation))
            {
                bool isFullyTranslated = IdentifierTranslationService.IsFieldNameFullyTranslated(field.FieldName);
                string guessedName = GuessFieldName(field.FieldName);
                bool isMissing = !IdentifierTranslationService.IsReliableChineseName(guessedName);
                if (!isMissing)
                {
                    layeredCandidates.Add(new MetadataValue
                    {
                        Value = guessedName,
                        Status = ConfidenceStatus.Guessed,
                        ConfidenceScore = isFullyTranslated ? 72 : 65,
                        SourceType = isFullyTranslated ? "名称翻译" : "规则推测",
                        SourceSummary = isFullyTranslated ? "英文字段名拆分翻译" : "英文字段名保守推测",
                        Evidence = new List<EvidenceItem>
                        {
                            new EvidenceItem
                            {
                                SourceType = isFullyTranslated ? "名称翻译" : "规则推测",
                                RuleName = "IdentifierTranslation",
                                RawValue = field.FieldName,
                                OriginalText = field.FieldName,
                                Explanation = "英文字段名拆分形成参考译名。"
                            }
                        }
                    });
                }
            }
            new BusinessNameLayerService().ApplyFieldCandidates(field, layeredCandidates);

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

        /// <summary>XMZADD 20260915 将源码名称候选转换为带评分和完整证据的双层名称输入。</summary>
        private static List<MetadataValue> CreateLayerCandidates(IList<MetadataCandidate> codeCandidates)
        {
            var result = new List<MetadataValue>();
            for (int i = 0; i < codeCandidates.Count; i++)
            {
                MetadataCandidate candidate = codeCandidates[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Value))
                {
                    continue;
                }
                string ruleName = candidate.Evidence == null
                    ? string.Empty
                    : candidate.Evidence.RuleName ?? string.Empty;
                int score = candidate.ConfidenceScore > 0
                    ? candidate.ConfidenceScore
                    : (IsDirectNameRule(ruleName) ? 94 : 84);
                var evidence = new List<EvidenceItem>();
                if (candidate.Evidence != null)
                {
                    evidence.Add(candidate.Evidence);
                }
                result.Add(new MetadataValue
                {
                    Value = candidate.Value,
                    Status = candidate.Status,
                    ConfidenceScore = score,
                    SourceType = string.IsNullOrWhiteSpace(candidate.SourceType)
                        ? "代码依据"
                        : candidate.SourceType,
                    SourceSummary = candidate.SourceSummary,
                    Evidence = evidence
                });
            }
            return result;
        }

        /// <summary>XMZADD 20260915 识别唯一绑定物理字段的直接标题和 SQL 别名规则。</summary>
        private static bool IsDirectNameRule(string ruleName)
        {
            return string.Equals(ruleName, "GridColumnCaption", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "DataColumnCaption", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "SqlColumnAlias", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "SqlFieldRelation", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "EntityFieldAssignment", StringComparison.Ordinal);
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
            var writableEvidence = new List<EvidenceItem>();
            if (existing.Evidence != null)
            {
                for (int evidenceIndex = 0; evidenceIndex < existing.Evidence.Count; evidenceIndex++)
                {
                    if (existing.Evidence[evidenceIndex] != null)
                    {
                        writableEvidence.Add(existing.Evidence[evidenceIndex]);
                    }
                }
            }
            // 历史快照可能把证据反序列化为固定长度数组，名称清洗前必须换成可写集合。
            existing.Evidence = writableEvidence;
            writableEvidence.Add(new EvidenceItem
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
