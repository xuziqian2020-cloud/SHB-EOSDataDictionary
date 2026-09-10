using System;
using System.Collections.Generic;
using System.IO;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260903 合并 EOS 源码与项目知识库的高置信证据，生成第一版业务数据字典。</summary>
    public sealed class BusinessDictionaryV1EnrichmentService
    {
        /// <summary>XMZADD 20260903 按固定优先级补全实体、中文名、模块、枚举和代码对象关系。</summary>
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
            ApplyCodeRelations(snapshot, sourceEvidence);
            // 离线快照必须固化命名关系，确保共享字典无需打开详情页也拥有完整关联证据。
            LogicalRelationDiscoveryService.Discover(snapshot);
            new BusinessTableClassificationService().Classify(snapshot, sourceEvidence, knowledgeCatalog);
        }

        /// <summary>XMZADD 20260903 将表索引和核心字段精确结论合并到普通推测之上，同时保护人工及数据库依据。</summary>
        private static void ApplyKnowledgeBase(SnapshotData snapshot, EosProjectKnowledgeCatalog knowledgeCatalog)
        {
            if (knowledgeCatalog == null)
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
                EosKnowledgeTableEntry tableEntry = knowledgeCatalog.FindTable(table.ObjectName);
                if (tableEntry != null)
                {
                    if (MetadataEvidencePolicy.CanReplace(table.ChineseName, ConfidenceStatus.KnowledgeBaseEvidence, "ExactProjectTable"))
                    {
                        table.ChineseName = CreateKnowledgeValue(
                            tableEntry.ChineseName, tableEntry.RelativePath, tableEntry.LineNumber,
                            "ExactProjectTable", tableEntry.TableName + "=" + tableEntry.ChineseName,
                            "EOS 项目表索引精确条目");
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
                    if (MetadataEvidencePolicy.CanReplace(field.ChineseName, ConfidenceStatus.KnowledgeBaseEvidence, "ExactProjectField") &&
                        IdentifierTranslationService.IsReliableChineseName(fieldEntry.ChineseName))
                    {
                        field.ChineseName = CreateKnowledgeValue(
                            fieldEntry.ChineseName, fieldEntry.RelativePath, fieldEntry.LineNumber,
                            "ExactProjectField", field.FieldName + "=" + fieldEntry.ChineseName,
                            "EOS 项目核心字段字典精确条目");
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

        /// <summary>XMZADD 20260903 将 obj 字段指向的目标实体解析为双向可见的代码证据关系。</summary>
        private static void ApplyCodeRelations(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence)
        {
            if (sourceEvidence == null)
            {
                return;
            }
            var tableByName = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            var tableByEntity = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            var existingRelations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BuildRelationIndexes(snapshot, tableByName, tableByEntity, existingRelations);

            for (int evidenceIndex = 0; evidenceIndex < sourceEvidence.Count; evidenceIndex++)
            {
                SourceEvidence evidence = sourceEvidence[evidenceIndex];
                if (evidence == null || evidence.Evidence == null ||
                    evidence.Evidence.RuleName != "EntityObjectRelation" ||
                    string.IsNullOrWhiteSpace(evidence.RelationFieldName) ||
                    string.IsNullOrWhiteSpace(evidence.RelationTargetEntity))
                {
                    continue;
                }
                TableMetadata child;
                TableMetadata parent;
                if (!tableByName.TryGetValue(evidence.ObjectName ?? string.Empty, out child) || child == null ||
                    !tableByEntity.TryGetValue(evidence.RelationTargetEntity, out parent) || parent == null)
                {
                    continue;
                }
                FieldMetadata childField = FindField(child, evidence.RelationFieldName, false);
                FieldMetadata parentField = FindField(parent, evidence.RelationFieldName, true) ?? FindFirstPrimaryKey(parent);
                string relationKey = MakeRelationKey(child, evidence.RelationFieldName);
                if (childField == null || parentField == null || existingRelations.Contains(relationKey))
                {
                    continue;
                }

                EvidenceItem publishedEvidence = CreatePublishedEvidence(evidence.Evidence);
                var relation = new RelationMetadata
                {
                    ScopeKey = child.ScopeKey,
                    ForeignKeyName = "代码_" + child.ObjectName + "_" + childField.FieldName,
                    ParentSchemaName = parent.SchemaName,
                    ParentTableName = parent.ObjectName,
                    ParentFieldName = parentField.FieldName,
                    ChildSchemaName = child.SchemaName,
                    ChildTableName = child.ObjectName,
                    ChildFieldName = childField.FieldName,
                    RelationType = new MetadataValue
                    {
                        Value = "代码关联：多对一",
                        Status = ConfidenceStatus.CodeEvidence,
                        SourceType = "EOS源码",
                        SourceSummary = "EOS 实体对象属性",
                        Evidence = new List<EvidenceItem> { publishedEvidence }
                    }
                };
                EnsureMutableRelations(child);
                EnsureMutableRelations(parent);
                child.Relations.Add(relation);
                if (!ReferenceEquals(child, parent))
                {
                    parent.Relations.Add(relation);
                }
                existingRelations.Add(relationKey);
                if (!IsProtectedValue(childField.RelationSummary))
                {
                    childField.RelationSummary = new MetadataValue
                    {
                        Value = "代码关联：" + parent.ObjectName + "." + parentField.FieldName,
                        Status = ConfidenceStatus.CodeEvidence,
                        SourceType = "EOS源码",
                        SourceSummary = "EOS 实体对象属性",
                        Evidence = new List<EvidenceItem> { publishedEvidence }
                    };
                }
            }
        }

        /// <summary>XMZADD 20260903 建立唯一表名、唯一实体名和已有关系索引，避免七万表场景反复扫描。</summary>
        private static void BuildRelationIndexes(SnapshotData snapshot,
            IDictionary<string, TableMetadata> tableByName,
            IDictionary<string, TableMetadata> tableByEntity,
            ISet<string> existingRelations)
        {
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                AddUniqueTable(tableByName, table.ObjectName, table);
                if (table.EntityName != null)
                {
                    AddUniqueTable(tableByEntity, table.EntityName.Value, table);
                }
                if (table.Relations == null)
                {
                    continue;
                }
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = table.Relations[relationIndex];
                    if (relation != null)
                    {
                        existingRelations.Add(MakeRelationKey(
                            relation.ChildSchemaName, relation.ChildTableName, relation.ChildFieldName));
                    }
                }
            }
        }

        /// <summary>XMZADD 20260903 仅为唯一对象键保留映射，重复架构对象保持空值以阻止错误猜测。</summary>
        private static void AddUniqueTable(IDictionary<string, TableMetadata> index, string key, TableMetadata table)
        {
            string normalizedKey = key ?? string.Empty;
            if (normalizedKey.Length == 0)
            {
                return;
            }
            if (index.ContainsKey(normalizedKey))
            {
                index[normalizedKey] = null;
            }
            else
            {
                index.Add(normalizedKey, table);
            }
        }

        /// <summary>XMZADD 20260903 按字段名查找关系端点，并可要求目标字段必须为真实主键。</summary>
        private static FieldMetadata FindField(TableMetadata table, string fieldName, bool requirePrimaryKey)
        {
            if (table == null || table.Fields == null)
            {
                return null;
            }
            for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
            {
                FieldMetadata field = table.Fields[fieldIndex];
                if (field != null && (!requirePrimaryKey || field.IsPrimaryKey) &&
                    string.Equals(field.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260903 在对象关系未直接给出目标字段时使用目标实体的首个真实主键。</summary>
        private static FieldMetadata FindFirstPrimaryKey(TableMetadata table)
        {
            if (table == null || table.Fields == null)
            {
                return null;
            }
            for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
            {
                FieldMetadata field = table.Fields[fieldIndex];
                if (field != null && field.IsPrimaryKey)
                {
                    return field;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260903 将旧快照反序列化出的只读关系数组转换为可追加集合。</summary>
        private static void EnsureMutableRelations(TableMetadata table)
        {
            if (table.Relations == null)
            {
                table.Relations = new List<RelationMetadata>();
            }
            else if (table.Relations.IsReadOnly)
            {
                table.Relations = new List<RelationMetadata>(table.Relations);
            }
        }

        /// <summary>XMZADD 20260903 判断高优先级或人工锁定值是否禁止第一版自动覆盖。</summary>
        private static bool IsProtectedValue(MetadataValue value)
        {
            return value != null && !string.IsNullOrWhiteSpace(value.Value) &&
                   (value.IsManualOverride || value.IsLocked ||
                    value.Status == ConfidenceStatus.LocalOverride ||
                    value.Status == ConfidenceStatus.Confirmed ||
                    value.Status == ConfidenceStatus.DatabaseEvidence);
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

        /// <summary>XMZADD 20260903 复制代码关系证据并移除可能泄露本机目录的绝对路径。</summary>
        private static EvidenceItem CreatePublishedEvidence(EvidenceItem source)
        {
            string path = source == null ? string.Empty : source.SourcePath ?? string.Empty;
            if (Path.IsPathRooted(path) || path.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                path = Path.GetFileName(path);
            }
            return new EvidenceItem
            {
                SourceType = source == null ? "EOS源码" : source.SourceType,
                SourcePath = path.Replace('\\', '/'),
                SourceLine = source == null ? 0 : source.SourceLine,
                RuleName = source == null ? "EntityObjectRelation" : source.RuleName,
                RawValue = source == null ? string.Empty : source.RawValue,
                OriginalText = source == null ? string.Empty : source.OriginalText,
                Explanation = source == null ? "EOS 实体对象属性" : source.Explanation
            };
        }

        /// <summary>XMZADD 20260903 生成代码关系的子表字段唯一键，避免与物理外键或其他代码证据重复。</summary>
        private static string MakeRelationKey(TableMetadata child, string childField)
        {
            return MakeRelationKey(child == null ? null : child.SchemaName,
                child == null ? null : child.ObjectName, childField);
        }

        /// <summary>XMZADD 20260903 按架构、子表和子字段生成稳定关系键。</summary>
        private static string MakeRelationKey(string childSchema, string childTable, string childField)
        {
            return (childSchema ?? string.Empty) + "\u001F" +
                   (childTable ?? string.Empty) + "\u001F" + (childField ?? string.Empty);
        }
    }
}
