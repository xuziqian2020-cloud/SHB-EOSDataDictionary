using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260916 在缺少真实外键和代码关系时按完整父子端点补充明确标为推测的逻辑关系。</summary>
    public static class LogicalRelationDiscoveryService
    {
        /// <summary>XMZADD 20260916 一次建立表名索引并按完整关系键发现唯一命名外键，使父子表共享同一关系。</summary>
        public static void Discover(SnapshotData snapshot)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }

            var tableIndex = new Dictionary<string, List<TableMetadata>>(StringComparer.OrdinalIgnoreCase);
            var existingRelations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryKeyCache = new Dictionary<TableMetadata, IDictionary<string, FieldMetadata>>();
            BuildIndexes(snapshot, tableIndex, existingRelations);

            for (int tableNumber = 0; tableNumber < snapshot.Tables.Count; tableNumber++)
            {
                TableMetadata child = snapshot.Tables[tableNumber];
                if (child == null || child.Fields == null)
                {
                    continue;
                }
                for (int fieldNumber = 0; fieldNumber < child.Fields.Count; fieldNumber++)
                {
                    FieldMetadata childField = child.Fields[fieldNumber];
                    if (childField == null || childField.IsPrimaryKey || childField.IsForeignKey)
                    {
                        continue;
                    }

                    string referenceName = GetReferenceName(childField.FieldName);
                    List<TableMetadata> candidates;
                    if (string.IsNullOrWhiteSpace(referenceName) || !tableIndex.TryGetValue(NormalizeTableName(referenceName), out candidates) || candidates.Count != 1)
                    {
                        continue;
                    }

                    TableMetadata parent = candidates[0];
                    if (parent == child)
                    {
                        continue;
                    }
                    FieldMetadata parentField = FindPrimaryKey(parent, referenceName, primaryKeyCache);
                    if (parentField == null)
                    {
                        continue;
                    }
                    string relationKey = MakeRelationKey(parent.SchemaName, parent.ObjectName, parentField.FieldName,
                        child.SchemaName, child.ObjectName, childField.FieldName);
                    if (existingRelations.Contains(relationKey))
                    {
                        continue;
                    }

                    RelationMetadata relation = CreateRelation(parent, parentField, child, childField);
                    parent.Relations.Add(relation);
                    child.Relations.Add(relation);
                    existingRelations.Add(relationKey);
                    childField.RelationSummary = new MetadataValue
                    {
                        Value = "推测关联：" + parent.ObjectName + "." + parentField.FieldName,
                        Status = ConfidenceStatus.Guessed,
                        SourceType = "结构推测",
                        SourceSummary = "字段命名与目标表主键匹配",
                        Evidence = new List<EvidenceItem> { CreateLogicalEvidence(parent, parentField, child, childField) }
                    };
                }
            }
        }

        /// <summary>XMZADD 20260903 仅为当前查看的数据表生成双向逻辑关系，避免七万表启动时实例化数十万条推测对象。</summary>
        public static void DiscoverForTable(SnapshotData snapshot, TableMetadata target)
        {
            if (snapshot == null || snapshot.Tables == null || target == null)
            {
                return;
            }

            EnsureMutableRelations(target);
            var tableIndex = new Dictionary<string, List<TableMetadata>>(StringComparer.OrdinalIgnoreCase);
            var existingRelations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetRelationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingRelationsToAdd = new List<RelationMetadata>();
            for (int relationIndex = 0; relationIndex < target.Relations.Count; relationIndex++)
            {
                RelationMetadata relation = target.Relations[relationIndex];
                if (relation != null)
                {
                    // 当前表已有关系必须先占用唯一键，避免其他表保存的双向副本再次加入详情。
                    targetRelationKeys.Add(MakeRelationKey(
                        relation.ParentSchemaName, relation.ParentTableName, relation.ParentFieldName,
                        relation.ChildSchemaName, relation.ChildTableName, relation.ChildFieldName));
                }
            }
            for (int tableIndexNumber = 0; tableIndexNumber < snapshot.Tables.Count; tableIndexNumber++)
            {
                TableMetadata table = snapshot.Tables[tableIndexNumber];
                if (table == null)
                {
                    continue;
                }
                string tableKey = NormalizeTableName(table.ObjectName);
                List<TableMetadata> candidates;
                if (!tableIndex.TryGetValue(tableKey, out candidates))
                {
                    candidates = new List<TableMetadata>();
                    tableIndex.Add(tableKey, candidates);
                }
                candidates.Add(table);
                if (table.Relations == null)
                {
                    continue;
                }
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = table.Relations[relationIndex];
                    if (relation == null)
                    {
                        continue;
                    }
                    string relationKey = MakeRelationKey(
                        relation.ParentSchemaName, relation.ParentTableName, relation.ParentFieldName,
                        relation.ChildSchemaName, relation.ChildTableName, relation.ChildFieldName);
                    existingRelations.Add(relationKey);
                    if (RelationInvolvesTable(relation, target) && targetRelationKeys.Add(relationKey) &&
                        !object.ReferenceEquals(table, target))
                    {
                        existingRelationsToAdd.Add(relation);
                    }
                }
            }
            for (int relationIndex = 0; relationIndex < existingRelationsToAdd.Count; relationIndex++)
            {
                target.Relations.Add(existingRelationsToAdd[relationIndex]);
            }

            var primaryKeyCache = new Dictionary<TableMetadata, IDictionary<string, FieldMetadata>>();
            for (int tableNumber = 0; tableNumber < snapshot.Tables.Count; tableNumber++)
            {
                TableMetadata child = snapshot.Tables[tableNumber];
                if (child == null || child.Fields == null)
                {
                    continue;
                }
                for (int fieldNumber = 0; fieldNumber < child.Fields.Count; fieldNumber++)
                {
                    FieldMetadata childField = child.Fields[fieldNumber];
                    if (childField == null || childField.IsPrimaryKey || childField.IsForeignKey)
                    {
                        continue;
                    }
                    string referenceName = GetReferenceName(childField.FieldName);
                    List<TableMetadata> candidates;
                    if (string.IsNullOrWhiteSpace(referenceName) ||
                        !tableIndex.TryGetValue(NormalizeTableName(referenceName), out candidates) ||
                        candidates.Count != 1)
                    {
                        continue;
                    }
                    TableMetadata parent = candidates[0];
                    if (parent == child || (parent != target && child != target))
                    {
                        continue;
                    }
                    FieldMetadata parentField = FindPrimaryKey(parent, referenceName, primaryKeyCache);
                    if (parentField == null)
                    {
                        continue;
                    }
                    string relationKey = MakeRelationKey(parent.SchemaName, parent.ObjectName, parentField.FieldName,
                        child.SchemaName, child.ObjectName, childField.FieldName);
                    if (existingRelations.Contains(relationKey))
                    {
                        continue;
                    }
                    RelationMetadata relation = CreateRelation(parent, parentField, child, childField);
                    target.Relations.Add(relation);
                    existingRelations.Add(relationKey);
                    childField.RelationSummary = new MetadataValue
                    {
                        Value = "推测关联：" + parent.ObjectName + "." + parentField.FieldName,
                        Status = ConfidenceStatus.Guessed,
                        SourceType = "结构推测",
                        SourceSummary = "字段命名与目标表主键匹配",
                        Evidence = new List<EvidenceItem> { CreateLogicalEvidence(parent, parentField, child, childField) }
                    };
                }
            }
        }

        /// <summary>XMZADD 20260903 只把当前表的关系集合转换为可追加列表，避免按需推测修改其余七万张表。</summary>
        private static void EnsureMutableRelations(TableMetadata table)
        {
            if (table.Relations == null)
            {
                table.Relations = new List<RelationMetadata>();
                return;
            }
            if (!table.Relations.IsReadOnly)
            {
                return;
            }
            var mutableRelations = new List<RelationMetadata>();
            for (int index = 0; index < table.Relations.Count; index++)
            {
                mutableRelations.Add(table.Relations[index]);
            }
            table.Relations = mutableRelations;
        }

        /// <summary>XMZADD 20260903 判断已有真实或推测关系是否应在当前表的双向详情中展示。</summary>
        private static bool RelationInvolvesTable(RelationMetadata relation, TableMetadata table)
        {
            return TableEndpointMatches(relation.ParentSchemaName, relation.ParentTableName, table) ||
                TableEndpointMatches(relation.ChildSchemaName, relation.ChildTableName, table);
        }

        /// <summary>XMZADD 20260903 按架构名和表名匹配关系端点，旧快照缺少架构时保留表名兼容路径。</summary>
        private static bool TableEndpointMatches(string schemaName, string tableName, TableMetadata table)
        {
            return table != null &&
                string.Equals(tableName, table.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(schemaName) ||
                 string.Equals(schemaName, table.SchemaName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>XMZADD 20260831 建立规范表名和现有关联索引，避免对近百万字段反复扫描全部对象。</summary>
        private static void BuildIndexes(SnapshotData snapshot,
            IDictionary<string, List<TableMetadata>> tableIndex, ISet<string> existingRelations)
        {
            for (int i = 0; i < snapshot.Tables.Count; i++)
            {
                TableMetadata table = snapshot.Tables[i];
                if (table == null)
                {
                    continue;
                }
                string key = NormalizeTableName(table.ObjectName);
                List<TableMetadata> candidates;
                if (!tableIndex.TryGetValue(key, out candidates))
                {
                    candidates = new List<TableMetadata>();
                    tableIndex.Add(key, candidates);
                }
                candidates.Add(table);

                if (table.Relations == null)
                {
                    table.Relations = new List<RelationMetadata>();
                }
                else if (table.Relations.IsReadOnly)
                {
                    // 旧快照会把 IList 还原为定长数组，生成逻辑关系前必须保留原项并转换为可追加集合。
                    var mutableRelations = new List<RelationMetadata>();
                    for (int relationNumber = 0; relationNumber < table.Relations.Count; relationNumber++)
                    {
                        mutableRelations.Add(table.Relations[relationNumber]);
                    }
                    table.Relations = mutableRelations;
                }
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = table.Relations[relationIndex];
                    if (relation != null)
                    {
                        existingRelations.Add(MakeRelationKey(
                            relation.ParentSchemaName, relation.ParentTableName, relation.ParentFieldName,
                            relation.ChildSchemaName, relation.ChildTableName, relation.ChildFieldName));
                    }
                }
            }
        }

        /// <summary>XMZADD 20260831 从 Item_ID、ItemID、FITEMID 等传统字段名提取目标业务表名。</summary>
        private static string GetReferenceName(string fieldName)
        {
            string value = (fieldName ?? string.Empty).Trim();
            if (value.EndsWith("_ID", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 3);
            }
            else if (value.EndsWith("ID", StringComparison.OrdinalIgnoreCase) && value.Length > 2)
            {
                value = value.Substring(0, value.Length - 2).TrimEnd('_');
            }
            else
            {
                return null;
            }
            if (value.Length > 1 && (value[0] == 'F' || value[0] == 'f') && value.IndexOf('_') < 0)
            {
                value = value.Substring(1);
            }
            return value;
        }

        /// <summary>XMZADD 20260831 去除常见对象类型前缀，使 Item、T_Item 和 TB_Item 按同一业务名参与唯一性判断。</summary>
        private static string NormalizeTableName(string tableName)
        {
            string value = (tableName ?? string.Empty).Trim();
            if (value.StartsWith("TB_", StringComparison.OrdinalIgnoreCase)) return value.Substring(3);
            if (value.StartsWith("T_", StringComparison.OrdinalIgnoreCase) || value.StartsWith("V_", StringComparison.OrdinalIgnoreCase)) return value.Substring(2);
            return value;
        }

        /// <summary>XMZADD 20260831 按业务专用主键、通用 ID、传统 FID 的顺序选取并缓存目标表真实主键。</summary>
        private static FieldMetadata FindPrimaryKey(
            TableMetadata table,
            string referenceName,
            IDictionary<TableMetadata, IDictionary<string, FieldMetadata>> primaryKeyCache)
        {
            if (table == null || table.Fields == null)
            {
                return null;
            }
            IDictionary<string, FieldMetadata> tableCache;
            if (!primaryKeyCache.TryGetValue(table, out tableCache))
            {
                tableCache = new Dictionary<string, FieldMetadata>(StringComparer.OrdinalIgnoreCase);
                primaryKeyCache.Add(table, tableCache);
            }
            FieldMetadata cached;
            if (tableCache.TryGetValue(referenceName, out cached))
            {
                return cached;
            }
            string specificName = referenceName + "_ID";
            FieldMetadata generic = null;
            for (int i = 0; i < table.Fields.Count; i++)
            {
                FieldMetadata field = table.Fields[i];
                if (field == null || !field.IsPrimaryKey)
                {
                    continue;
                }
                if (string.Equals(field.FieldName, specificName, StringComparison.OrdinalIgnoreCase))
                {
                    tableCache.Add(referenceName, field);
                    return field;
                }
                if (string.Equals(field.FieldName, "ID", StringComparison.OrdinalIgnoreCase) || string.Equals(field.FieldName, "FID", StringComparison.OrdinalIgnoreCase)) generic = field;
            }
            tableCache.Add(referenceName, generic);
            return generic;
        }

        /// <summary>XMZADD 20260831 创建带来源状态的逻辑关系，确保用户不会把命名推测误认为数据库真实约束。</summary>
        private static RelationMetadata CreateRelation(TableMetadata parent, FieldMetadata parentField,
            TableMetadata child, FieldMetadata childField)
        {
            return new RelationMetadata
            {
                ScopeKey = child.ScopeKey,
                ForeignKeyName = "推测_" + child.ObjectName + "_" + childField.FieldName,
                ParentSchemaName = parent.SchemaName,
                ParentTableName = parent.ObjectName,
                ParentFieldName = parentField.FieldName,
                ChildSchemaName = child.SchemaName,
                ChildTableName = child.ObjectName,
                ChildFieldName = childField.FieldName,
                RelationType = new MetadataValue
                {
                    Value = "推测：多对一",
                    Status = ConfidenceStatus.Guessed,
                    SourceType = "结构推测",
                    SourceSummary = "字段命名与目标表主键匹配",
                    Evidence = new List<EvidenceItem> { CreateLogicalEvidence(parent, parentField, child, childField) }
                }
            };
        }

        /// <summary>XMZADD 20260903 记录命名外键的匹配端点和规则，使推测关系可在来源证据页审阅。</summary>
        private static EvidenceItem CreateLogicalEvidence(TableMetadata parent, FieldMetadata parentField,
            TableMetadata child, FieldMetadata childField)
        {
            string childEndpoint = (child == null ? string.Empty : child.ObjectName) + "." +
                (childField == null ? string.Empty : childField.FieldName);
            string parentEndpoint = (parent == null ? string.Empty : parent.ObjectName) + "." +
                (parentField == null ? string.Empty : parentField.FieldName);
            return new EvidenceItem
            {
                SourceType = "结构推测",
                RuleName = "LogicalForeignKeyName",
                RawValue = childField == null ? string.Empty : childField.FieldName,
                OriginalText = childEndpoint + " -> " + parentEndpoint,
                Explanation = "子字段命名唯一匹配目标表及其真实主键"
            };
        }

        /// <summary>XMZADD 20260916 生成完整父子端点关系键，阻止同目标重复且保留同一子字段的真实冲突目标。</summary>
        private static string MakeRelationKey(string parentSchema, string parentTable, string parentField,
            string childSchema, string childTable, string childField)
        {
            return string.Concat(parentSchema ?? string.Empty, "\u001F", parentTable ?? string.Empty,
                "\u001F", parentField ?? string.Empty, "\u001F", childSchema ?? string.Empty,
                "\u001F", childTable ?? string.Empty, "\u001F", childField ?? string.Empty);
        }
    }
}
