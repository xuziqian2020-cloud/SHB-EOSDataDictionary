using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260831 对比两个本地数据库结构快照，识别后续需要补充业务解释的对象和字段变化。</summary>
    public sealed class SchemaDiffService
    {
        /// <summary>XMZADD 20260831 比较同一作用域的前后快照，输出新增、删除和物理属性变更的结构差异。</summary>
        public SchemaDiffResult Compare(string scopeKey, SnapshotData previous, SnapshotData current)
        {
            var result = new SchemaDiffResult();
            IDictionary<string, TableMetadata> previousTables = BuildTableLookup(previous);
            IDictionary<string, TableMetadata> currentTables = BuildTableLookup(current);
            string normalizedScopeKey = scopeKey ?? string.Empty;
            DateTime detectedAt = DateTime.Now;

            foreach (KeyValuePair<string, TableMetadata> currentPair in currentTables)
            {
                TableMetadata previousTable;
                if (!previousTables.TryGetValue(currentPair.Key, out previousTable))
                {
                    // 新对象没有既有解释，后续流程应优先为它建立字典信息。
                    result.Changes.Add(CreateObjectChange(normalizedScopeKey, currentPair.Value, SchemaChangeType.ObjectAdded, null, DescribeObject(currentPair.Value), detectedAt));
                    AddAllFieldChanges(result, normalizedScopeKey, currentPair.Value, SchemaChangeType.FieldAdded, detectedAt);
                    continue;
                }

                CompareExistingTable(result, normalizedScopeKey, previousTable, currentPair.Value, detectedAt);
            }

            foreach (KeyValuePair<string, TableMetadata> previousPair in previousTables)
            {
                if (currentTables.ContainsKey(previousPair.Key))
                {
                    continue;
                }

                // 保留已删除对象的定位信息，便于人工维护的旧解释被标记而非直接丢失。
                result.Changes.Add(CreateObjectChange(normalizedScopeKey, previousPair.Value, SchemaChangeType.ObjectRemoved, DescribeObject(previousPair.Value), null, detectedAt));
            }

            CompareRelations(result, normalizedScopeKey, previous, current, detectedAt);
            result.Changes = OrderChangesForProtocol(result.Changes);

            return result;
        }

        /// <summary>XMZADD 20260901 按关系、字段、表的依赖顺序排列删除事件，并按表、字段、关系排列新增事件。</summary>
        private static IList<SchemaChange> OrderChangesForProtocol(IList<SchemaChange> source)
        {
            var result = new List<SchemaChange>();
            for (int priority = 0; priority <= 6; priority++)
            {
                for (int index = 0; index < source.Count; index++)
                {
                    SchemaChange change = source[index];
                    if (change != null && GetProtocolPriority(change.ChangeType) == priority)
                    {
                        result.Add(change);
                    }
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260901 将结构变化映射为服务端可逐条应用的依赖优先级。</summary>
        private static int GetProtocolPriority(SchemaChangeType changeType)
        {
            switch (changeType)
            {
                case SchemaChangeType.RelationRemoved:
                    return 0;
                case SchemaChangeType.FieldRemoved:
                    return 1;
                case SchemaChangeType.ObjectRemoved:
                    return 2;
                case SchemaChangeType.ObjectAdded:
                    return 3;
                case SchemaChangeType.FieldAdded:
                    return 4;
                case SchemaChangeType.RelationAdded:
                    return 5;
                default:
                    return 6;
            }
        }

        /// <summary>XMZADD 20260901 按完整父子端点建立一次关系索引并输出新增删除，避免父子表重复保存导致重复事件。</summary>
        private static void CompareRelations(
            SchemaDiffResult result,
            string scopeKey,
            SnapshotData previous,
            SnapshotData current,
            DateTime detectedAt)
        {
            IDictionary<string, RelationMetadata> previousRelations = BuildRelationLookup(previous);
            IDictionary<string, RelationMetadata> currentRelations = BuildRelationLookup(current);
            foreach (KeyValuePair<string, RelationMetadata> pair in currentRelations)
            {
                if (!previousRelations.ContainsKey(pair.Key))
                {
                    result.Changes.Add(CreateRelationChange(scopeKey, pair.Value, SchemaChangeType.RelationAdded, detectedAt));
                }
            }
            foreach (KeyValuePair<string, RelationMetadata> pair in previousRelations)
            {
                if (!currentRelations.ContainsKey(pair.Key))
                {
                    result.Changes.Add(CreateRelationChange(scopeKey, pair.Value, SchemaChangeType.RelationRemoved, detectedAt));
                }
            }
        }

        /// <summary>XMZADD 20260901 将快照全部关系按完整端点去重，供线性差异比较复用。</summary>
        private static IDictionary<string, RelationMetadata> BuildRelationLookup(SnapshotData snapshot)
        {
            var result = new Dictionary<string, RelationMetadata>(StringComparer.OrdinalIgnoreCase);
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
                    if (relation != null)
                    {
                        result[BuildRelationKey(relation)] = relation;
                    }
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260901 创建带类型化端点载荷的关系差异，使发布流程无需解析自由文本。</summary>
        private static SchemaChange CreateRelationChange(
            string scopeKey,
            RelationMetadata relation,
            SchemaChangeType changeType,
            DateTime detectedAt)
        {
            return new SchemaChange
            {
                ScopeKey = scopeKey,
                SchemaName = relation.ChildSchemaName ?? string.Empty,
                ObjectName = relation.ChildTableName ?? string.Empty,
                FieldName = string.Empty,
                ChangeType = changeType,
                Description = changeType == SchemaChangeType.RelationAdded ? "发现新增关系" : "发现已删除关系",
                DetectedAt = detectedAt,
                RelationPayload = new RelationStructurePayload
                {
                    ForeignKeyName = relation.ForeignKeyName,
                    ParentSchemaName = relation.ParentSchemaName,
                    ParentTableName = relation.ParentTableName,
                    ParentFieldName = relation.ParentFieldName,
                    ChildSchemaName = relation.ChildSchemaName,
                    ChildTableName = relation.ChildTableName,
                    ChildFieldName = relation.ChildFieldName
                }
            };
        }

        /// <summary>XMZADD 20260901 用外键名和完整父子端点形成关系稳定身份。</summary>
        private static string BuildRelationKey(RelationMetadata relation)
        {
            return Normalize(relation.ForeignKeyName) + "\u001f" +
                Normalize(relation.ParentSchemaName) + "\u001f" + Normalize(relation.ParentTableName) + "\u001f" +
                Normalize(relation.ParentFieldName) + "\u001f" + Normalize(relation.ChildSchemaName) + "\u001f" +
                Normalize(relation.ChildTableName) + "\u001f" + Normalize(relation.ChildFieldName);
        }

        /// <summary>XMZADD 20260831 将快照中的对象按架构和对象名建立忽略大小写的检索索引。</summary>
        private static IDictionary<string, TableMetadata> BuildTableLookup(SnapshotData snapshot)
        {
            var tables = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            if (snapshot == null || snapshot.Tables == null)
            {
                return tables;
            }

            for (int i = 0; i < snapshot.Tables.Count; i++)
            {
                TableMetadata table = snapshot.Tables[i];
                if (table == null || string.IsNullOrEmpty(table.ObjectName))
                {
                    continue;
                }

                tables[BuildTableKey(table)] = table;
            }

            return tables;
        }

        /// <summary>XMZADD 20260831 对比同名对象的对象类型和字段结构，避免把大小写差异误判为结构变更。</summary>
        private static void CompareExistingTable(SchemaDiffResult result, string scopeKey, TableMetadata previous, TableMetadata current, DateTime detectedAt)
        {
            string previousObjectType = Normalize(previous.ObjectType);
            string currentObjectType = Normalize(current.ObjectType);
            if (!string.Equals(previousObjectType, currentObjectType, StringComparison.OrdinalIgnoreCase))
            {
                result.Changes.Add(CreateObjectChange(scopeKey, current, SchemaChangeType.ObjectChanged, DescribeObject(previous), DescribeObject(current), detectedAt));
            }

            IDictionary<string, FieldMetadata> previousFields = BuildFieldLookup(previous);
            IDictionary<string, FieldMetadata> currentFields = BuildFieldLookup(current);
            foreach (KeyValuePair<string, FieldMetadata> currentPair in currentFields)
            {
                FieldMetadata previousField;
                if (!previousFields.TryGetValue(currentPair.Key, out previousField))
                {
                    // 新字段没有历史人工确认内容，应被后续 AI 推测队列捕获。
                    result.Changes.Add(CreateFieldChange(scopeKey, current, null, currentPair.Value, SchemaChangeType.FieldAdded, detectedAt));
                    continue;
                }

                if (IsFieldStructureChanged(previousField, currentPair.Value))
                {
                    result.Changes.Add(CreateFieldChange(scopeKey, current, previousField, currentPair.Value, SchemaChangeType.FieldChanged, detectedAt));
                }
            }

            foreach (KeyValuePair<string, FieldMetadata> previousPair in previousFields)
            {
                if (currentFields.ContainsKey(previousPair.Key))
                {
                    continue;
                }

                result.Changes.Add(CreateFieldChange(scopeKey, previous, previousPair.Value, null, SchemaChangeType.FieldRemoved, detectedAt));
            }
        }

        /// <summary>XMZADD 20260831 为指定对象的所有字段生成同一种新增或删除结构差异。</summary>
        private static void AddAllFieldChanges(SchemaDiffResult result, string scopeKey, TableMetadata table, SchemaChangeType changeType, DateTime detectedAt)
        {
            IDictionary<string, FieldMetadata> fields = BuildFieldLookup(table);
            foreach (KeyValuePair<string, FieldMetadata> fieldPair in fields)
            {
                if (changeType == SchemaChangeType.FieldAdded)
                {
                    result.Changes.Add(CreateFieldChange(scopeKey, table, null, fieldPair.Value, changeType, detectedAt));
                }
                else
                {
                    result.Changes.Add(CreateFieldChange(scopeKey, table, fieldPair.Value, null, changeType, detectedAt));
                }
            }
        }

        /// <summary>XMZADD 20260831 将对象字段按字段名建立忽略大小写的检索索引。</summary>
        private static IDictionary<string, FieldMetadata> BuildFieldLookup(TableMetadata table)
        {
            var fields = new Dictionary<string, FieldMetadata>(StringComparer.OrdinalIgnoreCase);
            if (table == null || table.Fields == null)
            {
                return fields;
            }

            for (int i = 0; i < table.Fields.Count; i++)
            {
                FieldMetadata field = table.Fields[i];
                if (field == null || string.IsNullOrEmpty(field.FieldName))
                {
                    continue;
                }

                fields[field.FieldName] = field;
            }

            return fields;
        }

        /// <summary>XMZADD 20260831 判断字段的业务可用物理约束是否变化。</summary>
        private static bool IsFieldStructureChanged(FieldMetadata previous, FieldMetadata current)
        {
            return !EqualsIgnoreCase(previous.DataType, current.DataType)
                || !EqualsIgnoreCase(previous.LengthText, current.LengthText)
                || previous.IsRequired != current.IsRequired
                || previous.IsPrimaryKey != current.IsPrimaryKey
                || previous.IsForeignKey != current.IsForeignKey;
        }

        /// <summary>XMZADD 20260831 创建对象级结构变化记录，使后续字典处理可准确定位业务对象。</summary>
        private static SchemaChange CreateObjectChange(string scopeKey, TableMetadata table, SchemaChangeType changeType, string previousValue, string currentValue, DateTime detectedAt)
        {
            return new SchemaChange
            {
                ScopeKey = scopeKey,
                SchemaName = table == null ? string.Empty : table.SchemaName ?? string.Empty,
                ObjectName = table == null ? string.Empty : table.ObjectName ?? string.Empty,
                FieldName = string.Empty,
                ChangeType = changeType,
                PreviousValue = previousValue,
                CurrentValue = currentValue,
                Description = DescribeChange(changeType),
                DetectedAt = detectedAt
            };
        }

        /// <summary>XMZADD 20260831 创建字段级结构变化记录，使新增字段可直接进入自动解释流程。</summary>
        private static SchemaChange CreateFieldChange(string scopeKey, TableMetadata table, FieldMetadata previous, FieldMetadata current, SchemaChangeType changeType, DateTime detectedAt)
        {
            FieldMetadata field = current ?? previous;
            return new SchemaChange
            {
                ScopeKey = scopeKey,
                SchemaName = table == null ? string.Empty : table.SchemaName ?? string.Empty,
                ObjectName = table == null ? string.Empty : table.ObjectName ?? string.Empty,
                FieldName = field == null ? string.Empty : field.FieldName ?? string.Empty,
                ChangeType = changeType,
                PreviousValue = DescribeField(previous),
                CurrentValue = DescribeField(current),
                Description = DescribeChange(changeType),
                DetectedAt = detectedAt
            };
        }

        /// <summary>XMZADD 20260831 生成稳定的对象键，确保数据库标识符大小写差异不产生重复对象。</summary>
        private static string BuildTableKey(TableMetadata table)
        {
            return Normalize(table.SchemaName) + "\u001f" + Normalize(table.ObjectName);
        }

        /// <summary>XMZADD 20260831 将对象的物理类型转换成变化记录可读的前后说明。</summary>
        private static string DescribeObject(TableMetadata table)
        {
            if (table == null)
            {
                return null;
            }

            return "对象类型=" + Normalize(table.ObjectType);
        }

        /// <summary>XMZADD 20260831 将字段物理约束转换成可审计的前后说明。</summary>
        private static string DescribeField(FieldMetadata field)
        {
            if (field == null)
            {
                return null;
            }

            return "类型=" + Normalize(field.DataType)
                + "；长度=" + Normalize(field.LengthText)
                + "；必填=" + field.IsRequired.ToString()
                + "；主键=" + field.IsPrimaryKey.ToString()
                + "；外键=" + field.IsForeignKey.ToString();
        }

        /// <summary>XMZADD 20260831 说明结构变化类型，便于界面和自动任务使用统一业务文案。</summary>
        private static string DescribeChange(SchemaChangeType changeType)
        {
            switch (changeType)
            {
                case SchemaChangeType.ObjectAdded:
                    return "发现新增对象";
                case SchemaChangeType.ObjectRemoved:
                    return "发现已删除对象";
                case SchemaChangeType.ObjectChanged:
                    return "发现对象属性变更";
                case SchemaChangeType.FieldAdded:
                    return "发现新增字段";
                case SchemaChangeType.FieldRemoved:
                    return "发现已删除字段";
                case SchemaChangeType.FieldChanged:
                    return "发现字段物理属性变更";
                default:
                    return "发现结构变更";
            }
        }

        /// <summary>XMZADD 20260831 规范化空值，避免空值与空字符串被重复识别为结构变化。</summary>
        private static string Normalize(string value)
        {
            return value == null ? string.Empty : value.Trim();
        }

        /// <summary>XMZADD 20260831 按忽略大小写的规则比较物理标识信息。</summary>
        private static bool EqualsIgnoreCase(string previous, string current)
        {
            return string.Equals(Normalize(previous), Normalize(current), StringComparison.OrdinalIgnoreCase);
        }
    }
}
