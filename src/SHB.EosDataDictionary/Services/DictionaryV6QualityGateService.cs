using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260916 以版本化金标准和全量业务覆盖指标决定 V6 候选能否提升为正式字典。</summary>
    public sealed class DictionaryV6QualityGateService
    {
        public const double MinimumOfficialNameAccuracy = 0.99D;
        public const double MinimumSuggestedNameAccuracy = 0.95D;
        public const double MinimumRelationDirectionAccuracy = 0.98D;
        public const int MinimumGoldTableCount = 150;
        public const int MinimumGoldFieldCount = 1000;
        public const int MinimumGoldRelationCount = 100;

        private static readonly string[] ActualUsageRules =
        {
            "GridColumnCaption",
            "SqlFieldRelation",
            "EntityFieldAssignment",
            "DynamicTableFieldUsage",
            "SqlFieldUsage"
        };

        /// <summary>XMZADD 20260916 读取并验证金标结构、稳定键、中文名称和公开证据路径。</summary>
        public DictionaryV6GoldStandard LoadGoldStandard(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("V6 金标准文件不存在。", path);
            }
            DictionaryV6GoldStandard gold;
            try
            {
                var serializer = new DataContractJsonSerializer(
                    typeof(DictionaryV6GoldStandard),
                    new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = int.MaxValue });
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    gold = serializer.ReadObject(stream) as DictionaryV6GoldStandard;
                }
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                throw new InvalidDataException("V6 金标准 JSON 无法读取。", exception);
            }
            ValidateGoldStandard(gold);
            return gold;
        }

        /// <summary>XMZADD 20260916 对已计算指标执行发布硬门槛，返回全部失败原因而非只报告第一项。</summary>
        public DictionaryV6QualityResult Evaluate(DictionaryV6QualityInput input)
        {
            if (input == null)
            {
                throw new ArgumentNullException("input");
            }
            var result = new DictionaryV6QualityResult
            {
                OfficialNameAccuracy = input.OfficialNameAccuracy,
                SuggestedNameAccuracy = input.SuggestedNameAccuracy,
                RelationDirectionAccuracy = input.RelationDirectionAccuracy,
                UsedFieldGapCount = input.UsedFieldGapCount,
                PseudoChineseOfficialCount = input.PseudoChineseOfficialCount,
                OfficialConflictCount = input.OfficialConflictCount,
                EntityTableCount = input.EntityTableCount,
                AuditedEntityTableCount = input.AuditedEntityTableCount,
                GoldTableCount = input.GoldTableCount,
                GoldFieldCount = input.GoldFieldCount,
                GoldRelationCount = input.GoldRelationCount
            };
            if (input.OfficialNameAccuracy < MinimumOfficialNameAccuracy)
            {
                result.Failures.Add("正式名称准确率低于 99%。");
            }
            if (input.SuggestedNameAccuracy < MinimumSuggestedNameAccuracy)
            {
                result.Failures.Add("参考译名准确率低于 95%。");
            }
            if (input.RelationDirectionAccuracy < MinimumRelationDirectionAccuracy)
            {
                result.Failures.Add("关系方向准确率低于 98%。");
            }
            if (input.UsedFieldGapCount > 0)
            {
                result.Failures.Add("存在实际使用字段同时缺少可靠正式名和参考名。");
            }
            if (input.PseudoChineseOfficialCount > 0)
            {
                result.Failures.Add("存在伪中文正式名称或未翻译英文残片。");
            }
            if (input.OfficialConflictCount > 0)
            {
                result.Failures.Add("存在尚未裁决的正式名称冲突。");
            }
            if (input.EntityTableCount <= 0 || input.AuditedEntityTableCount != input.EntityTableCount)
            {
                result.Failures.Add("EOS 实体表审计覆盖不完整。");
            }
            if (input.GoldTableCount < MinimumGoldTableCount ||
                input.GoldFieldCount < MinimumGoldFieldCount ||
                input.GoldRelationCount < MinimumGoldRelationCount)
            {
                result.Failures.Add("金标准规模低于 150 表、1000 字段或 100 关系的最低门槛。");
            }
            result.CanPromote = result.Failures.Count == 0;
            return result;
        }

        /// <summary>XMZADD 20260916 对全量候选执行金标比对、实际使用字段检查、伪中文检查和实体覆盖审计。</summary>
        public DictionaryV6QualityResult Evaluate(SnapshotData snapshot,
            IList<SourceEvidence> sourceEvidence, DictionaryV6GoldStandard gold)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            ValidateGoldStandard(gold);
            var tableIndex = BuildTableIndex(snapshot);
            var relationIndex = BuildOfficialRelationIndex(snapshot);
            var usageIndex = BuildActualUsageIndex(sourceEvidence);
            var goldEvaluations = new List<DictionaryV6GoldEvaluation>();
            int officialMatches = 0;
            int suggestedMatches = 0;
            int nameSamples = 0;
            int relationMatches = 0;
            int relationSamples = 0;
            int goldTableCount = 0;
            int goldFieldCount = 0;
            int goldRelationCount = 0;

            for (int index = 0; index < gold.Entries.Count; index++)
            {
                DictionaryV6GoldEntry entry = gold.Entries[index];
                if (string.Equals(entry.Kind, "Relation", StringComparison.Ordinal))
                {
                    relationSamples++;
                    goldRelationCount++;
                    bool relationMatch = relationIndex.Contains(MakeRelationKey(entry));
                    if (relationMatch) relationMatches++;
                    goldEvaluations.Add(CreateRelationEvaluation(entry, relationMatch));
                    continue;
                }

                if (string.Equals(entry.Kind, "Table", StringComparison.Ordinal)) goldTableCount++;
                if (string.Equals(entry.Kind, "Field", StringComparison.Ordinal)) goldFieldCount++;
                nameSamples++;
                string officialName;
                string suggestedName;
                FindNames(entry, tableIndex, out officialName, out suggestedName);
                bool officialMatch = IsExpectedNameMatch(entry, officialName);
                bool suggestedMatch = officialMatch || IsExpectedNameMatch(entry, suggestedName);
                if (officialMatch) officialMatches++;
                if (suggestedMatch) suggestedMatches++;
                goldEvaluations.Add(CreateNameEvaluation(
                    entry, officialName, suggestedName, officialMatch, suggestedMatch));
            }

            var gaps = new List<DictionaryV6NameAuditRecord>();
            var pseudoChinese = new List<DictionaryV6NameAuditRecord>();
            var conflicts = new List<DictionaryV6NameAuditRecord>();
            int entityTableCount = 0;
            int auditedEntityTableCount = 0;
            AuditSnapshot(snapshot, usageIndex, gaps, pseudoChinese, conflicts,
                ref entityTableCount, ref auditedEntityTableCount);

            DictionaryV6QualityResult result = Evaluate(new DictionaryV6QualityInput
            {
                OfficialNameAccuracy = Divide(officialMatches, nameSamples),
                SuggestedNameAccuracy = Divide(suggestedMatches, nameSamples),
                RelationDirectionAccuracy = Divide(relationMatches, relationSamples),
                UsedFieldGapCount = gaps.Count,
                PseudoChineseOfficialCount = pseudoChinese.Count,
                OfficialConflictCount = conflicts.Count,
                EntityTableCount = entityTableCount,
                AuditedEntityTableCount = auditedEntityTableCount,
                GoldTableCount = goldTableCount,
                GoldFieldCount = goldFieldCount,
                GoldRelationCount = goldRelationCount
            });
            result.GoldEvaluations = goldEvaluations;
            result.UsedFieldGaps = gaps;
            result.PseudoChineseOfficials = pseudoChinese;
            result.OfficialConflicts = conflicts;
            SortQualityDetails(result);
            return result;
        }

        /// <summary>XMZADD 20260916 验证金标条目的类型、稳定键、证据来源与关系端点均可独立审计。</summary>
        private static void ValidateGoldStandard(DictionaryV6GoldStandard gold)
        {
            if (gold == null || gold.Entries == null)
            {
                throw new InvalidDataException("V6 金标准没有可评估条目。");
            }
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < gold.Entries.Count; index++)
            {
                DictionaryV6GoldEntry entry = gold.Entries[index];
                if (entry == null || string.IsNullOrWhiteSpace(entry.StableKey) ||
                    !keys.Add(entry.StableKey))
                {
                    throw new InvalidDataException("V6 金标准包含空值或重复稳定键。");
                }
                bool isTable = string.Equals(entry.Kind, "Table", StringComparison.Ordinal);
                bool isField = string.Equals(entry.Kind, "Field", StringComparison.Ordinal);
                bool isRelation = string.Equals(entry.Kind, "Relation", StringComparison.Ordinal);
                if (!isTable && !isField && !isRelation)
                {
                    throw new InvalidDataException("V6 金标准包含未知条目类型。");
                }
                if (string.IsNullOrWhiteSpace(entry.ObjectName) ||
                    string.IsNullOrWhiteSpace(entry.EvidenceType) ||
                    string.IsNullOrWhiteSpace(entry.Source) || ContainsAbsolutePath(entry.Source))
                {
                    throw new InvalidDataException("V6 金标准缺少对象、证据或包含本机绝对路径。");
                }
                if (isField && string.IsNullOrWhiteSpace(entry.FieldName))
                {
                    throw new InvalidDataException("V6 字段金标缺少字段名。");
                }
                if (isRelation)
                {
                    if (string.IsNullOrWhiteSpace(entry.ParentTableName) ||
                        string.IsNullOrWhiteSpace(entry.ParentFieldName) ||
                        string.IsNullOrWhiteSpace(entry.ChildTableName) ||
                        string.IsNullOrWhiteSpace(entry.ChildFieldName))
                    {
                        throw new InvalidDataException("V6 关系金标缺少父子端点。");
                    }
                }
                else if (!string.Equals(entry.ExpectedDisposition, "MustRemainPending", StringComparison.Ordinal) &&
                         !IdentifierTranslationService.IsReliableChineseName(entry.ExpectedChineseName))
                {
                    throw new InvalidDataException("V6 名称金标包含不可靠中文值：" + entry.StableKey);
                }
            }
        }

        /// <summary>XMZADD 20260916 建立架构和对象名索引，避免 1,400 条金标重复扫描七万余张表。</summary>
        private static IDictionary<string, TableMetadata> BuildTableIndex(SnapshotData snapshot)
        {
            var result = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            if (snapshot.Tables == null)
            {
                return result;
            }
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                if (table == null || string.IsNullOrWhiteSpace(table.ObjectName))
                {
                    continue;
                }
                string key = MakeObjectKey(table.SchemaName, table.ObjectName);
                if (!result.ContainsKey(key)) result.Add(key, table);
            }
            return result;
        }

        /// <summary>XMZADD 20260916 仅把数据库或交叉核验代码关系加入正式关系索引，排除单字段命名推测。</summary>
        private static ISet<string> BuildOfficialRelationIndex(SnapshotData snapshot)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (snapshot.Tables == null)
            {
                return result;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Relations == null)
                {
                    continue;
                }
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = table.Relations[relationIndex];
                    if (relation == null || relation.RelationType == null ||
                        (relation.RelationType.Status != ConfidenceStatus.DatabaseEvidence &&
                         relation.RelationType.Status != ConfidenceStatus.CodeEvidence))
                    {
                        continue;
                    }
                    result.Add(MakeRelationKey(relation));
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260916 合并字段内固化用途和本轮源码用途，为全量缺名审计建立索引。</summary>
        private static ISet<string> BuildActualUsageIndex(IList<SourceEvidence> sourceEvidence)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sourceEvidence == null)
            {
                return result;
            }
            for (int index = 0; index < sourceEvidence.Count; index++)
            {
                SourceEvidence evidence = sourceEvidence[index];
                if (evidence == null || evidence.Evidence == null ||
                    string.IsNullOrWhiteSpace(evidence.ObjectName) ||
                    string.IsNullOrWhiteSpace(evidence.FieldName) ||
                    !IsActualUsageRule(evidence.Evidence.RuleName))
                {
                    continue;
                }
                result.Add(MakeFieldKey(evidence.ObjectName, evidence.FieldName));
            }
            return result;
        }

        /// <summary>XMZADD 20260916 逐表审计实际使用字段、正式名质量、冲突和实体表完整性。</summary>
        private static void AuditSnapshot(SnapshotData snapshot, ISet<string> usageIndex,
            IList<DictionaryV6NameAuditRecord> gaps,
            IList<DictionaryV6NameAuditRecord> pseudoChinese,
            IList<DictionaryV6NameAuditRecord> conflicts,
            ref int entityTableCount, ref int auditedEntityTableCount)
        {
            if (snapshot.Tables == null)
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
                AuditOfficialName("Table", table, null, table.ChineseName,
                    table.SuggestedChineseName, pseudoChinese, conflicts);
                if (HasValue(table.EntityName))
                {
                    entityTableCount++;
                    // 实体审计只负责确认对象已经完成业务价值分类；名称、模块和伪中文由各自专项报告独立揭示，避免重复阻塞。
                    if (table.Category != DictionaryTableCategory.Unclassified)
                    {
                        auditedEntityTableCount++;
                    }
                }
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
                    AuditOfficialName("Field", table, field, field.ChineseName,
                        field.SuggestedChineseName, pseudoChinese, conflicts);
                    bool isUsed = HasValue(field.Usage) ||
                                  usageIndex.Contains(MakeFieldKey(table.ObjectName, field.FieldName));
                    if (isUsed && !HasAnyReliableName(field.ChineseName, field.SuggestedChineseName))
                    {
                        gaps.Add(CreateAuditRecord("Field", table, field,
                            field.ChineseName, field.SuggestedChineseName,
                            "业务代码实际使用，但正式名和参考名均不可靠"));
                    }
                }
            }
        }

        /// <summary>XMZADD 20260916 把不可靠或冲突正式值加入专项清单，参考译名不伪装成正式错误。</summary>
        private static void AuditOfficialName(string objectType, TableMetadata table,
            FieldMetadata field, MetadataValue official, MetadataValue suggested,
            IList<DictionaryV6NameAuditRecord> pseudoChinese,
            IList<DictionaryV6NameAuditRecord> conflicts)
        {
            if (official == null || string.IsNullOrWhiteSpace(official.Value))
            {
                return;
            }
            if (!IdentifierTranslationService.IsReliableChineseName(official.Value))
            {
                pseudoChinese.Add(CreateAuditRecord(objectType, table, field,
                    official, suggested, "正式名称不是完整可靠中文业务名称"));
            }
            if (official.Status == ConfidenceStatus.GuessedConflict)
            {
                conflicts.Add(CreateAuditRecord(objectType, table, field,
                    official, suggested, "正式名称仍处于强证据冲突状态"));
            }
        }

        /// <summary>XMZADD 20260916 按金标对象类型读取候选正式名与首选参考名。</summary>
        private static void FindNames(DictionaryV6GoldEntry entry,
            IDictionary<string, TableMetadata> tableIndex,
            out string officialName, out string suggestedName)
        {
            officialName = string.Empty;
            suggestedName = string.Empty;
            TableMetadata table;
            if (!tableIndex.TryGetValue(MakeObjectKey(entry.SchemaName, entry.ObjectName), out table))
            {
                return;
            }
            if (string.Equals(entry.Kind, "Table", StringComparison.Ordinal))
            {
                officialName = GetValue(table.ChineseName);
                suggestedName = GetValue(table.SuggestedChineseName);
                return;
            }
            if (table.Fields == null)
            {
                return;
            }
            for (int index = 0; index < table.Fields.Count; index++)
            {
                FieldMetadata field = table.Fields[index];
                if (field != null && string.Equals(
                        field.FieldName, entry.FieldName, StringComparison.OrdinalIgnoreCase))
                {
                    officialName = GetValue(field.ChineseName);
                    suggestedName = GetValue(field.SuggestedChineseName);
                    return;
                }
            }
        }

        /// <summary>XMZADD 20260916 按正确、必须待确认或必须拒绝三类样本比较名称结论。</summary>
        private static bool IsExpectedNameMatch(DictionaryV6GoldEntry entry, string actual)
        {
            string disposition = entry.ExpectedDisposition ?? "CorrectName";
            if (string.Equals(disposition, "MustRemainPending", StringComparison.Ordinal))
            {
                return !IdentifierTranslationService.IsReliableChineseName(actual);
            }
            if (string.Equals(disposition, "MustRejectName", StringComparison.Ordinal))
            {
                return !NamesEqual(entry.ExpectedChineseName, actual);
            }
            return NamesEqual(entry.ExpectedChineseName, actual);
        }

        /// <summary>XMZADD 20260916 创建名称金标逐项比对结果，供 CSV 审计错误和缺失。</summary>
        private static DictionaryV6GoldEvaluation CreateNameEvaluation(DictionaryV6GoldEntry entry,
            string officialName, string suggestedName, bool officialMatch, bool suggestedMatch)
        {
            return new DictionaryV6GoldEvaluation
            {
                Kind = entry.Kind,
                StableKey = entry.StableKey,
                ExpectedChineseName = entry.ExpectedChineseName,
                OfficialChineseName = officialName,
                SuggestedChineseName = suggestedName,
                OfficialMatch = officialMatch,
                SuggestedMatch = suggestedMatch,
                RelationMatch = false,
                EvidenceType = entry.EvidenceType,
                Source = entry.Source
            };
        }

        /// <summary>XMZADD 20260916 创建关系金标逐项比对结果，显式记录父子方向是否命中。</summary>
        private static DictionaryV6GoldEvaluation CreateRelationEvaluation(
            DictionaryV6GoldEntry entry, bool relationMatch)
        {
            return new DictionaryV6GoldEvaluation
            {
                Kind = entry.Kind,
                StableKey = entry.StableKey,
                ExpectedChineseName = entry.ExpectedChineseName,
                OfficialChineseName = string.Empty,
                SuggestedChineseName = string.Empty,
                OfficialMatch = false,
                SuggestedMatch = false,
                RelationMatch = relationMatch,
                EvidenceType = entry.EvidenceType,
                Source = entry.Source
            };
        }

        /// <summary>XMZADD 20260916 创建包含模块、状态和首条证据的统一名称问题记录。</summary>
        private static DictionaryV6NameAuditRecord CreateAuditRecord(string objectType,
            TableMetadata table, FieldMetadata field, MetadataValue official,
            MetadataValue suggested, string reason)
        {
            EvidenceItem evidence = GetFirstEvidence(official) ?? GetFirstEvidence(suggested);
            return new DictionaryV6NameAuditRecord
            {
                ObjectType = objectType,
                SchemaName = table == null ? string.Empty : table.SchemaName ?? string.Empty,
                ObjectName = table == null ? string.Empty : table.ObjectName ?? string.Empty,
                FieldName = field == null ? string.Empty : field.FieldName ?? string.Empty,
                ModuleName = table == null ? string.Empty : GetValue(table.ModuleName),
                OfficialChineseName = GetValue(official),
                SuggestedChineseName = GetValue(suggested),
                Status = official == null ? string.Empty : official.Status.ToString(),
                Reason = reason ?? string.Empty,
                RuleName = evidence == null ? string.Empty : evidence.RuleName ?? string.Empty,
                SourcePath = evidence == null ? string.Empty : evidence.SourcePath ?? string.Empty,
                SourceLine = evidence == null ? 0 : evidence.SourceLine
            };
        }

        /// <summary>XMZADD 20260916 对逐项金标和专项问题按稳定键排序，确保多次报告可直接比较。</summary>
        private static void SortQualityDetails(DictionaryV6QualityResult result)
        {
            var goldRows = result.GoldEvaluations as List<DictionaryV6GoldEvaluation>;
            if (goldRows != null) goldRows.Sort(CompareGoldEvaluations);
            var gapRows = result.UsedFieldGaps as List<DictionaryV6NameAuditRecord>;
            if (gapRows != null) gapRows.Sort(CompareAuditRecords);
            var pseudoRows = result.PseudoChineseOfficials as List<DictionaryV6NameAuditRecord>;
            if (pseudoRows != null) pseudoRows.Sort(CompareAuditRecords);
            var conflictRows = result.OfficialConflicts as List<DictionaryV6NameAuditRecord>;
            if (conflictRows != null) conflictRows.Sort(CompareAuditRecords);
        }

        /// <summary>XMZADD 20260916 按类型和稳定键比较金标结果。</summary>
        private static int CompareGoldEvaluations(DictionaryV6GoldEvaluation left,
            DictionaryV6GoldEvaluation right)
        {
            int comparison = string.Compare(left == null ? null : left.Kind,
                right == null ? null : right.Kind, StringComparison.Ordinal);
            if (comparison != 0) return comparison;
            return string.Compare(left == null ? null : left.StableKey,
                right == null ? null : right.StableKey, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260916 按表字段稳定键比较名称审计问题。</summary>
        private static int CompareAuditRecords(DictionaryV6NameAuditRecord left,
            DictionaryV6NameAuditRecord right)
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(
                left == null ? null : left.SchemaName, right == null ? null : right.SchemaName);
            if (comparison != 0) return comparison;
            comparison = StringComparer.OrdinalIgnoreCase.Compare(
                left == null ? null : left.ObjectName, right == null ? null : right.ObjectName);
            if (comparison != 0) return comparison;
            return StringComparer.OrdinalIgnoreCase.Compare(
                left == null ? null : left.FieldName, right == null ? null : right.FieldName);
        }

        /// <summary>XMZADD 20260916 判断字段证据是否代表真实读取、写入、关联或界面绑定。</summary>
        private static bool IsActualUsageRule(string ruleName)
        {
            for (int index = 0; index < ActualUsageRules.Length; index++)
            {
                if (string.Equals(ActualUsageRules[index], ruleName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260916 判断正式名或首选参考名至少有一个完整可靠中文结论。</summary>
        private static bool HasAnyReliableName(MetadataValue official, MetadataValue suggested)
        {
            return IdentifierTranslationService.IsReliableChineseName(GetValue(official)) ||
                   IdentifierTranslationService.IsReliableChineseName(GetValue(suggested));
        }

        /// <summary>XMZADD 20260916 判断元数据值是否包含非空文本。</summary>
        private static bool HasValue(MetadataValue value)
        {
            return value != null && !string.IsNullOrWhiteSpace(value.Value);
        }

        /// <summary>XMZADD 20260916 安全读取元数据文本。</summary>
        private static string GetValue(MetadataValue value)
        {
            return value == null ? string.Empty : value.Value ?? string.Empty;
        }

        /// <summary>XMZADD 20260916 读取名称值的首条可定位证据。</summary>
        private static EvidenceItem GetFirstEvidence(MetadataValue value)
        {
            return value == null || value.Evidence == null || value.Evidence.Count == 0
                ? null
                : value.Evidence[0];
        }

        /// <summary>XMZADD 20260916 比较去除首尾空白后的中文名称。</summary>
        private static bool NamesEqual(string expected, string actual)
        {
            return string.Equals((expected ?? string.Empty).Trim(),
                (actual ?? string.Empty).Trim(), StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260916 安全计算准确率，无样本时返回零以阻止虚假通过。</summary>
        private static double Divide(int numerator, int denominator)
        {
            return denominator <= 0 ? 0D : (double)numerator / denominator;
        }

        /// <summary>XMZADD 20260916 生成架构和对象的大小写不敏感索引键。</summary>
        private static string MakeObjectKey(string schemaName, string objectName)
        {
            return (string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName) + "\u001F" +
                   (objectName ?? string.Empty);
        }

        /// <summary>XMZADD 20260916 生成表字段实际使用索引键。</summary>
        private static string MakeFieldKey(string objectName, string fieldName)
        {
            return (objectName ?? string.Empty) + "\u001F" + (fieldName ?? string.Empty);
        }

        /// <summary>XMZADD 20260916 生成候选关系的方向稳定键。</summary>
        private static string MakeRelationKey(RelationMetadata relation)
        {
            return relation == null ? string.Empty :
                MakeRelationKey(relation.ParentSchemaName, relation.ParentTableName,
                    relation.ParentFieldName, relation.ChildSchemaName,
                    relation.ChildTableName, relation.ChildFieldName);
        }

        /// <summary>XMZADD 20260916 生成金标关系的方向稳定键。</summary>
        private static string MakeRelationKey(DictionaryV6GoldEntry entry)
        {
            return entry == null ? string.Empty :
                MakeRelationKey(entry.ParentSchemaName, entry.ParentTableName,
                    entry.ParentFieldName, entry.ChildSchemaName,
                    entry.ChildTableName, entry.ChildFieldName);
        }

        /// <summary>XMZADD 20260916 拼接父子架构、表和字段以区分反向关系。</summary>
        private static string MakeRelationKey(string parentSchema, string parentTable,
            string parentField, string childSchema, string childTable, string childField)
        {
            return (string.IsNullOrWhiteSpace(parentSchema) ? "dbo" : parentSchema) + "\u001F" +
                   (parentTable ?? string.Empty) + "\u001F" + (parentField ?? string.Empty) + "\u001F" +
                   (string.IsNullOrWhiteSpace(childSchema) ? "dbo" : childSchema) + "\u001F" +
                   (childTable ?? string.Empty) + "\u001F" + (childField ?? string.Empty);
        }

        /// <summary>XMZADD 20260916 拒绝金标证据中的驱动器路径和 UNC 路径，保证仓库内容可公开复现。</summary>
        private static bool ContainsAbsolutePath(string source)
        {
            string value = source ?? string.Empty;
            if (value.StartsWith("\\\\", StringComparison.Ordinal) ||
                value.StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }
            return value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
                   (value[2] == '\\' || value[2] == '/');
        }
    }

    /// <summary>XMZADD 20260916 保存质量门禁所需的准确率、缺口、冲突、实体覆盖和金标规模指标。</summary>
    public sealed class DictionaryV6QualityInput
    {
        public double OfficialNameAccuracy { get; set; }
        public double SuggestedNameAccuracy { get; set; }
        public double RelationDirectionAccuracy { get; set; }
        public int UsedFieldGapCount { get; set; }
        public int PseudoChineseOfficialCount { get; set; }
        public int OfficialConflictCount { get; set; }
        public int EntityTableCount { get; set; }
        public int AuditedEntityTableCount { get; set; }
        public int GoldTableCount { get; set; }
        public int GoldFieldCount { get; set; }
        public int GoldRelationCount { get; set; }
    }

    /// <summary>XMZADD 20260916 返回候选能否提升、全部失败原因及可导出的逐项审计结果。</summary>
    public sealed class DictionaryV6QualityResult
    {
        /// <summary>XMZADD 20260916 初始化可追加的失败原因和专项审计集合。</summary>
        public DictionaryV6QualityResult()
        {
            Failures = new List<string>();
            GoldEvaluations = new List<DictionaryV6GoldEvaluation>();
            UsedFieldGaps = new List<DictionaryV6NameAuditRecord>();
            PseudoChineseOfficials = new List<DictionaryV6NameAuditRecord>();
            OfficialConflicts = new List<DictionaryV6NameAuditRecord>();
        }

        public bool CanPromote { get; set; }
        public double OfficialNameAccuracy { get; set; }
        public double SuggestedNameAccuracy { get; set; }
        public double RelationDirectionAccuracy { get; set; }
        public int UsedFieldGapCount { get; set; }
        public int PseudoChineseOfficialCount { get; set; }
        public int OfficialConflictCount { get; set; }
        public int EntityTableCount { get; set; }
        public int AuditedEntityTableCount { get; set; }
        public int GoldTableCount { get; set; }
        public int GoldFieldCount { get; set; }
        public int GoldRelationCount { get; set; }
        public IList<string> Failures { get; set; }
        public IList<DictionaryV6GoldEvaluation> GoldEvaluations { get; set; }
        public IList<DictionaryV6NameAuditRecord> UsedFieldGaps { get; set; }
        public IList<DictionaryV6NameAuditRecord> PseudoChineseOfficials { get; set; }
        public IList<DictionaryV6NameAuditRecord> OfficialConflicts { get; set; }
    }

    /// <summary>XMZADD 20260916 表示一个版本化、可追溯且不依赖本轮自动翻译的 V6 金标准。</summary>
    [DataContract]
    public sealed class DictionaryV6GoldStandard
    {
        [DataMember(Name = "version")]
        public string Version { get; set; }

        [DataMember(Name = "baselineRevision")]
        public long BaselineRevision { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "entries")]
        public IList<DictionaryV6GoldEntry> Entries { get; set; }
    }

    /// <summary>XMZADD 20260916 保存单个表、字段或有方向关系的金标期望与来源。</summary>
    [DataContract]
    public sealed class DictionaryV6GoldEntry
    {
        [DataMember(Name = "kind")]
        public string Kind { get; set; }

        [DataMember(Name = "stableKey")]
        public string StableKey { get; set; }

        [DataMember(Name = "scopeKey")]
        public string ScopeKey { get; set; }

        [DataMember(Name = "schemaName")]
        public string SchemaName { get; set; }

        [DataMember(Name = "objectName")]
        public string ObjectName { get; set; }

        [DataMember(Name = "fieldName")]
        public string FieldName { get; set; }

        [DataMember(Name = "parentSchemaName")]
        public string ParentSchemaName { get; set; }

        [DataMember(Name = "parentTableName")]
        public string ParentTableName { get; set; }

        [DataMember(Name = "parentFieldName")]
        public string ParentFieldName { get; set; }

        [DataMember(Name = "childSchemaName")]
        public string ChildSchemaName { get; set; }

        [DataMember(Name = "childTableName")]
        public string ChildTableName { get; set; }

        [DataMember(Name = "childFieldName")]
        public string ChildFieldName { get; set; }

        [DataMember(Name = "expectedChineseName")]
        public string ExpectedChineseName { get; set; }

        [DataMember(Name = "expectedDisposition")]
        public string ExpectedDisposition { get; set; }

        [DataMember(Name = "moduleName")]
        public string ModuleName { get; set; }

        [DataMember(Name = "evidenceType")]
        public string EvidenceType { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }
    }

    /// <summary>XMZADD 20260916 保存金标期望与候选正式名、参考名或关系方向的逐项对照。</summary>
    public sealed class DictionaryV6GoldEvaluation
    {
        public string Kind { get; set; }
        public string StableKey { get; set; }
        public string ExpectedChineseName { get; set; }
        public string OfficialChineseName { get; set; }
        public string SuggestedChineseName { get; set; }
        public bool OfficialMatch { get; set; }
        public bool SuggestedMatch { get; set; }
        public bool RelationMatch { get; set; }
        public string EvidenceType { get; set; }
        public string Source { get; set; }
    }

    /// <summary>XMZADD 20260916 保存名称缺口、伪中文或冲突对象的模块、状态和证据位置。</summary>
    public sealed class DictionaryV6NameAuditRecord
    {
        public string ObjectType { get; set; }
        public string SchemaName { get; set; }
        public string ObjectName { get; set; }
        public string FieldName { get; set; }
        public string ModuleName { get; set; }
        public string OfficialChineseName { get; set; }
        public string SuggestedChineseName { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
        public string RuleName { get; set; }
        public string SourcePath { get; set; }
        public int SourceLine { get; set; }
    }
}
