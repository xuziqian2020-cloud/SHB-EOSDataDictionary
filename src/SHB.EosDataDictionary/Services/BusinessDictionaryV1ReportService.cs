using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260903 统计并导出第一版业务字典的覆盖率、分类和关系审计结果。</summary>
    public sealed class BusinessDictionaryV1ReportService
    {
        private static readonly string[] ActualUsageRules =
        {
            "GridColumnCaption",
            "SqlFieldRelation",
            "EntityFieldAssignment",
            "DynamicTableFieldUsage",
            "SqlFieldUsage"
        };
        private static readonly HashSet<string> AllowedEnglishIdentifiers = new HashSet<string>(
            new[] { "ID", "GUID", "ERP", "EOS", "KIS", "K3", "SQL", "IP", "MAC", "URL" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly Regex IdentifierTokenRegex = new Regex(
            @"[A-Z]+(?=[A-Z][a-z]|\d|$)|[A-Z]?[a-z]+|\d+",
            RegexOptions.Compiled);

        /// <summary>XMZADD 20260903 单次遍历快照计算第一版可验收的核心覆盖指标。</summary>
        public BusinessDictionaryV1Report Create(SnapshotData snapshot)
        {
            return Create(snapshot, null);
        }

        /// <summary>XMZADD 20260903 结合源码对象关系证据计算未落地关系数量，避免只报告成功结果。</summary>
        public BusinessDictionaryV1Report Create(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence)
        {
            return Create(snapshot, sourceEvidence, BuildActualUsageIndex(sourceEvidence));
        }

        /// <summary>XMZADD 20260907 复用已建立的源码用途索引统计 V4 指标，避免重复遍历大规模证据集合。</summary>
        private static BusinessDictionaryV1Report Create(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence,
            IDictionary<string, ActualFieldUsage> actualUsageIndex)
        {
            var report = new BusinessDictionaryV1Report();
            if (snapshot == null || snapshot.Tables == null)
            {
                return report;
            }
            var relationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                report.TotalTableCount++;
                CountTable(report, table);
                CountFields(report, table, actualUsageIndex);
                CountRelations(report, table, relationKeys);
            }
            CountObjectRelationEvidence(report, snapshot, sourceEvidence);
            return report;
        }

        /// <summary>XMZADD 20260903 写出 Markdown 汇总及核心表、排除表和关联关系 CSV，便于人工抽查。</summary>
        public void Write(string reportRoot, SnapshotData snapshot, BusinessDictionaryV1Report report)
        {
            Write(reportRoot, snapshot, report, null);
        }

        /// <summary>XMZADD 20260903 写出包含核心字段和未落地代码关系的完整第一版审计文件。</summary>
        public void Write(string reportRoot, SnapshotData snapshot, BusinessDictionaryV1Report report,
            IList<SourceEvidence> sourceEvidence)
        {
            Write(reportRoot, snapshot, report, sourceEvidence, null);
        }

        /// <summary>XMZADD 20260916 写出原有业务报告及 V6 发布门禁所需的十份稳定审计文件。</summary>
        public void Write(string reportRoot, SnapshotData snapshot, BusinessDictionaryV1Report report,
            IList<SourceEvidence> sourceEvidence, DictionaryV6QualityResult qualityResult)
        {
            if (string.IsNullOrWhiteSpace(reportRoot))
            {
                throw new ArgumentException("报告目录不能为空。", "reportRoot");
            }
            Directory.CreateDirectory(reportRoot);
            IDictionary<string, ActualFieldUsage> actualUsageIndex = BuildActualUsageIndex(sourceEvidence);
            BusinessDictionaryV1Report effectiveReport = report ?? Create(snapshot, sourceEvidence, actualUsageIndex);
            File.WriteAllText(Path.Combine(reportRoot, "推断运行报告.md"), CreateMarkdown(effectiveReport), Encoding.UTF8);
            File.WriteAllText(Path.Combine(reportRoot, "核心业务表.csv"), CreateBusinessTableCsv(snapshot), Encoding.UTF8);
            File.WriteAllText(Path.Combine(reportRoot, "核心业务字段.csv"), CreateBusinessFieldCsv(snapshot), Encoding.UTF8);
            File.WriteAllText(Path.Combine(reportRoot, "低价值表排除清单.csv"), CreateExcludedTableCsv(snapshot), Encoding.UTF8);
            File.WriteAllText(Path.Combine(reportRoot, "关联关系清单.csv"), CreateRelationCsv(snapshot), Encoding.UTF8);
            File.WriteAllText(Path.Combine(reportRoot, "未落地代码关系列表.csv"),
                CreateUnresolvedRelationCsv(snapshot, sourceEvidence), Encoding.UTF8);
            File.WriteAllText(Path.Combine(reportRoot, "待复核中文名称.csv"),
                CreateChineseNameReviewCsv(snapshot), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(reportRoot, "中文名称变更审计.csv"),
                CreateChineseNameChangeAuditCsv(snapshot), new UTF8Encoding(true));
            IList<ActualFieldRecord> reviewRecords = BuildActualFieldReviewRecords(snapshot, actualUsageIndex);
            File.WriteAllText(Path.Combine(reportRoot, "实际使用字段待复核.csv"),
                CreateActualFieldReviewCsv(reviewRecords), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(reportRoot, "未知缩写频率.csv"),
                CreateUnknownAbbreviationFrequencyCsv(reviewRecords), new UTF8Encoding(true));
            if (qualityResult != null)
            {
                WriteV6QualityReports(reportRoot, snapshot, qualityResult);
            }
        }

        /// <summary>XMZADD 20260903 统计表级实体、知识库来源及业务分类。</summary>
        private static void CountTable(BusinessDictionaryV1Report report, TableMetadata table)
        {
            if (table.EntityName != null && !string.IsNullOrWhiteSpace(table.EntityName.Value)) report.EntityTableCount++;
            if ((table.ChineseName != null && table.ChineseName.Status == ConfidenceStatus.KnowledgeBaseEvidence) ||
                (table.ModuleName != null && table.ModuleName.Status == ConfidenceStatus.KnowledgeBaseEvidence)) report.KnowledgeTableCount++;
            if (table.Category == DictionaryTableCategory.Business || table.Category == DictionaryTableCategory.BaseData) report.BusinessTableCount++;
            if (table.Category == DictionaryTableCategory.Technical) report.TechnicalTableCount++;
            if (table.Category == DictionaryTableCategory.Excluded) report.ExcludedTableCount++;
            if (table.Category == DictionaryTableCategory.Unclassified) report.UnclassifiedTableCount++;
            if (IsLowConfidence(table.ChineseName))
            {
                report.PendingTableNameCount++;
                report.LowConfidenceTableNameCount++;
            }
        }

        /// <summary>XMZADD 20260903 统计字段中文名和业务枚举覆盖情况。</summary>
        private static void CountFields(BusinessDictionaryV1Report report, TableMetadata table,
            IDictionary<string, ActualFieldUsage> actualUsageIndex)
        {
            if (table.Fields == null)
            {
                return;
            }
            for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
            {
                FieldMetadata field = table.Fields[fieldIndex];
                if (field == null)
                {
                    continue;
                }
                report.TotalFieldCount++;
                if (field.ChineseName != null && !string.IsNullOrWhiteSpace(field.ChineseName.Value)) report.NamedFieldCount++;
                if (!IsLowConfidence(field.ChineseName)) report.HighConfidenceFieldNameCount++;
                if (field.EnumItems != null && field.EnumItems.Count > 0) report.EnumFieldCount++;
                if (IsRefinedName(field.ChineseName)) report.V4RefinedNameCount++;
                if (field.ChineseName != null && field.ChineseName.Status == ConfidenceStatus.GuessedConflict)
                {
                    report.AmbiguousConflictCount++;
                }

                ActualFieldUsage usage;
                if (actualUsageIndex == null ||
                    !actualUsageIndex.TryGetValue(MakeFieldKey(table.ObjectName, field.FieldName), out usage))
                {
                    continue;
                }
                report.ActualUsedFieldCount++;
                if (RequiresActualFieldReview(field.ChineseName))
                {
                    report.ActualUsedReviewFieldCount++;
                }
                else
                {
                    report.ActualUsedNamedFieldCount++;
                }
            }
        }

        /// <summary>XMZADD 20260903 按关系端点去重统计物理关系和代码推断关系。</summary>
        private static void CountRelations(BusinessDictionaryV1Report report, TableMetadata table, ISet<string> relationKeys)
        {
            if (table.Relations == null)
            {
                return;
            }
            for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
            {
                RelationMetadata relation = table.Relations[relationIndex];
                string key = MakeRelationKey(relation);
                if (relation == null || !relationKeys.Add(key))
                {
                    continue;
                }
                report.TotalRelationCount++;
                if (relation.RelationType != null && relation.RelationType.Status == ConfidenceStatus.DatabaseEvidence) report.PhysicalRelationCount++;
                if (relation.RelationType != null && relation.RelationType.Status == ConfidenceStatus.CodeEvidence) report.CodeRelationCount++;
            }
        }

        /// <summary>XMZADD 20260903 对源码 obj 关系按业务键去重，并统计未形成代码关系的证据。</summary>
        private static void CountObjectRelationEvidence(BusinessDictionaryV1Report report, SnapshotData snapshot,
            IList<SourceEvidence> sourceEvidence)
        {
            ISet<string> resolvedRelations = BuildResolvedCodeRelationSet(snapshot);
            var evidenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sourceEvidence == null)
            {
                return;
            }
            for (int index = 0; index < sourceEvidence.Count; index++)
            {
                SourceEvidence evidence = sourceEvidence[index];
                if (!IsObjectRelationEvidence(evidence))
                {
                    continue;
                }
                string evidenceKey = MakeObjectRelationEvidenceKey(evidence);
                if (!evidenceKeys.Add(evidenceKey))
                {
                    continue;
                }
                report.ObjectRelationEvidenceCount++;
                if (!resolvedRelations.Contains(MakeChildRelationKey(evidence.ObjectName, evidence.RelationFieldName)))
                {
                    report.UnresolvedObjectRelationCount++;
                }
            }
        }

        /// <summary>XMZADD 20260903 建立已生成代码关系的子表字段索引，供未落地证据审计复用。</summary>
        private static ISet<string> BuildResolvedCodeRelationSet(SnapshotData snapshot)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var relationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (snapshot == null || snapshot.Tables == null)
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
                    if (relation == null || !relationKeys.Add(MakeRelationKey(relation)) ||
                        relation.RelationType == null || relation.RelationType.Status != ConfidenceStatus.CodeEvidence)
                    {
                        continue;
                    }
                    result.Add(MakeChildRelationKey(relation.ChildTableName, relation.ChildFieldName));
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260903 生成便于版本间对比的固定指标 Markdown。</summary>
        private static string CreateMarkdown(BusinessDictionaryV1Report report)
        {
            var text = new StringBuilder();
            text.AppendLine("# EOS 业务数据字典 V4 推断报告");
            text.AppendLine();
            text.AppendLine("| 指标 | 数量 |");
            text.AppendLine("|---|---:|");
            AppendMetric(text, "物理表总数", report.TotalTableCount);
            AppendMetric(text, "字段总数", report.TotalFieldCount);
            AppendMetric(text, "业务及基础资料表", report.BusinessTableCount);
            AppendMetric(text, "实体映射表", report.EntityTableCount);
            AppendMetric(text, "知识库命中表", report.KnowledgeTableCount);
            AppendMetric(text, "有显示名称字段（含推测）", report.NamedFieldCount);
            AppendMetric(text, "高置信度中文字段", report.HighConfidenceFieldNameCount);
            AppendMetric(text, "低置信度表名", report.LowConfidenceTableNameCount);
            AppendMetric(text, "含业务枚举字段", report.EnumFieldCount);
            AppendMetric(text, "关系总数", report.TotalRelationCount);
            AppendMetric(text, "物理外键关系", report.PhysicalRelationCount);
            AppendMetric(text, "代码对象关系", report.CodeRelationCount);
            AppendMetric(text, "代码对象关系证据", report.ObjectRelationEvidenceCount);
            AppendMetric(text, "未落地代码关系", report.UnresolvedObjectRelationCount);
            AppendMetric(text, "技术表", report.TechnicalTableCount);
            AppendMetric(text, "排除表", report.ExcludedTableCount);
            AppendMetric(text, "未分类表", report.UnclassifiedTableCount);
            AppendMetric(text, "待确认表名", report.PendingTableNameCount);
            AppendMetric(text, "实际使用字段", report.ActualUsedFieldCount);
            AppendMetric(text, "实际使用字段已命名", report.ActualUsedNamedFieldCount);
            AppendMetric(text, "V4 细化名称", report.V4RefinedNameCount);
            AppendMetric(text, "实际使用字段待复核", report.ActualUsedReviewFieldCount);
            AppendMetric(text, "多义冲突", report.AmbiguousConflictCount);
            return text.ToString();
        }

        /// <summary>XMZADD 20260903 追加单个报告指标并使用固定区域格式。</summary>
        private static void AppendMetric(StringBuilder text, string name, int value)
        {
            text.Append("| ").Append(name).Append(" | ")
                .Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        }

        /// <summary>XMZADD 20260903 导出核心业务表的中文名、模块、实体和置信状态。</summary>
        private static string CreateBusinessTableCsv(SnapshotData snapshot)
        {
            var text = new StringBuilder("SchemaName,ObjectName,ChineseName,ModuleName,EntityName,Category,Confidence\r\n");
            if (snapshot == null || snapshot.Tables == null)
            {
                return text.ToString();
            }
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                if (table == null || (table.Category != DictionaryTableCategory.Business && table.Category != DictionaryTableCategory.BaseData))
                {
                    continue;
                }
                AppendCsvRow(text, table.SchemaName, table.ObjectName, GetValue(table.ChineseName),
                    GetValue(table.ModuleName), GetValue(table.EntityName), table.Category.ToString(),
                    table.ChineseName == null ? string.Empty : table.ChineseName.Status.ToString());
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260903 导出核心业务表字段的中文名、实体属性、枚举、关系和首条证据。</summary>
        private static string CreateBusinessFieldCsv(SnapshotData snapshot)
        {
            var text = new StringBuilder("SchemaName,ObjectName,FieldName,ChineseName,Confidence,EntityProperty,EnumItems,RelationSummary,SourcePath,SourceLine\r\n");
            if (snapshot == null || snapshot.Tables == null)
            {
                return text.ToString();
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Fields == null ||
                    (table.Category != DictionaryTableCategory.Business && table.Category != DictionaryTableCategory.BaseData))
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
                    EvidenceItem evidence = GetFirstEvidence(field.ChineseName) ?? GetFirstEvidence(field.EntityPropertyName);
                    AppendCsvRow(text, table.SchemaName, table.ObjectName, field.FieldName,
                        GetValue(field.ChineseName), field.ChineseName == null ? string.Empty : field.ChineseName.Status.ToString(),
                        GetValue(field.EntityPropertyName), CreateEnumSummary(field), GetValue(field.RelationSummary),
                        evidence == null ? string.Empty : evidence.SourcePath,
                        evidence == null ? string.Empty : evidence.SourceLine.ToString(CultureInfo.InvariantCulture));
                }
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260903 导出尚未匹配目标实体、字段或主键的代码对象关系，供下一版继续补全。</summary>
        private static string CreateUnresolvedRelationCsv(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence)
        {
            var text = new StringBuilder("ObjectName,RelationField,TargetEntity,SourcePath,SourceLine\r\n");
            ISet<string> resolvedRelations = BuildResolvedCodeRelationSet(snapshot);
            var evidenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sourceEvidence == null)
            {
                return text.ToString();
            }
            for (int index = 0; index < sourceEvidence.Count; index++)
            {
                SourceEvidence evidence = sourceEvidence[index];
                if (!IsObjectRelationEvidence(evidence) || !evidenceKeys.Add(MakeObjectRelationEvidenceKey(evidence)) ||
                    resolvedRelations.Contains(MakeChildRelationKey(evidence.ObjectName, evidence.RelationFieldName)))
                {
                    continue;
                }
                AppendCsvRow(text, evidence.ObjectName, evidence.RelationFieldName, evidence.RelationTargetEntity,
                    evidence.Evidence.SourcePath,
                    evidence.Evidence.SourceLine.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260903 将字段枚举压缩成值等于中文名的审计文本。</summary>
        private static string CreateEnumSummary(FieldMetadata field)
        {
            if (field == null || field.EnumItems == null || field.EnumItems.Count == 0)
            {
                return string.Empty;
            }
            var text = new StringBuilder();
            for (int index = 0; index < field.EnumItems.Count; index++)
            {
                EnumItemMetadata item = field.EnumItems[index];
                if (item == null)
                {
                    continue;
                }
                if (text.Length > 0) text.Append("；");
                text.Append(item.Value).Append("=").Append(GetValue(item.ChineseName));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260903 导出明确排除表及其分类依据。</summary>
        private static string CreateExcludedTableCsv(SnapshotData snapshot)
        {
            var text = new StringBuilder("SchemaName,ObjectName,Reason,RuleName\r\n");
            if (snapshot == null || snapshot.Tables == null)
            {
                return text.ToString();
            }
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                if (table == null || table.Category != DictionaryTableCategory.Excluded)
                {
                    continue;
                }
                AppendCsvRow(text, table.SchemaName, table.ObjectName, GetValue(table.Remark), GetFirstRule(table.Remark));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260903 导出去重后的关系端点、证据状态和首个来源位置。</summary>
        private static string CreateRelationCsv(SnapshotData snapshot)
        {
            var text = new StringBuilder("ParentTable,ParentField,ChildTable,ChildField,Status,SourcePath,SourceLine\r\n");
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (snapshot == null || snapshot.Tables == null)
            {
                return text.ToString();
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
                    if (relation == null || !keys.Add(MakeRelationKey(relation)))
                    {
                        continue;
                    }
                    EvidenceItem evidence = GetFirstEvidence(relation.RelationType);
                    AppendCsvRow(text, relation.ParentTableName, relation.ParentFieldName,
                        relation.ChildTableName, relation.ChildFieldName,
                        relation.RelationType == null ? string.Empty : relation.RelationType.Status.ToString(),
                        evidence == null ? string.Empty : evidence.SourcePath,
                        evidence == null ? string.Empty : evidence.SourceLine.ToString(CultureInfo.InvariantCulture));
                }
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260904 导出业务表和基础资料表中缺少可靠中文、混入未知英文或存在冲突的名称。</summary>
        private static string CreateChineseNameReviewCsv(SnapshotData snapshot)
        {
            var text = new StringBuilder("ObjectType,ObjectName,FieldName,ChineseName,Status,RuleName,SourcePath,SourceLine\r\n");
            if (snapshot == null || snapshot.Tables == null)
            {
                return text.ToString();
            }

            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null ||
                    (table.Category != DictionaryTableCategory.Business &&
                     table.Category != DictionaryTableCategory.BaseData))
                {
                    continue;
                }

                if (RequiresChineseNameReview(table.ChineseName))
                {
                    AppendChineseNameReviewRow(text, "Table", table.ObjectName, null, table.ChineseName);
                }
                if (table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field != null && RequiresChineseNameReview(field.ChineseName))
                    {
                        AppendChineseNameReviewRow(text, "Field", table.ObjectName, field.FieldName, field.ChineseName);
                    }
                }
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260904 判断显示名称是否仍缺少可靠中文或包含未翻译业务英文。</summary>
        private static bool RequiresChineseNameReview(MetadataValue value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.Value) ||
                string.Equals(value.Value, "暂无可靠中文名称", StringComparison.Ordinal) ||
                value.Status == ConfidenceStatus.PendingConfirmation ||
                value.Status == ConfidenceStatus.GuessedConflict)
            {
                return true;
            }

            // 行业标识可保留英文，其他连续英文片段代表尚未解释的业务语义。
            MatchCollection englishIdentifiers = Regex.Matches(value.Value, "[A-Za-z][A-Za-z0-9]*");
            for (int index = 0; index < englishIdentifiers.Count; index++)
            {
                if (!IsAllowedEnglishIdentifier(englishIdentifiers[index].Value))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260907 统一判断可在中文名称和未知缩写报告中保留的行业英文标识，避免两套白名单漂移。</summary>
        private static bool IsAllowedEnglishIdentifier(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && AllowedEnglishIdentifiers.Contains(value);
        }

        /// <summary>XMZADD 20260907 从普通业务源码证据建立大小写不敏感的实际使用字段索引，并按业务价值保留最强定位证据。</summary>
        private static IDictionary<string, ActualFieldUsage> BuildActualUsageIndex(IList<SourceEvidence> sourceEvidence)
        {
            var result = new Dictionary<string, ActualFieldUsage>(StringComparer.OrdinalIgnoreCase);
            if (sourceEvidence == null)
            {
                return result;
            }

            for (int index = 0; index < sourceEvidence.Count; index++)
            {
                SourceEvidence source = sourceEvidence[index];
                if (source == null || source.Evidence == null ||
                    string.IsNullOrWhiteSpace(source.ObjectName) || string.IsNullOrWhiteSpace(source.FieldName) ||
                    !IsActualUsageRule(source.Evidence.RuleName))
                {
                    continue;
                }

                string key = MakeFieldKey(source.ObjectName, source.FieldName);
                ActualFieldUsage usage;
                if (!result.TryGetValue(key, out usage))
                {
                    usage = new ActualFieldUsage(source.ObjectName, source.FieldName);
                    result.Add(key, usage);
                }
                usage.UsageKinds.Add(source.Evidence.RuleName);
                if (usage.StrongestEvidence == null ||
                    CompareEvidenceStrength(source.Evidence, usage.StrongestEvidence.Evidence) < 0)
                {
                    usage.StrongestEvidence = source;
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260907 判断证据是否能证明字段被普通业务代码真实读取、写入、关联或界面绑定。</summary>
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

        /// <summary>XMZADD 20260907 按界面直绑、SQL 关系、实体赋值、动态字段和普通 SQL 使用的顺序比较证据业务强度。</summary>
        private static int CompareEvidenceStrength(EvidenceItem left, EvidenceItem right)
        {
            int comparison = GetEvidenceRank(left == null ? null : left.RuleName)
                .CompareTo(GetEvidenceRank(right == null ? null : right.RuleName));
            if (comparison != 0)
            {
                return comparison;
            }

            string leftPath = left == null ? string.Empty : left.SourcePath ?? string.Empty;
            string rightPath = right == null ? string.Empty : right.SourcePath ?? string.Empty;
            if (leftPath.Length == 0 && rightPath.Length > 0) return 1;
            if (rightPath.Length == 0 && leftPath.Length > 0) return -1;
            comparison = StringComparer.OrdinalIgnoreCase.Compare(leftPath, rightPath);
            if (comparison != 0)
            {
                return comparison;
            }

            int leftLine = left == null || left.SourceLine <= 0 ? int.MaxValue : left.SourceLine;
            int rightLine = right == null || right.SourceLine <= 0 ? int.MaxValue : right.SourceLine;
            return leftLine.CompareTo(rightLine);
        }

        /// <summary>XMZADD 20260907 返回源码和名称证据的稳定优先级，保证报告每次选中相同来源位置。</summary>
        private static int GetEvidenceRank(string ruleName)
        {
            for (int index = 0; index < ActualUsageRules.Length; index++)
            {
                if (string.Equals(ActualUsageRules[index], ruleName, StringComparison.Ordinal))
                {
                    return index;
                }
            }
            if (string.Equals(ruleName, "ExactBusinessPhrase", StringComparison.Ordinal)) return 10;
            if (string.Equals(ruleName, "EntityProperty", StringComparison.Ordinal)) return 20;
            return 100;
        }

        /// <summary>XMZADD 20260907 识别旧自动名称确实被当前自动结论细化的字段，排除人工名称和同值证据晋升。</summary>
        private static bool IsRefinedName(MetadataValue value)
        {
            return value != null && !value.IsManualOverride && !value.IsLocked &&
                   value.Status != ConfidenceStatus.LocalOverride &&
                   !string.IsNullOrWhiteSpace(value.OriginalAutomaticValue) &&
                   !string.IsNullOrWhiteSpace(value.Value) &&
                   !string.Equals(value.OriginalAutomaticValue.Trim(), value.Value.Trim(), StringComparison.Ordinal) &&
                   value.Evidence != null && value.Evidence.Count > 0;
        }

        /// <summary>XMZADD 20260907 判断实际使用字段是否仍缺少可供新人可靠理解的中文业务名称。</summary>
        private static bool RequiresActualFieldReview(MetadataValue value)
        {
            return IsLowConfidence(value) || RequiresChineseNameReview(value);
        }

        /// <summary>XMZADD 20260907 导出自动中文名称的真实细化记录，并使用当前结论中最强的可定位证据。</summary>
        private static string CreateChineseNameChangeAuditCsv(SnapshotData snapshot)
        {
            var text = new StringBuilder("ObjectName,FieldName,OldChineseName,NewChineseName,ConfidenceScore,RuleName,Explanation,SourcePath,SourceLine\r\n");
            var records = new List<NameChangeRecord>();
            if (snapshot != null && snapshot.Tables != null)
            {
                for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
                {
                    TableMetadata table = snapshot.Tables[tableIndex];
                    if (table == null || table.Fields == null)
                    {
                        continue;
                    }
                    for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                    {
                        FieldMetadata field = table.Fields[fieldIndex];
                        if (field == null || !IsRefinedName(field.ChineseName))
                        {
                            continue;
                        }
                        records.Add(new NameChangeRecord(table, field,
                            GetStrongestEvidence(field.ChineseName.Evidence)));
                    }
                }
            }
            records.Sort(CompareNameChangeRecords);

            for (int index = 0; index < records.Count; index++)
            {
                NameChangeRecord record = records[index];
                EvidenceItem evidence = record.Evidence;
                AppendCsvRow(text,
                    record.Table.ObjectName,
                    record.Field.FieldName,
                    record.Field.ChineseName.OriginalAutomaticValue,
                    record.Field.ChineseName.Value,
                    record.Field.ChineseName.ConfidenceScore.ToString(CultureInfo.InvariantCulture),
                    evidence == null ? string.Empty : evidence.RuleName,
                    evidence == null ? string.Empty : evidence.Explanation,
                    evidence == null ? string.Empty : evidence.SourcePath,
                    evidence == null ? string.Empty : evidence.SourceLine.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260907 从当前名称证据中选择业务强度最高且来源位置稳定的一条。</summary>
        private static EvidenceItem GetStrongestEvidence(IList<EvidenceItem> evidence)
        {
            EvidenceItem strongest = null;
            if (evidence == null)
            {
                return null;
            }
            for (int index = 0; index < evidence.Count; index++)
            {
                EvidenceItem item = evidence[index];
                if (item != null && (strongest == null || CompareEvidenceStrength(item, strongest) < 0))
                {
                    strongest = item;
                }
            }
            return strongest;
        }

        /// <summary>XMZADD 20260907 建立实际使用但名称仍不可靠的字段记录，避免归档结构字段污染业务复核清单。</summary>
        private static IList<ActualFieldRecord> BuildActualFieldReviewRecords(SnapshotData snapshot,
            IDictionary<string, ActualFieldUsage> actualUsageIndex)
        {
            var result = new List<ActualFieldRecord>();
            if (snapshot == null || snapshot.Tables == null || actualUsageIndex == null)
            {
                return result;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    ActualFieldUsage usage;
                    if (field != null && RequiresActualFieldReview(field.ChineseName) &&
                        actualUsageIndex.TryGetValue(MakeFieldKey(table.ObjectName, field.FieldName), out usage))
                    {
                        result.Add(new ActualFieldRecord(table, field, usage));
                    }
                }
            }
            result.Sort(CompareActualFieldRecords);
            return result;
        }

        /// <summary>XMZADD 20260907 导出实际业务代码使用但中文语义仍不可靠的字段、用途类型和最强来源。</summary>
        private static string CreateActualFieldReviewCsv(IList<ActualFieldRecord> records)
        {
            var text = new StringBuilder("ObjectName,FieldName,CurrentChineseName,UnknownTokens,UsageKinds,SourcePath,SourceLine,Reason\r\n");
            if (records == null)
            {
                return text.ToString();
            }
            for (int index = 0; index < records.Count; index++)
            {
                ActualFieldRecord record = records[index];
                IList<string> unknownTokens = GetUnknownTokens(record.Field.FieldName);
                EvidenceItem evidence = record.Usage.StrongestEvidence == null
                    ? null
                    : record.Usage.StrongestEvidence.Evidence;
                AppendCsvRow(text,
                    record.Table.ObjectName,
                    record.Field.FieldName,
                    GetValue(record.Field.ChineseName),
                    JoinValues(unknownTokens),
                    CreateUsageKindSummary(record.Usage),
                    evidence == null ? string.Empty : evidence.SourcePath,
                    evidence == null ? string.Empty : evidence.SourceLine.ToString(CultureInfo.InvariantCulture),
                    GetActualFieldReviewReason(record.Field.ChineseName, unknownTokens));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260907 汇总实际待复核字段中的未知业务缩写，并按字段去重而非按重复证据计数。</summary>
        private static string CreateUnknownAbbreviationFrequencyCsv(IList<ActualFieldRecord> records)
        {
            var text = new StringBuilder("Token,Count,ExampleObject,ModuleName,SourcePath,SourceLine\r\n");
            var frequencies = new Dictionary<string, UnknownTokenFrequency>(StringComparer.OrdinalIgnoreCase);
            if (records != null)
            {
                for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
                {
                    ActualFieldRecord record = records[recordIndex];
                    IList<string> tokens = GetUnknownTokens(record.Field.FieldName);
                    for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
                    {
                        string token = tokens[tokenIndex];
                        UnknownTokenFrequency frequency;
                        if (!frequencies.TryGetValue(token, out frequency))
                        {
                            frequency = new UnknownTokenFrequency(token, record);
                            frequencies.Add(token, frequency);
                        }
                        frequency.Count++;
                    }
                }
            }

            var rows = new List<UnknownTokenFrequency>(frequencies.Values);
            rows.Sort(CompareUnknownTokenFrequencies);
            for (int index = 0; index < rows.Count; index++)
            {
                UnknownTokenFrequency row = rows[index];
                EvidenceItem evidence = row.Example.Usage.StrongestEvidence == null
                    ? null
                    : row.Example.Usage.StrongestEvidence.Evidence;
                AppendCsvRow(text,
                    row.Token,
                    row.Count.ToString(CultureInfo.InvariantCulture),
                    row.Example.Table.ObjectName,
                    GetValue(row.Example.Table.ModuleName),
                    evidence == null ? string.Empty : evidence.SourcePath,
                    evidence == null ? string.Empty : evidence.SourceLine.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260907 从字段标识符提取翻译词典仍无法解释的分词，并排除允许保留的行业英文标识。</summary>
        private static IList<string> GetUnknownTokens(string fieldName)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var translator = new IdentifierTranslationService();
            string[] sections = Regex.Split(fieldName ?? string.Empty, "[^A-Za-z0-9]+");
            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                string section = sections[sectionIndex];
                if (string.IsNullOrWhiteSpace(section) || IsAllowedEnglishIdentifier(section))
                {
                    continue;
                }
                MatchCollection matches = IdentifierTokenRegex.Matches(section);
                for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
                {
                    string token = matches[matchIndex].Value;
                    if (Regex.IsMatch(token, "^\\d+$") || IsAllowedEnglishIdentifier(token))
                    {
                        continue;
                    }
                    IdentifierTranslationResult translation = translator.Translate(token);
                    if (translation.UnknownTokens.Count > 0)
                    {
                        string normalized = token.ToUpperInvariant();
                        if (seen.Add(normalized)) result.Add(normalized);
                    }
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>XMZADD 20260907 按固定业务强度顺序拼接同一字段的多种真实用途。</summary>
        private static string CreateUsageKindSummary(ActualFieldUsage usage)
        {
            var text = new StringBuilder();
            if (usage == null)
            {
                return string.Empty;
            }
            for (int index = 0; index < ActualUsageRules.Length; index++)
            {
                if (!usage.UsageKinds.Contains(ActualUsageRules[index]))
                {
                    continue;
                }
                if (text.Length > 0) text.Append(";");
                text.Append(ActualUsageRules[index]);
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260907 拼接已排序且已去重的未知分词，供复核人员直接筛选。</summary>
        private static string JoinValues(IList<string> values)
        {
            var text = new StringBuilder();
            if (values == null)
            {
                return string.Empty;
            }
            for (int index = 0; index < values.Count; index++)
            {
                if (text.Length > 0) text.Append(";");
                text.Append(values[index]);
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260907 生成实际使用字段进入专项复核的可读原因。</summary>
        private static string GetActualFieldReviewReason(MetadataValue value, IList<string> unknownTokens)
        {
            if (value != null && value.Status == ConfidenceStatus.GuessedConflict)
            {
                return "实际业务代码已使用，但字段语义存在多义冲突。";
            }
            if (unknownTokens != null && unknownTokens.Count > 0)
            {
                return "实际业务代码已使用，字段标识仍包含未知业务缩写。";
            }
            if (value == null || string.IsNullOrWhiteSpace(value.Value) ||
                string.Equals(value.Value, "暂无可靠中文名称", StringComparison.Ordinal))
            {
                return "实际业务代码已使用，但尚无可靠中文名称。";
            }
            return "实际业务代码已使用，但当前中文名称置信度不足。";
        }

        /// <summary>XMZADD 20260907 生成表字段大小写不敏感复合键，避免相同字段的多条证据重复统计。</summary>
        private static string MakeFieldKey(string objectName, string fieldName)
        {
            return (objectName ?? string.Empty) + "\u001F" + (fieldName ?? string.Empty);
        }

        /// <summary>XMZADD 20260907 按对象和字段英文名稳定排序名称变更记录。</summary>
        private static int CompareNameChangeRecords(NameChangeRecord left, NameChangeRecord right)
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Table.ObjectName, right.Table.ObjectName);
            return comparison != 0
                ? comparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.Field.FieldName, right.Field.FieldName);
        }

        /// <summary>XMZADD 20260907 按对象和字段英文名稳定排序实际待复核字段。</summary>
        private static int CompareActualFieldRecords(ActualFieldRecord left, ActualFieldRecord right)
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Table.ObjectName, right.Table.ObjectName);
            return comparison != 0
                ? comparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.Field.FieldName, right.Field.FieldName);
        }

        /// <summary>XMZADD 20260907 按出现字段数降序及缩写字母序输出未知缩写统计。</summary>
        private static int CompareUnknownTokenFrequencies(UnknownTokenFrequency left, UnknownTokenFrequency right)
        {
            int comparison = right.Count.CompareTo(left.Count);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Token, right.Token);
        }

        /// <summary>XMZADD 20260904 写入单条名称复核记录及其首个可定位证据。</summary>
        private static void AppendChineseNameReviewRow(StringBuilder text, string objectType,
            string objectName, string fieldName, MetadataValue value)
        {
            EvidenceItem evidence = GetFirstEvidence(value);
            AppendCsvRow(text,
                objectType,
                objectName,
                fieldName,
                GetValue(value),
                value == null ? string.Empty : value.Status.ToString(),
                evidence == null ? string.Empty : evidence.RuleName,
                evidence == null ? string.Empty : evidence.SourcePath,
                evidence == null ? string.Empty : evidence.SourceLine.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>XMZADD 20260916 写出机器门禁摘要与九份可供 Excel 复核的 V6 明细报告。</summary>
        private static void WriteV6QualityReports(string reportRoot, SnapshotData snapshot,
            DictionaryV6QualityResult quality)
        {
            File.WriteAllText(Path.Combine(reportRoot, "summary.json"),
                CreateV6SummaryJson(quality), new UTF8Encoding(false));
            WriteV6Csv(reportRoot, "official-name-coverage.csv",
                CreateNameCoverageCsv(snapshot, false));
            WriteV6Csv(reportRoot, "suggested-name-coverage.csv",
                CreateNameCoverageCsv(snapshot, true));
            WriteV6Csv(reportRoot, "used-field-gaps.csv",
                CreateQualityIssueCsv(quality.UsedFieldGaps));
            WriteV6Csv(reportRoot, "conflicts.csv",
                CreateQualityIssueCsv(quality.OfficialConflicts));
            WriteV6Csv(reportRoot, "pseudo-chinese.csv",
                CreateQualityIssueCsv(quality.PseudoChineseOfficials));
            WriteV6Csv(reportRoot, "module-attribution.csv",
                CreateModuleAttributionCsv(snapshot));
            WriteV6Csv(reportRoot, "relation-audit.csv",
                CreateV6RelationAuditCsv(snapshot));
            WriteV6Csv(reportRoot, "gold-evaluation.csv",
                CreateGoldEvaluationCsv(quality.GoldEvaluations));
            WriteV6Csv(reportRoot, "evidence-distribution.csv",
                CreateEvidenceDistributionCsv(snapshot));
        }

        /// <summary>XMZADD 20260916 使用固定属性顺序生成可由发布脚本直接读取的质量摘要。</summary>
        private static string CreateV6SummaryJson(DictionaryV6QualityResult quality)
        {
            var text = new StringBuilder();
            text.Append("{\n");
            text.Append("  \"CanPromote\": ").Append(quality.CanPromote ? "true" : "false").Append(",\n");
            AppendJsonNumber(text, "OfficialNameAccuracy", quality.OfficialNameAccuracy, true);
            AppendJsonNumber(text, "SuggestedNameAccuracy", quality.SuggestedNameAccuracy, true);
            AppendJsonNumber(text, "RelationDirectionAccuracy", quality.RelationDirectionAccuracy, true);
            AppendJsonInteger(text, "UsedFieldGapCount", quality.UsedFieldGapCount, true);
            AppendJsonInteger(text, "PseudoChineseOfficialCount", quality.PseudoChineseOfficialCount, true);
            AppendJsonInteger(text, "OfficialConflictCount", quality.OfficialConflictCount, true);
            AppendJsonInteger(text, "EntityTableCount", quality.EntityTableCount, true);
            AppendJsonInteger(text, "AuditedEntityTableCount", quality.AuditedEntityTableCount, true);
            AppendJsonInteger(text, "GoldTableCount", quality.GoldTableCount, true);
            AppendJsonInteger(text, "GoldFieldCount", quality.GoldFieldCount, true);
            AppendJsonInteger(text, "GoldRelationCount", quality.GoldRelationCount, true);
            text.Append("  \"Failures\": [");
            if (quality.Failures != null && quality.Failures.Count > 0) text.Append('\n');
            if (quality.Failures != null)
            {
                for (int index = 0; index < quality.Failures.Count; index++)
                {
                    text.Append("    \"").Append(EscapeJson(quality.Failures[index])).Append('"');
                    text.Append(index + 1 < quality.Failures.Count ? ",\n" : "\n");
                }
            }
            text.Append("  ]\n}");
            return text.ToString();
        }

        /// <summary>XMZADD 20260916 追加使用固定小数格式的 JSON 准确率属性。</summary>
        private static void AppendJsonNumber(StringBuilder text, string name, double value, bool comma)
        {
            text.Append("  \"").Append(name).Append("\": ")
                .Append(value.ToString("0.############", CultureInfo.InvariantCulture));
            text.Append(comma ? ",\n" : "\n");
        }

        /// <summary>XMZADD 20260916 追加使用不变区域格式的 JSON 整数属性。</summary>
        private static void AppendJsonInteger(StringBuilder text, string name, int value, bool comma)
        {
            text.Append("  \"").Append(name).Append("\": ")
                .Append(value.ToString(CultureInfo.InvariantCulture));
            text.Append(comma ? ",\n" : "\n");
        }

        /// <summary>XMZADD 20260916 转义摘要失败原因中的 JSON 控制字符。</summary>
        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\")
                .Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        /// <summary>XMZADD 20260916 统一以 UTF-8 BOM 写入 CSV，避免 Excel 错判中文编码。</summary>
        private static void WriteV6Csv(string reportRoot, string fileName, string content)
        {
            File.WriteAllText(Path.Combine(reportRoot, fileName), content, new UTF8Encoding(true));
        }

        /// <summary>XMZADD 20260916 导出正式名称或首选参考名称的状态、评分和首条来源。</summary>
        private static string CreateNameCoverageCsv(SnapshotData snapshot, bool suggested)
        {
            var rows = new List<V6NameCoverageRow>();
            if (snapshot != null && snapshot.Tables != null)
            {
                for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
                {
                    TableMetadata table = snapshot.Tables[tableIndex];
                    if (table == null) continue;
                    AddNameCoverageRow(rows, "Table", table, null,
                        suggested ? table.SuggestedChineseName : table.ChineseName);
                    if (table.Fields == null) continue;
                    for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                    {
                        FieldMetadata field = table.Fields[fieldIndex];
                        if (field != null)
                        {
                            AddNameCoverageRow(rows, "Field", table, field,
                                suggested ? field.SuggestedChineseName : field.ChineseName);
                        }
                    }
                }
            }
            rows.Sort(CompareNameCoverageRows);
            var text = new StringBuilder("ObjectType,SchemaName,ObjectName,FieldName,ModuleName,ChineseName,Status,ConfidenceScore,RuleName,SourcePath,SourceLine\r\n");
            for (int index = 0; index < rows.Count; index++)
            {
                V6NameCoverageRow row = rows[index];
                AppendCsvRow(text, row.ObjectType, row.SchemaName, row.ObjectName, row.FieldName,
                    row.ModuleName, row.ChineseName, row.Status,
                    row.ConfidenceScore.ToString(CultureInfo.InvariantCulture), row.RuleName,
                    row.SourcePath, row.SourceLine.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260916 把一个非空名称及首条证据加入覆盖明细。</summary>
        private static void AddNameCoverageRow(IList<V6NameCoverageRow> rows, string objectType,
            TableMetadata table, FieldMetadata field, MetadataValue value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.Value)) return;
            EvidenceItem evidence = GetFirstEvidence(value);
            rows.Add(new V6NameCoverageRow
            {
                ObjectType = objectType,
                SchemaName = table.SchemaName ?? string.Empty,
                ObjectName = table.ObjectName ?? string.Empty,
                FieldName = field == null ? string.Empty : field.FieldName ?? string.Empty,
                ModuleName = GetValue(table.ModuleName),
                ChineseName = value.Value,
                Status = value.Status.ToString(),
                ConfidenceScore = value.ConfidenceScore,
                RuleName = evidence == null ? string.Empty : evidence.RuleName ?? string.Empty,
                SourcePath = evidence == null ? string.Empty : evidence.SourcePath ?? string.Empty,
                SourceLine = evidence == null ? 0 : evidence.SourceLine
            });
        }

        /// <summary>XMZADD 20260916 导出实际使用缺口、正式冲突或伪中文的统一专项清单。</summary>
        private static string CreateQualityIssueCsv(IList<DictionaryV6NameAuditRecord> rows)
        {
            var ordered = new List<DictionaryV6NameAuditRecord>();
            if (rows != null)
            {
                for (int index = 0; index < rows.Count; index++)
                {
                    if (rows[index] != null) ordered.Add(rows[index]);
                }
            }
            ordered.Sort(CompareQualityIssueRows);
            var text = new StringBuilder("ObjectType,SchemaName,ObjectName,FieldName,ModuleName,OfficialChineseName,SuggestedChineseName,Status,Reason,RuleName,SourcePath,SourceLine\r\n");
            for (int index = 0; index < ordered.Count; index++)
            {
                DictionaryV6NameAuditRecord row = ordered[index];
                AppendCsvRow(text, row.ObjectType, row.SchemaName, row.ObjectName, row.FieldName,
                    row.ModuleName, row.OfficialChineseName, row.SuggestedChineseName,
                    row.Status, row.Reason, row.RuleName, row.SourcePath,
                    row.SourceLine.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260916 导出实体及已分类业务表的主模块、使用模块和名称覆盖。</summary>
        private static string CreateModuleAttributionCsv(SnapshotData snapshot)
        {
            var rows = new List<TableMetadata>();
            if (snapshot != null && snapshot.Tables != null)
            {
                for (int index = 0; index < snapshot.Tables.Count; index++)
                {
                    TableMetadata table = snapshot.Tables[index];
                    if (table != null && (table.Category == DictionaryTableCategory.Business ||
                        table.Category == DictionaryTableCategory.BaseData ||
                        !string.IsNullOrWhiteSpace(GetValue(table.EntityName))))
                    {
                        rows.Add(table);
                    }
                }
            }
            rows.Sort(CompareTablesForV6Report);
            var text = new StringBuilder("SchemaName,ObjectName,EntityName,Category,PrimaryModule,ModuleStatus,UsedByModules,OfficialChineseName,SuggestedChineseName,RuleName,SourcePath,SourceLine\r\n");
            for (int index = 0; index < rows.Count; index++)
            {
                TableMetadata table = rows[index];
                EvidenceItem evidence = GetFirstEvidence(table.ModuleName);
                AppendCsvRow(text, table.SchemaName, table.ObjectName, GetValue(table.EntityName),
                    table.Category.ToString(), GetValue(table.ModuleName),
                    table.ModuleName == null ? string.Empty : table.ModuleName.Status.ToString(),
                    JoinMetadataValues(table.UsedByModules), GetValue(table.ChineseName),
                    GetValue(table.SuggestedChineseName),
                    evidence == null ? string.Empty : evidence.RuleName,
                    evidence == null ? string.Empty : evidence.SourcePath,
                    evidence == null ? string.Empty : evidence.SourceLine.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260916 以竖线连接去重排序后的使用模块，避免数组顺序影响报告。</summary>
        private static string JoinMetadataValues(IList<MetadataValue> values)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values != null)
            {
                for (int index = 0; index < values.Count; index++)
                {
                    string name = GetValue(values[index]);
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) names.Add(name);
                }
            }
            names.Sort(StringComparer.Ordinal);
            return string.Join("|", names.ToArray());
        }

        /// <summary>XMZADD 20260916 按方向稳定键去重导出物理、代码和参考关系及来源。</summary>
        private static string CreateV6RelationAuditCsv(SnapshotData snapshot)
        {
            var rows = new Dictionary<string, RelationMetadata>(StringComparer.OrdinalIgnoreCase);
            if (snapshot != null && snapshot.Tables != null)
            {
                for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
                {
                    TableMetadata table = snapshot.Tables[tableIndex];
                    if (table == null || table.Relations == null) continue;
                    for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                    {
                        RelationMetadata relation = table.Relations[relationIndex];
                        string key = MakeRelationKey(relation);
                        if (relation != null && !rows.ContainsKey(key)) rows.Add(key, relation);
                    }
                }
            }
            var keys = new List<string>(rows.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            var text = new StringBuilder("ForeignKeyName,ParentSchema,ParentTable,ParentField,ChildSchema,ChildTable,ChildField,RelationType,Status,RuleName,SourcePath,SourceLine\r\n");
            for (int index = 0; index < keys.Count; index++)
            {
                RelationMetadata relation = rows[keys[index]];
                EvidenceItem evidence = GetFirstEvidence(relation.RelationType);
                AppendCsvRow(text, relation.ForeignKeyName, relation.ParentSchemaName,
                    relation.ParentTableName, relation.ParentFieldName, relation.ChildSchemaName,
                    relation.ChildTableName, relation.ChildFieldName, GetValue(relation.RelationType),
                    relation.RelationType == null ? string.Empty : relation.RelationType.Status.ToString(),
                    evidence == null ? string.Empty : evidence.RuleName,
                    evidence == null ? string.Empty : evidence.SourcePath,
                    evidence == null ? string.Empty : evidence.SourceLine.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260916 导出每条金标的期望、正式结论、参考结论和命中状态。</summary>
        private static string CreateGoldEvaluationCsv(IList<DictionaryV6GoldEvaluation> rows)
        {
            var ordered = new List<DictionaryV6GoldEvaluation>();
            if (rows != null)
            {
                for (int index = 0; index < rows.Count; index++)
                {
                    if (rows[index] != null) ordered.Add(rows[index]);
                }
            }
            ordered.Sort(CompareGoldEvaluationRows);
            var text = new StringBuilder("Kind,StableKey,ExpectedChineseName,OfficialChineseName,SuggestedChineseName,OfficialMatch,SuggestedMatch,RelationMatch,EvidenceType,Source\r\n");
            for (int index = 0; index < ordered.Count; index++)
            {
                DictionaryV6GoldEvaluation row = ordered[index];
                AppendCsvRow(text, row.Kind, row.StableKey, row.ExpectedChineseName,
                    row.OfficialChineseName, row.SuggestedChineseName,
                    row.OfficialMatch.ToString(), row.SuggestedMatch.ToString(),
                    row.RelationMatch.ToString(), row.EvidenceType, row.Source);
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260916 聚合名称、模块、用途、枚举和关系元数据的证据分布。</summary>
        private static string CreateEvidenceDistributionCsv(SnapshotData snapshot)
        {
            var distribution = new Dictionary<string, V6EvidenceDistributionRow>(StringComparer.Ordinal);
            if (snapshot != null && snapshot.Tables != null)
            {
                for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
                {
                    TableMetadata table = snapshot.Tables[tableIndex];
                    if (table == null) continue;
                    AddEvidenceDistribution(distribution, "Table", "OfficialName", table.ChineseName);
                    AddEvidenceDistribution(distribution, "Table", "SuggestedName", table.SuggestedChineseName);
                    AddEvidenceDistribution(distribution, "Table", "Module", table.ModuleName);
                    AddEvidenceDistribution(distribution, "Table", "Entity", table.EntityName);
                    if (table.Fields != null)
                    {
                        for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                        {
                            FieldMetadata field = table.Fields[fieldIndex];
                            if (field == null) continue;
                            AddEvidenceDistribution(distribution, "Field", "OfficialName", field.ChineseName);
                            AddEvidenceDistribution(distribution, "Field", "SuggestedName", field.SuggestedChineseName);
                            AddEvidenceDistribution(distribution, "Field", "Usage", field.Usage);
                            AddEvidenceDistribution(distribution, "Field", "EnumName", field.EnumName);
                        }
                    }
                    if (table.Relations != null)
                    {
                        for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                        {
                            RelationMetadata relation = table.Relations[relationIndex];
                            if (relation != null)
                            {
                                AddEvidenceDistribution(distribution, "Relation", "RelationType", relation.RelationType);
                            }
                        }
                    }
                }
            }
            var rows = new List<V6EvidenceDistributionRow>(distribution.Values);
            rows.Sort(CompareEvidenceDistributionRows);
            var text = new StringBuilder("ObjectType,Layer,Status,RuleName,SourceType,Count\r\n");
            for (int index = 0; index < rows.Count; index++)
            {
                V6EvidenceDistributionRow row = rows[index];
                AppendCsvRow(text, row.ObjectType, row.Layer, row.Status, row.RuleName,
                    row.SourceType, row.Count.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        /// <summary>XMZADD 20260916 按每条来源证据累计分布，无证据值单独标记以暴露不可追溯结论。</summary>
        private static void AddEvidenceDistribution(
            IDictionary<string, V6EvidenceDistributionRow> distribution,
            string objectType, string layer, MetadataValue value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.Value)) return;
            if (value.Evidence == null || value.Evidence.Count == 0)
            {
                IncrementEvidenceDistribution(distribution, objectType, layer,
                    value.Status.ToString(), "(无证据)", value.SourceType);
                return;
            }
            for (int index = 0; index < value.Evidence.Count; index++)
            {
                EvidenceItem evidence = value.Evidence[index];
                IncrementEvidenceDistribution(distribution, objectType, layer,
                    value.Status.ToString(), evidence == null ? string.Empty : evidence.RuleName,
                    evidence == null ? value.SourceType : evidence.SourceType);
            }
        }

        /// <summary>XMZADD 20260916 增加一个证据分布组合的计数。</summary>
        private static void IncrementEvidenceDistribution(
            IDictionary<string, V6EvidenceDistributionRow> distribution,
            string objectType, string layer, string status, string ruleName, string sourceType)
        {
            string key = (objectType ?? string.Empty) + "\u001F" + (layer ?? string.Empty) + "\u001F" +
                         (status ?? string.Empty) + "\u001F" + (ruleName ?? string.Empty) + "\u001F" +
                         (sourceType ?? string.Empty);
            V6EvidenceDistributionRow row;
            if (!distribution.TryGetValue(key, out row))
            {
                row = new V6EvidenceDistributionRow
                {
                    ObjectType = objectType ?? string.Empty,
                    Layer = layer ?? string.Empty,
                    Status = status ?? string.Empty,
                    RuleName = ruleName ?? string.Empty,
                    SourceType = sourceType ?? string.Empty
                };
                distribution.Add(key, row);
            }
            row.Count++;
        }

        /// <summary>XMZADD 20260916 按对象类型、表和字段比较名称覆盖行。</summary>
        private static int CompareNameCoverageRows(V6NameCoverageRow left, V6NameCoverageRow right)
        {
            int comparison = StringComparer.Ordinal.Compare(left.ObjectType, right.ObjectType);
            if (comparison != 0) return comparison;
            comparison = StringComparer.OrdinalIgnoreCase.Compare(left.SchemaName, right.SchemaName);
            if (comparison != 0) return comparison;
            comparison = StringComparer.OrdinalIgnoreCase.Compare(left.ObjectName, right.ObjectName);
            return comparison != 0 ? comparison :
                StringComparer.OrdinalIgnoreCase.Compare(left.FieldName, right.FieldName);
        }

        /// <summary>XMZADD 20260916 按表字段稳定顺序比较专项问题行。</summary>
        private static int CompareQualityIssueRows(DictionaryV6NameAuditRecord left,
            DictionaryV6NameAuditRecord right)
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.SchemaName, right.SchemaName);
            if (comparison != 0) return comparison;
            comparison = StringComparer.OrdinalIgnoreCase.Compare(left.ObjectName, right.ObjectName);
            return comparison != 0 ? comparison :
                StringComparer.OrdinalIgnoreCase.Compare(left.FieldName, right.FieldName);
        }

        /// <summary>XMZADD 20260916 按架构和对象名比较模块归属行。</summary>
        private static int CompareTablesForV6Report(TableMetadata left, TableMetadata right)
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.SchemaName, right.SchemaName);
            return comparison != 0 ? comparison :
                StringComparer.OrdinalIgnoreCase.Compare(left.ObjectName, right.ObjectName);
        }

        /// <summary>XMZADD 20260916 按类型和稳定键比较金标明细行。</summary>
        private static int CompareGoldEvaluationRows(DictionaryV6GoldEvaluation left,
            DictionaryV6GoldEvaluation right)
        {
            int comparison = StringComparer.Ordinal.Compare(left.Kind, right.Kind);
            return comparison != 0 ? comparison :
                StringComparer.OrdinalIgnoreCase.Compare(left.StableKey, right.StableKey);
        }

        /// <summary>XMZADD 20260916 按证据分组列顺序比较汇总行。</summary>
        private static int CompareEvidenceDistributionRows(V6EvidenceDistributionRow left,
            V6EvidenceDistributionRow right)
        {
            int comparison = StringComparer.Ordinal.Compare(left.ObjectType, right.ObjectType);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.Layer, right.Layer);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.Status, right.Status);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.RuleName, right.RuleName);
            return comparison != 0 ? comparison :
                StringComparer.Ordinal.Compare(left.SourceType, right.SourceType);
        }

        /// <summary>XMZADD 20260903 将一行值按 RFC 4180 基本规则转义，保证中文逗号和双引号不会破坏审计文件。</summary>
        private static void AppendCsvRow(StringBuilder text, params string[] values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                if (index > 0) text.Append(',');
                string value = values[index] ?? string.Empty;
                text.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
            }
            text.Append("\r\n");
        }

        /// <summary>XMZADD 20260903 安全读取元数据值文本。</summary>
        private static string GetValue(MetadataValue value)
        {
            return value == null ? string.Empty : value.Value ?? string.Empty;
        }

        /// <summary>XMZADD 20260903 判断名称是否仍是规则、AI、冲突或待确认结论，用于区分显示覆盖和高置信覆盖。</summary>
        private static bool IsLowConfidence(MetadataValue value)
        {
            return value == null || string.IsNullOrWhiteSpace(value.Value) ||
                   value.Status == ConfidenceStatus.Guessed ||
                   value.Status == ConfidenceStatus.GuessedConflict ||
                   value.Status == ConfidenceStatus.PendingConfirmation ||
                   value.Status == ConfidenceStatus.AiGuessed;
        }

        /// <summary>XMZADD 20260903 判断源码证据是否为 EOS 实体对象关系。</summary>
        private static bool IsObjectRelationEvidence(SourceEvidence evidence)
        {
            return evidence != null && evidence.Evidence != null &&
                   evidence.Evidence.RuleName == "EntityObjectRelation" &&
                   !string.IsNullOrWhiteSpace(evidence.ObjectName) &&
                   !string.IsNullOrWhiteSpace(evidence.RelationFieldName) &&
                   !string.IsNullOrWhiteSpace(evidence.RelationTargetEntity);
        }

        /// <summary>XMZADD 20260903 生成源码对象关系证据的去重键。</summary>
        private static string MakeObjectRelationEvidenceKey(SourceEvidence evidence)
        {
            return (evidence.ObjectName ?? string.Empty) + "\u001F" +
                   (evidence.RelationFieldName ?? string.Empty) + "\u001F" +
                   (evidence.RelationTargetEntity ?? string.Empty);
        }

        /// <summary>XMZADD 20260903 生成代码关系子表字段匹配键。</summary>
        private static string MakeChildRelationKey(string childTable, string childField)
        {
            return (childTable ?? string.Empty) + "\u001F" + (childField ?? string.Empty);
        }

        /// <summary>XMZADD 20260903 读取分类备注中的首条规则名。</summary>
        private static string GetFirstRule(MetadataValue value)
        {
            EvidenceItem evidence = GetFirstEvidence(value);
            return evidence == null ? string.Empty : evidence.RuleName ?? string.Empty;
        }

        /// <summary>XMZADD 20260903 读取元数据值的首条证据用于紧凑 CSV 审计。</summary>
        private static EvidenceItem GetFirstEvidence(MetadataValue value)
        {
            return value == null || value.Evidence == null || value.Evidence.Count == 0 ? null : value.Evidence[0];
        }

        /// <summary>XMZADD 20260903 生成关系去重键，避免父子表各存一份时重复统计和导出。</summary>
        private static string MakeRelationKey(RelationMetadata relation)
        {
            return relation == null ? string.Empty :
                (relation.ParentSchemaName ?? string.Empty) + "\u001F" +
                (relation.ParentTableName ?? string.Empty) + "\u001F" +
                (relation.ParentFieldName ?? string.Empty) + "\u001F" +
                (relation.ChildSchemaName ?? string.Empty) + "\u001F" +
                (relation.ChildTableName ?? string.Empty) + "\u001F" +
                (relation.ChildFieldName ?? string.Empty);
        }

        /// <summary>XMZADD 20260907 保存一个表字段的实际用途类型和最强源码位置。</summary>
        private sealed class ActualFieldUsage
        {
            public ActualFieldUsage(string objectName, string fieldName)
            {
                ObjectName = objectName ?? string.Empty;
                FieldName = fieldName ?? string.Empty;
                UsageKinds = new HashSet<string>(StringComparer.Ordinal);
            }

            public string ObjectName { get; private set; }
            public string FieldName { get; private set; }
            public ISet<string> UsageKinds { get; private set; }
            public SourceEvidence StrongestEvidence { get; set; }
        }

        /// <summary>XMZADD 20260916 保存正式名或参考名覆盖报告的一行稳定数据。</summary>
        private sealed class V6NameCoverageRow
        {
            public string ObjectType { get; set; }
            public string SchemaName { get; set; }
            public string ObjectName { get; set; }
            public string FieldName { get; set; }
            public string ModuleName { get; set; }
            public string ChineseName { get; set; }
            public string Status { get; set; }
            public int ConfidenceScore { get; set; }
            public string RuleName { get; set; }
            public string SourcePath { get; set; }
            public int SourceLine { get; set; }
        }

        /// <summary>XMZADD 20260916 保存相同对象层、元数据层、状态与证据来源组合的计数。</summary>
        private sealed class V6EvidenceDistributionRow
        {
            public string ObjectType { get; set; }
            public string Layer { get; set; }
            public string Status { get; set; }
            public string RuleName { get; set; }
            public string SourceType { get; set; }
            public int Count { get; set; }
        }

        /// <summary>XMZADD 20260907 保存名称变更字段及其当前最强证据供稳定排序和导出。</summary>
        private sealed class NameChangeRecord
        {
            public NameChangeRecord(TableMetadata table, FieldMetadata field, EvidenceItem evidence)
            {
                Table = table;
                Field = field;
                Evidence = evidence;
            }

            public TableMetadata Table { get; private set; }
            public FieldMetadata Field { get; private set; }
            public EvidenceItem Evidence { get; private set; }
        }

        /// <summary>XMZADD 20260907 保存实际使用待复核字段及其聚合用途证据。</summary>
        private sealed class ActualFieldRecord
        {
            public ActualFieldRecord(TableMetadata table, FieldMetadata field, ActualFieldUsage usage)
            {
                Table = table;
                Field = field;
                Usage = usage;
            }

            public TableMetadata Table { get; private set; }
            public FieldMetadata Field { get; private set; }
            public ActualFieldUsage Usage { get; private set; }
        }

        /// <summary>XMZADD 20260907 保存一个未知缩写涉及的字段数量和确定性示例。</summary>
        private sealed class UnknownTokenFrequency
        {
            public UnknownTokenFrequency(string token, ActualFieldRecord example)
            {
                Token = token ?? string.Empty;
                Example = example;
            }

            public string Token { get; private set; }
            public int Count { get; set; }
            public ActualFieldRecord Example { get; private set; }
        }
    }

    /// <summary>XMZADD 20260903 保存第一版业务字典的可比较覆盖率指标。</summary>
    public sealed class BusinessDictionaryV1Report
    {
        public int TotalTableCount { get; set; }
        public int TotalFieldCount { get; set; }
        public int BusinessTableCount { get; set; }
        public int EntityTableCount { get; set; }
        public int KnowledgeTableCount { get; set; }
        public int NamedFieldCount { get; set; }
        public int HighConfidenceFieldNameCount { get; set; }
        public int LowConfidenceTableNameCount { get; set; }
        public int EnumFieldCount { get; set; }
        public int TotalRelationCount { get; set; }
        public int PhysicalRelationCount { get; set; }
        public int CodeRelationCount { get; set; }
        public int ObjectRelationEvidenceCount { get; set; }
        public int UnresolvedObjectRelationCount { get; set; }
        public int TechnicalTableCount { get; set; }
        public int ExcludedTableCount { get; set; }
        public int UnclassifiedTableCount { get; set; }
        public int PendingTableNameCount { get; set; }
        public int ActualUsedFieldCount { get; set; }
        public int ActualUsedNamedFieldCount { get; set; }
        public int V4RefinedNameCount { get; set; }
        public int ActualUsedReviewFieldCount { get; set; }
        public int AmbiguousConflictCount { get; set; }
    }
}
