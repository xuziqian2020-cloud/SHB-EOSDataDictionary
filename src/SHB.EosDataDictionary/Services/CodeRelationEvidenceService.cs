using System;
using System.Collections.Generic;
using System.IO;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260916 交叉核对物理外键、EOS 实体对象属性和业务 SQL JOIN 后发布可解释关联。</summary>
    public sealed class CodeRelationEvidenceService
    {
        private const int MaximumEvidenceCount = 24;

        /// <summary>XMZADD 20260916 按稳定父子端点键合并关系证据，并保护数据库真实外键不被代码推测降级。</summary>
        public void Apply(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }

            var tableByName = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            var tableByEntity = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            var knownKeyEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BuildTableIndexes(snapshot, tableByName, tableByEntity, knownKeyEndpoints);

            var existingRelations = new Dictionary<string, List<RelationMetadata>>(StringComparer.OrdinalIgnoreCase);
            BuildExistingRelationIndex(snapshot, tableByName, existingRelations, knownKeyEndpoints);

            var candidates = new Dictionary<string, RelationCandidate>(StringComparer.OrdinalIgnoreCase);
            var entityDirections = new Dictionary<string, RelationCandidate>(StringComparer.OrdinalIgnoreCase);
            CollectEntityEvidence(sourceEvidence, tableByName, tableByEntity, knownKeyEndpoints,
                candidates, entityDirections);
            CollectJoinEvidence(sourceEvidence, tableByName, knownKeyEndpoints, candidates, entityDirections);
            ApplyCandidates(candidates, existingRelations);
        }

        /// <summary>XMZADD 20260916 建立唯一表名、唯一实体名和已知主键端点索引，拒绝跨架构歧义映射。</summary>
        private static void BuildTableIndexes(SnapshotData snapshot,
            IDictionary<string, TableMetadata> tableByName,
            IDictionary<string, TableMetadata> tableByEntity,
            ISet<string> knownKeyEndpoints)
        {
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                EnsureMutableRelations(table);
                AddUniqueTable(tableByName, table.ObjectName, table);
                if (table.EntityName != null)
                {
                    AddUniqueTable(tableByEntity, table.EntityName.Value, table);
                }
                for (int fieldIndex = 0; table.Fields != null && fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field != null && field.IsPrimaryKey)
                    {
                        knownKeyEndpoints.Add(BuildEndpointKey(table.SchemaName, table.ObjectName, field.FieldName));
                    }
                }
            }
        }

        /// <summary>XMZADD 20260916 将重复表名或实体名标记为不可唯一解析，防止关系跨 schema 串表。</summary>
        private static void AddUniqueTable(IDictionary<string, TableMetadata> index, string key,
            TableMetadata table)
        {
            string value = (key ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return;
            }
            if (index.ContainsKey(value))
            {
                index[value] = null;
            }
            else
            {
                index.Add(value, table);
            }
        }

        /// <summary>XMZADD 20260916 索引已有完整关系键，并把物理外键的父端加入已知唯一键集合。</summary>
        private static void BuildExistingRelationIndex(SnapshotData snapshot,
            IDictionary<string, TableMetadata> tableByName,
            IDictionary<string, List<RelationMetadata>> existingRelations,
            ISet<string> knownKeyEndpoints)
        {
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                for (int relationIndex = 0; table != null && table.Relations != null &&
                    relationIndex < table.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = table.Relations[relationIndex];
                    if (relation == null)
                    {
                        continue;
                    }
                    string key = BuildRelationKey(relation, tableByName);
                    List<RelationMetadata> matches;
                    if (!existingRelations.TryGetValue(key, out matches))
                    {
                        matches = new List<RelationMetadata>();
                        existingRelations.Add(key, matches);
                    }
                    if (!ContainsReference(matches, relation))
                    {
                        matches.Add(relation);
                    }
                    if (relation.RelationType != null &&
                        relation.RelationType.Status == ConfidenceStatus.DatabaseEvidence)
                    {
                        knownKeyEndpoints.Add(BuildEndpointKey(
                            ResolveSchema(relation.ParentSchemaName, relation.ParentTableName, tableByName),
                            relation.ParentTableName, relation.ParentFieldName));
                    }
                }
            }
        }

        /// <summary>XMZADD 20260916 收集实体 obj 属性确定的父子方向，但暂不因单一生成代码证据提升为正式关系。</summary>
        private static void CollectEntityEvidence(IList<SourceEvidence> sourceEvidence,
            IDictionary<string, TableMetadata> tableByName,
            IDictionary<string, TableMetadata> tableByEntity,
            ISet<string> knownKeyEndpoints,
            IDictionary<string, RelationCandidate> candidates,
            IDictionary<string, RelationCandidate> entityDirections)
        {
            for (int evidenceIndex = 0; sourceEvidence != null && evidenceIndex < sourceEvidence.Count;
                evidenceIndex++)
            {
                SourceEvidence source = sourceEvidence[evidenceIndex];
                if (source == null || source.Evidence == null ||
                    !string.Equals(source.Evidence.RuleName, "EntityObjectRelation", StringComparison.Ordinal) ||
                    !IsSimpleIdentifier(source.ObjectName) ||
                    !IsSimpleIdentifier(source.RelationFieldName) ||
                    !IsSimpleIdentifier(source.RelationTargetEntity))
                {
                    continue;
                }

                TableMetadata child;
                TableMetadata parent;
                if (!TryGetUniqueTable(tableByName, source.ObjectName, out child) ||
                    !TryGetUniqueTable(tableByEntity, source.RelationTargetEntity, out parent) ||
                    child == null || parent == null)
                {
                    continue;
                }
                FieldMetadata childField = FindField(child, source.RelationFieldName);
                FieldMetadata parentField = FindParentKeyField(parent,
                    source.RelationTargetFieldName ?? source.RelationFieldName, knownKeyEndpoints);
                if (childField == null || parentField == null)
                {
                    continue;
                }

                RelationCandidate candidate = GetOrCreateCandidate(
                    parent, parentField, child, childField, candidates);
                candidate.AddEntityEvidence(CreatePublishedEvidence(source,
                    "EOS 实体对象属性确认子表到父实体的对象关系。"));
                string undirectedKey = BuildUndirectedKey(parent, parentField, child, childField);
                RelationCandidate existingDirection;
                if (entityDirections.TryGetValue(undirectedKey, out existingDirection) &&
                    existingDirection != null && !string.Equals(existingDirection.RelationKey,
                        candidate.RelationKey, StringComparison.OrdinalIgnoreCase))
                {
                    // 两个实体方向互相冲突时不允许它们替 SQL 决定父子方向。
                    entityDirections[undirectedKey] = null;
                }
                else if (!entityDirections.ContainsKey(undirectedKey))
                {
                    entityDirections.Add(undirectedKey, candidate);
                }
            }
        }

        /// <summary>XMZADD 20260916 收集可定位到真实表字段的业务 JOIN，并按主键端而非 SQL 书写左右确定方向。</summary>
        private static void CollectJoinEvidence(IList<SourceEvidence> sourceEvidence,
            IDictionary<string, TableMetadata> tableByName,
            ISet<string> knownKeyEndpoints,
            IDictionary<string, RelationCandidate> candidates,
            IDictionary<string, RelationCandidate> entityDirections)
        {
            for (int evidenceIndex = 0; sourceEvidence != null && evidenceIndex < sourceEvidence.Count;
                evidenceIndex++)
            {
                SourceEvidence source = sourceEvidence[evidenceIndex];
                if (!IsBusinessJoinEvidence(source) || !IsSimpleIdentifier(source.ObjectName) ||
                    !IsSimpleIdentifier(source.FieldName) ||
                    !IsSimpleIdentifier(source.RelationTargetObjectName) ||
                    !IsSimpleIdentifier(source.RelationTargetFieldName))
                {
                    continue;
                }

                TableMetadata sourceTable;
                TableMetadata targetTable;
                if (!TryGetUniqueTable(tableByName, source.ObjectName, out sourceTable) ||
                    !TryGetUniqueTable(tableByName, source.RelationTargetObjectName, out targetTable))
                {
                    continue;
                }
                FieldMetadata sourceField = FindField(sourceTable, source.FieldName);
                FieldMetadata targetField = FindField(targetTable, source.RelationTargetFieldName);
                if (sourceField == null || targetField == null ||
                    (ReferenceEquals(sourceTable, targetTable) && ReferenceEquals(sourceField, targetField)))
                {
                    continue;
                }

                bool sourceIsKey = IsKnownKey(sourceTable, sourceField, knownKeyEndpoints);
                bool targetIsKey = IsKnownKey(targetTable, targetField, knownKeyEndpoints);
                RelationCandidate candidate = null;
                if (sourceIsKey && !targetIsKey)
                {
                    candidate = GetOrCreateCandidate(sourceTable, sourceField, targetTable, targetField, candidates);
                }
                else if (targetIsKey && !sourceIsKey)
                {
                    candidate = GetOrCreateCandidate(targetTable, targetField, sourceTable, sourceField, candidates);
                }
                else
                {
                    // 主键无法唯一决定方向时，只允许已解析的实体对象关系补充方向，不能照搬 SQL 左右顺序。
                    string undirectedKey = BuildUndirectedKey(sourceTable, sourceField, targetTable, targetField);
                    entityDirections.TryGetValue(undirectedKey, out candidate);
                }
                if (candidate == null)
                {
                    continue;
                }
                candidate.AddJoinEvidence(GetIndependentSourcePath(source), CreatePublishedEvidence(source,
                    "业务 SQL 等值 JOIN 确认两个物理字段参与同一关系。"));
            }
        }

        /// <summary>XMZADD 20260916 判断 JOIN 是否来自真实业务源码而非生成实体、Designer 或配置镜像。</summary>
        private static bool IsBusinessJoinEvidence(SourceEvidence source)
        {
            if (source == null || source.Evidence == null ||
                !string.Equals(source.Evidence.RuleName, "SqlFieldRelation", StringComparison.Ordinal))
            {
                return false;
            }
            if (source.Origin == SourceEvidenceOrigin.BusinessCode)
            {
                return true;
            }
            if (source.Origin != SourceEvidenceOrigin.Unknown)
            {
                return false;
            }
            string path = (source.Evidence.SourcePath ?? string.Empty).Replace('\\', '/');
            string sourceType = source.Evidence.SourceType ?? string.Empty;
            return path.IndexOf(".Designer.", StringComparison.OrdinalIgnoreCase) < 0 &&
                   path.IndexOf("表-类定义", StringComparison.OrdinalIgnoreCase) < 0 &&
                   (sourceType.IndexOf("业务源码", StringComparison.Ordinal) >= 0 ||
                    string.Equals(sourceType, "EOS源码", StringComparison.Ordinal));
        }

        /// <summary>XMZADD 20260916 按稳定键依次应用候选，保证重复扫描和证据顺序不会改变快照结果。</summary>
        private static void ApplyCandidates(IDictionary<string, RelationCandidate> candidates,
            IDictionary<string, List<RelationMetadata>> existingRelations)
        {
            var keys = new List<string>();
            foreach (KeyValuePair<string, RelationCandidate> pair in candidates)
            {
                if (pair.Value != null && pair.Value.HasEvidence)
                {
                    keys.Add(pair.Key);
                }
            }
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int keyIndex = 0; keyIndex < keys.Count; keyIndex++)
            {
                string key = keys[keyIndex];
                RelationCandidate candidate = candidates[key];
                RelationMetadata inferred = RelationInferenceService.InferCodeRelation(
                    candidate.CreateInput(), candidate.CreateEvidence(), candidate.HasEntityEvidence,
                    candidate.IndependentJoinFileCount);
                inferred.ScopeKey = candidate.ChildTable.ScopeKey;
                inferred.ForeignKeyName = (inferred.RelationType.Status == ConfidenceStatus.CodeEvidence
                    ? "代码_" : "参考_") + candidate.ChildTable.ObjectName + "_" + candidate.ChildField.FieldName;

                List<RelationMetadata> existing;
                if (existingRelations.TryGetValue(key, out existing) && existing.Count > 0)
                {
                    MergeWithExisting(existing, inferred);
                }
                else
                {
                    candidate.ChildTable.Relations.Add(inferred);
                    if (!ReferenceEquals(candidate.ChildTable, candidate.ParentTable))
                    {
                        candidate.ParentTable.Relations.Add(inferred);
                    }
                    existingRelations.Add(key, new List<RelationMetadata> { inferred });
                }
                ApplyFieldSummary(candidate, inferred);
            }
        }

        /// <summary>XMZADD 20260916 将代码证据合并到已有关系，物理外键和人工确认关系只补证据不降级。</summary>
        private static void MergeWithExisting(IList<RelationMetadata> existing, RelationMetadata inferred)
        {
            bool hasPhysicalRelation = false;
            for (int index = 0; index < existing.Count; index++)
            {
                RelationMetadata relation = existing[index];
                if (relation != null && relation.RelationType != null &&
                    relation.RelationType.Status == ConfidenceStatus.DatabaseEvidence)
                {
                    hasPhysicalRelation = true;
                    AppendEvidence(relation.RelationType, inferred.RelationType.Evidence);
                }
            }
            if (hasPhysicalRelation)
            {
                return;
            }

            for (int index = 0; index < existing.Count; index++)
            {
                RelationMetadata relation = existing[index];
                if (relation == null)
                {
                    continue;
                }
                if (IsProtectedRelationType(relation.RelationType) ||
                    (relation.RelationType != null && relation.RelationType.Status == ConfidenceStatus.CodeEvidence &&
                     inferred.RelationType.Status == ConfidenceStatus.Guessed))
                {
                    AppendEvidence(relation.RelationType, inferred.RelationType.Evidence);
                    continue;
                }
                IList<EvidenceItem> previousEvidence = relation.RelationType == null
                    ? null
                    : relation.RelationType.Evidence;
                relation.RelationType = CloneMetadataValue(inferred.RelationType);
                AppendEvidence(relation.RelationType, previousEvidence);
                relation.ForeignKeyName = inferred.ForeignKeyName;
            }
        }

        /// <summary>XMZADD 20260916 把关系置信结论同步到子字段，保护数据库、人工和已确认说明。</summary>
        private static void ApplyFieldSummary(RelationCandidate candidate, RelationMetadata inferred)
        {
            MetadataValue existing = candidate.ChildField.RelationSummary;
            string desiredValue = (inferred.RelationType.Status == ConfidenceStatus.CodeEvidence
                ? "代码关联：" : "参考关联：") + candidate.ParentTable.ObjectName + "." +
                    candidate.ParentField.FieldName;
            if (candidate.ChildField.IsForeignKey || IsProtectedRelationType(existing) ||
                (existing != null && existing.Status == ConfidenceStatus.CodeEvidence &&
                 inferred.RelationType.Status == ConfidenceStatus.Guessed))
            {
                return;
            }
            if (existing != null && existing.Status == ConfidenceStatus.GuessedConflict)
            {
                AppendEvidence(existing, inferred.RelationType.Evidence);
                return;
            }
            bool sameEvidenceLevel = existing != null &&
                existing.Status == inferred.RelationType.Status &&
                (existing.Status == ConfidenceStatus.Guessed ||
                 existing.Status == ConfidenceStatus.CodeEvidence);
            if (sameEvidenceLevel && !string.Equals(existing.Value, desiredValue, StringComparison.Ordinal))
            {
                existing.Value = "关联冲突：存在多个可能父表";
                existing.Status = ConfidenceStatus.GuessedConflict;
                existing.ConfidenceScore = 0;
                existing.SourceType = "EOS代码关系冲突";
                existing.SourceSummary = "同一子字段存在多个不同父端点，需人工确认";
                AppendEvidence(existing, inferred.RelationType.Evidence);
                return;
            }
            candidate.ChildField.RelationSummary = new MetadataValue
            {
                Value = desiredValue,
                Status = inferred.RelationType.Status,
                ConfidenceScore = inferred.RelationType.ConfidenceScore,
                SourceType = inferred.RelationType.SourceType,
                SourceSummary = inferred.RelationType.SourceSummary,
                Evidence = CopyEvidence(inferred.RelationType.Evidence)
            };
        }

        /// <summary>XMZADD 20260916 判断关系说明是否由数据库、人工或确认结论保护。</summary>
        private static bool IsProtectedRelationType(MetadataValue value)
        {
            return value != null && (value.IsManualOverride || value.IsLocked ||
                   value.Status == ConfidenceStatus.LocalOverride ||
                   value.Status == ConfidenceStatus.Confirmed ||
                   value.Status == ConfidenceStatus.DatabaseEvidence);
        }

        /// <summary>XMZADD 20260916 获取或创建同一完整父子端点的关系候选，避免仅按子字段错误合并。</summary>
        private static RelationCandidate GetOrCreateCandidate(TableMetadata parentTable,
            FieldMetadata parentField, TableMetadata childTable, FieldMetadata childField,
            IDictionary<string, RelationCandidate> candidates)
        {
            string key = BuildRelationKey(parentTable.SchemaName, parentTable.ObjectName, parentField.FieldName,
                childTable.SchemaName, childTable.ObjectName, childField.FieldName);
            RelationCandidate candidate;
            if (!candidates.TryGetValue(key, out candidate))
            {
                candidate = new RelationCandidate(key, parentTable, parentField, childTable, childField);
                candidates.Add(key, candidate);
            }
            return candidate;
        }

        /// <summary>XMZADD 20260916 查找实体目标的真实主键或物理外键已证明的唯一父端字段。</summary>
        private static FieldMetadata FindParentKeyField(TableMetadata table, string preferredFieldName,
            ISet<string> knownKeyEndpoints)
        {
            FieldMetadata preferred = FindField(table, preferredFieldName);
            if (preferred != null && IsKnownKey(table, preferred, knownKeyEndpoints))
            {
                return preferred;
            }
            for (int fieldIndex = 0; table != null && table.Fields != null && fieldIndex < table.Fields.Count;
                fieldIndex++)
            {
                FieldMetadata field = table.Fields[fieldIndex];
                if (field != null && field.IsPrimaryKey)
                {
                    return field;
                }
            }
            for (int fieldIndex = 0; table != null && table.Fields != null && fieldIndex < table.Fields.Count;
                fieldIndex++)
            {
                FieldMetadata field = table.Fields[fieldIndex];
                if (field != null && IsKnownKey(table, field, knownKeyEndpoints))
                {
                    return field;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260916 按大小写不敏感字段名查找真实快照字段。</summary>
        private static FieldMetadata FindField(TableMetadata table, string fieldName)
        {
            for (int index = 0; table != null && table.Fields != null && index < table.Fields.Count; index++)
            {
                FieldMetadata field = table.Fields[index];
                if (field != null && string.Equals(field.FieldName, fieldName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260916 判断字段是否为快照主键或数据库物理外键已证明的唯一父端。</summary>
        private static bool IsKnownKey(TableMetadata table, FieldMetadata field,
            ISet<string> knownKeyEndpoints)
        {
            return field != null && (field.IsPrimaryKey || knownKeyEndpoints.Contains(
                BuildEndpointKey(table == null ? null : table.SchemaName,
                    table == null ? null : table.ObjectName, field.FieldName)));
        }

        /// <summary>XMZADD 20260916 读取唯一表索引并拒绝已标记歧义的空映射。</summary>
        private static bool TryGetUniqueTable(IDictionary<string, TableMetadata> index, string key,
            out TableMetadata table)
        {
            table = null;
            return index != null && !string.IsNullOrWhiteSpace(key) &&
                   index.TryGetValue(key, out table) && table != null;
        }

        /// <summary>XMZADD 20260916 判断关系端点仍为单一安全标识符，排除临时对象、表变量和表达式。</summary>
        private static bool IsSimpleIdentifier(string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_'))
            {
                return false;
            }
            for (int index = 1; index < text.Length; index++)
            {
                if (!(char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260916 生成不受 JOIN 左右顺序影响的无向端点键，用于查找实体关系方向。</summary>
        private static string BuildUndirectedKey(TableMetadata firstTable, FieldMetadata firstField,
            TableMetadata secondTable, FieldMetadata secondField)
        {
            string first = BuildEndpointKey(firstTable == null ? null : firstTable.SchemaName,
                firstTable == null ? null : firstTable.ObjectName, firstField == null ? null : firstField.FieldName);
            string second = BuildEndpointKey(secondTable == null ? null : secondTable.SchemaName,
                secondTable == null ? null : secondTable.ObjectName, secondField == null ? null : secondField.FieldName);
            return string.Compare(first, second, StringComparison.OrdinalIgnoreCase) <= 0
                ? first + "<>" + second
                : second + "<>" + first;
        }

        /// <summary>XMZADD 20260916 生成包含 schema、表和字段的单端点稳定键。</summary>
        private static string BuildEndpointKey(string schemaName, string tableName, string fieldName)
        {
            return string.Concat(schemaName ?? string.Empty, "|", tableName ?? string.Empty, "|",
                fieldName ?? string.Empty).ToUpperInvariant();
        }

        /// <summary>XMZADD 20260916 生成完整父子端点稳定键，避免同一子字段的不同目标关系互相覆盖。</summary>
        private static string BuildRelationKey(string parentSchemaName, string parentTableName,
            string parentFieldName, string childSchemaName, string childTableName, string childFieldName)
        {
            return string.Concat(
                parentSchemaName ?? string.Empty, "|", parentTableName ?? string.Empty, "|",
                parentFieldName ?? string.Empty, "|", childSchemaName ?? string.Empty, "|",
                childTableName ?? string.Empty, "|", childFieldName ?? string.Empty).ToUpperInvariant();
        }

        /// <summary>XMZADD 20260916 以唯一表的实际 schema 补齐旧关系端点后生成完整稳定键。</summary>
        private static string BuildRelationKey(RelationMetadata relation,
            IDictionary<string, TableMetadata> tableByName)
        {
            return BuildRelationKey(
                ResolveSchema(relation == null ? null : relation.ParentSchemaName,
                    relation == null ? null : relation.ParentTableName, tableByName),
                relation == null ? null : relation.ParentTableName,
                relation == null ? null : relation.ParentFieldName,
                ResolveSchema(relation == null ? null : relation.ChildSchemaName,
                    relation == null ? null : relation.ChildTableName, tableByName),
                relation == null ? null : relation.ChildTableName,
                relation == null ? null : relation.ChildFieldName);
        }

        /// <summary>XMZADD 20260916 在旧关系缺少 schema 时使用唯一物理表补齐比较上下文。</summary>
        private static string ResolveSchema(string schemaName, string tableName,
            IDictionary<string, TableMetadata> tableByName)
        {
            if (!string.IsNullOrWhiteSpace(schemaName))
            {
                return schemaName;
            }
            TableMetadata table;
            return TryGetUniqueTable(tableByName, tableName, out table) ? table.SchemaName : string.Empty;
        }

        /// <summary>XMZADD 20260916 判断列表是否已保存同一关系实例。</summary>
        private static bool ContainsReference(IList<RelationMetadata> relations, RelationMetadata target)
        {
            for (int index = 0; relations != null && index < relations.Count; index++)
            {
                if (ReferenceEquals(relations[index], target))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260916 将关系集合恢复为可追加列表以兼容旧快照数组。</summary>
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

        /// <summary>XMZADD 20260916 获取用于独立来源计数的规范文件路径，同一文件的双向端点只计一次。</summary>
        private static string GetIndependentSourcePath(SourceEvidence source)
        {
            string path = source == null || source.Evidence == null
                ? string.Empty
                : source.Evidence.SourcePath ?? string.Empty;
            path = path.Trim().Replace('\\', '/');
            return path.Length == 0 ? "未定位来源" : path;
        }

        /// <summary>XMZADD 20260916 复制公开关系证据并移除本机绝对路径、SQL 正文和可能含凭据的原始值。</summary>
        private static EvidenceItem CreatePublishedEvidence(SourceEvidence source, string explanation)
        {
            EvidenceItem evidence = source == null ? null : source.Evidence;
            string path = evidence == null ? string.Empty : evidence.SourcePath ?? string.Empty;
            if (Path.IsPathRooted(path) || path.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                path = Path.GetFileName(path);
            }
            string endpoint = source == null
                ? string.Empty
                : (source.ObjectName ?? string.Empty) + "." +
                  (source.FieldName ?? source.RelationFieldName ?? string.Empty) + " -> " +
                  (source.RelationTargetObjectName ?? source.RelationTargetEntity ?? string.Empty) + "." +
                  (source.RelationTargetFieldName ?? string.Empty);
            return new EvidenceItem
            {
                SourceType = evidence == null ? "EOS源码" : evidence.SourceType,
                SourcePath = path.Replace('\\', '/'),
                SourceLine = evidence == null ? 0 : evidence.SourceLine,
                RuleName = evidence == null ? "CodeRelation" : evidence.RuleName,
                RawValue = endpoint,
                Explanation = explanation
            };
        }

        /// <summary>XMZADD 20260916 合并有限且不重复的关系证据，避免多轮富化无限扩张快照。</summary>
        private static void AppendEvidence(MetadataValue target, IList<EvidenceItem> additions)
        {
            if (target == null)
            {
                return;
            }
            if (target.Evidence == null || target.Evidence.IsReadOnly)
            {
                target.Evidence = CopyEvidence(target.Evidence);
            }
            for (int index = 0; additions != null && index < additions.Count &&
                target.Evidence.Count < MaximumEvidenceCount; index++)
            {
                EvidenceItem item = additions[index];
                if (item != null && !ContainsEvidence(target.Evidence, item))
                {
                    target.Evidence.Add(item);
                }
            }
        }

        /// <summary>XMZADD 20260916 按规则、路径和行号判断同一关系证据是否已存在。</summary>
        private static bool ContainsEvidence(IList<EvidenceItem> evidence, EvidenceItem target)
        {
            for (int index = 0; evidence != null && index < evidence.Count; index++)
            {
                EvidenceItem item = evidence[index];
                if (item != null && string.Equals(item.RuleName, target.RuleName, StringComparison.Ordinal) &&
                    string.Equals(item.SourcePath, target.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                    item.SourceLine == target.SourceLine)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260916 复制元数据值以避免多个历史关系共享可变证据集合。</summary>
        private static MetadataValue CloneMetadataValue(MetadataValue source)
        {
            return new MetadataValue
            {
                Value = source == null ? string.Empty : source.Value,
                Status = source == null ? ConfidenceStatus.PendingConfirmation : source.Status,
                ConfidenceScore = source == null ? 0 : source.ConfidenceScore,
                SourceType = source == null ? null : source.SourceType,
                SourceSummary = source == null ? null : source.SourceSummary,
                Evidence = CopyEvidence(source == null ? null : source.Evidence)
            };
        }

        /// <summary>XMZADD 20260916 复制关系证据列表并忽略空项。</summary>
        private static IList<EvidenceItem> CopyEvidence(IList<EvidenceItem> source)
        {
            var result = new List<EvidenceItem>();
            for (int index = 0; source != null && index < source.Count; index++)
            {
                if (source[index] != null)
                {
                    result.Add(source[index]);
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260916 聚合同一完整父子端点的实体证据与按文件去重的 JOIN 证据。</summary>
        private sealed class RelationCandidate
        {
            private readonly Dictionary<string, EvidenceItem> entityEvidence =
                new Dictionary<string, EvidenceItem>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, EvidenceItem> joinEvidenceByFile =
                new Dictionary<string, EvidenceItem>(StringComparer.OrdinalIgnoreCase);

            /// <summary>XMZADD 20260916 创建一个方向已由实体关系或真实键确定的关系候选。</summary>
            public RelationCandidate(string relationKey, TableMetadata parentTable, FieldMetadata parentField,
                TableMetadata childTable, FieldMetadata childField)
            {
                RelationKey = relationKey;
                ParentTable = parentTable;
                ParentField = parentField;
                ChildTable = childTable;
                ChildField = childField;
            }

            public string RelationKey { get; private set; }
            public TableMetadata ParentTable { get; private set; }
            public FieldMetadata ParentField { get; private set; }
            public TableMetadata ChildTable { get; private set; }
            public FieldMetadata ChildField { get; private set; }
            public bool HasEntityEvidence { get { return entityEvidence.Count > 0; } }
            public int IndependentJoinFileCount { get { return joinEvidenceByFile.Count; } }
            public bool HasEvidence { get { return HasEntityEvidence || IndependentJoinFileCount > 0; } }

            /// <summary>XMZADD 20260916 按位置去重实体对象属性证据。</summary>
            public void AddEntityEvidence(EvidenceItem evidence)
            {
                string key = BuildEvidenceKey(evidence);
                if (!entityEvidence.ContainsKey(key))
                {
                    entityEvidence.Add(key, evidence);
                }
            }

            /// <summary>XMZADD 20260916 每个业务文件仅保留一条 JOIN 证据以满足独立来源门槛。</summary>
            public void AddJoinEvidence(string sourcePath, EvidenceItem evidence)
            {
                if (!joinEvidenceByFile.ContainsKey(sourcePath))
                {
                    joinEvidenceByFile.Add(sourcePath, evidence);
                }
            }

            /// <summary>XMZADD 20260916 创建关系推理器所需的父子端点输入。</summary>
            public RelationInput CreateInput()
            {
                return new RelationInput
                {
                    ParentSchemaName = ParentTable.SchemaName,
                    ParentTableName = ParentTable.ObjectName,
                    ParentFieldName = ParentField.FieldName,
                    ChildSchemaName = ChildTable.SchemaName,
                    ChildTableName = ChildTable.ObjectName,
                    ChildFieldName = ChildField.FieldName
                };
            }

            /// <summary>XMZADD 20260916 按实体优先、文件路径稳定排序输出有限关系证据。</summary>
            public IList<EvidenceItem> CreateEvidence()
            {
                var result = new List<EvidenceItem>();
                AddSortedEvidence(entityEvidence, result);
                AddSortedEvidence(joinEvidenceByFile, result);
                return result;
            }

            /// <summary>XMZADD 20260916 将字典证据按键稳定追加到发布列表。</summary>
            private static void AddSortedEvidence(IDictionary<string, EvidenceItem> source,
                IList<EvidenceItem> target)
            {
                var keys = new List<string>();
                foreach (KeyValuePair<string, EvidenceItem> pair in source)
                {
                    keys.Add(pair.Key);
                }
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < keys.Count && target.Count < MaximumEvidenceCount; index++)
                {
                    target.Add(source[keys[index]]);
                }
            }

            /// <summary>XMZADD 20260916 生成实体证据的位置去重键。</summary>
            private static string BuildEvidenceKey(EvidenceItem evidence)
            {
                return string.Concat(evidence == null ? string.Empty : evidence.SourcePath, "|",
                    evidence == null ? 0 : evidence.SourceLine, "|",
                    evidence == null ? string.Empty : evidence.RuleName);
            }
        }
    }
}
