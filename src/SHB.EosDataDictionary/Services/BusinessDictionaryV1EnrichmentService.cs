using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260916 合并 EOS 源码、知识库及交叉核验关系证据，生成可审计业务数据字典。</summary>
    public sealed class BusinessDictionaryV1EnrichmentService
    {
        /// <summary>XMZADD 20260916 按固定优先级补全实体、名称、模块、枚举和经交叉核验的代码关系。</summary>
        public void Enrich(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence,
            EosProjectKnowledgeCatalog knowledgeCatalog)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }

            // 旧快照中的自动弱值必须先按当前稳定词根重算，后续实体、知识库和业务代码证据才能在统一基线上选择名称。
            IdentifierTranslationService.RepairWeakMetadata(snapshot);
            MetadataEnrichmentService.Enrich(snapshot, sourceEvidence);
            ApplyKnowledgeBase(snapshot, knowledgeCatalog);
            // 业务代码中的动态表用途比知识库简称更精确，用于纠正逐词翻译和宽泛名称。
            new BusinessCodeNameInferenceService().Apply(snapshot, sourceEvidence);
            BusinessSemanticRuleService.Apply(snapshot);
            new CodeRelationEvidenceService().Apply(snapshot, sourceEvidence);
            // 离线快照必须固化命名关系，确保共享字典无需打开详情页也拥有完整关联证据。
            LogicalRelationDiscoveryService.Discover(snapshot);
            new BusinessTableClassificationService().Classify(snapshot, sourceEvidence, knowledgeCatalog);
            // 所有后置业务规则也必须经过同一准入门槛，避免弱代码候选重新占用正式名称列。
            new BusinessNameLayerService().NormalizeSnapshot(snapshot);
        }

        /// <summary>XMZADD 20260903 将表索引和核心字段精确结论合并到普通推测之上，同时保护人工及数据库依据。</summary>
        private static void ApplyKnowledgeBase(SnapshotData snapshot, EosProjectKnowledgeCatalog knowledgeCatalog)
        {
            if (knowledgeCatalog == null)
            {
                return;
            }
            var nameLayer = new BusinessNameLayerService();
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                EosKnowledgeTableEntry tableEntry = knowledgeCatalog.FindTable(table.ObjectName);
                if (tableEntry != null)
                {
                    if (IdentifierTranslationService.IsReliableChineseName(tableEntry.ChineseName))
                    {
                        // 精确知识与既有数据库或代码结论不一致时必须进入冲突候选，不能被优先级判断静默丢弃。
                        nameLayer.ApplyTableCandidate(table, CreateKnowledgeValue(
                            tableEntry.ChineseName, tableEntry.RelativePath, tableEntry.LineNumber,
                            "ExactProjectTable", tableEntry.TableName + "=" + tableEntry.ChineseName,
                            "EOS 项目表索引精确条目"));
                    }
                    if (MetadataEvidencePolicy.CanReplace(table.ModuleName, ConfidenceStatus.KnowledgeBaseEvidence, "ProjectModuleHeading"))
                    {
                        table.ModuleName = CreateKnowledgeValue(
                            tableEntry.ModuleName, tableEntry.RelativePath, tableEntry.LineNumber,
                            "ProjectModuleHeading", tableEntry.TableName + "=" + tableEntry.ModuleName,
                            "EOS 项目表索引业务分组");
                    }
                    if (!string.IsNullOrWhiteSpace(tableEntry.Remark) &&
                        MetadataEvidencePolicy.CanReplace(table.BusinessMeaning, ConfidenceStatus.KnowledgeBaseEvidence, "ProjectTableRemark"))
                    {
                        table.BusinessMeaning = CreateKnowledgeValue(
                            tableEntry.Remark, tableEntry.RelativePath, tableEntry.LineNumber,
                            "ProjectTableRemark", tableEntry.Remark, "EOS 项目表索引备注");
                    }
                    table.Category = DictionaryTableCategory.Business;
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
                    EosKnowledgeFieldEntry fieldEntry = knowledgeCatalog.FindField(table.ObjectName, field.FieldName);
                    if (fieldEntry == null)
                    {
                        continue;
                    }
                    if (IdentifierTranslationService.IsReliableChineseName(fieldEntry.ChineseName))
                    {
                        // 字段精确知识同样保留为可审阅候选，防止受保护正式名遮蔽新的业务依据。
                        nameLayer.ApplyFieldCandidate(field, CreateKnowledgeValue(
                            fieldEntry.ChineseName, fieldEntry.RelativePath, fieldEntry.LineNumber,
                            "ExactProjectField", field.FieldName + "=" + fieldEntry.ChineseName,
                            "EOS 项目核心字段字典精确条目"));
                    }
                    ApplyKnowledgeEnum(field, fieldEntry);
                }
            }
        }

        /// <summary>XMZADD 20260903 将知识库常量说明发布为字段枚举，且不覆盖人工或数据库枚举。</summary>
        private static void ApplyKnowledgeEnum(FieldMetadata field, EosKnowledgeFieldEntry fieldEntry)
        {
            if (fieldEntry.EnumItems == null || fieldEntry.EnumItems.Count == 0 ||
                !MetadataEvidencePolicy.CanReplace(field.EnumName, ConfidenceStatus.KnowledgeBaseEvidence, "ProjectFieldEnum"))
            {
                return;
            }
            field.EnumName = CreateKnowledgeValue(
                field.FieldName + "业务枚举", fieldEntry.RelativePath, fieldEntry.LineNumber,
                "ProjectFieldEnum", field.FieldName, "EOS 项目核心字段字典常用枚举");
            field.EnumItems = new List<EnumItemMetadata>();
            for (int itemIndex = 0; itemIndex < fieldEntry.EnumItems.Count; itemIndex++)
            {
                EosKnowledgeEnumItem item = fieldEntry.EnumItems[itemIndex];
                field.EnumItems.Add(new EnumItemMetadata
                {
                    Value = item.Value,
                    ChineseName = CreateKnowledgeValue(
                        item.ChineseName, item.RelativePath, item.LineNumber,
                        "ProjectEnumItem", field.FieldName + "=" + item.Value,
                        "EOS 项目核心字段字典枚举值")
                });
            }
        }

        /// <summary>XMZADD 20260903 创建包含知识库相对路径和行号的高可信元数据值。</summary>
        private static MetadataValue CreateKnowledgeValue(string value, string path, int line,
            string ruleName, string rawValue, string explanation)
        {
            return new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.KnowledgeBaseEvidence,
                SourceType = "EOS知识库",
                SourceSummary = explanation,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "EOS知识库",
                        SourcePath = path,
                        SourceLine = line,
                        RuleName = ruleName,
                        RawValue = rawValue,
                        OriginalText = rawValue,
                        Explanation = explanation
                    }
                }
            };
        }

    }
}
