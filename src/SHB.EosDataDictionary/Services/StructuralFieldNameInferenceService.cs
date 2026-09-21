using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260917 依据真实主键、代码关系和唯一键签名补全可追溯的业务字段中文名。</summary>
    public sealed class StructuralFieldNameInferenceService
    {
        /// <summary>XMZADD 20260917 按主键、明确关系、逻辑关系和唯一键签名的证据强度依次生成名称候选。</summary>
        public void Apply(SnapshotData snapshot)
        {
            Apply(snapshot, null);
        }

        /// <summary>XMZADD 20260918 合并数据库约束和 EOS 生成实体主键声明，补全缺少物理主键约束的历史对象字段名。</summary>
        public void Apply(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }

            var nameLayer = new BusinessNameLayerService();
            ApplyPrimaryKeyNames(snapshot, nameLayer);
            var generatedEntityPrimaryKeys = new HashSet<FieldMetadata>();
            ApplyGeneratedEntityPrimaryKeyNames(snapshot, sourceEvidence, nameLayer,
                generatedEntityPrimaryKeys);

            var exactTableIndex = new Dictionary<string, List<TableMetadata>>(StringComparer.OrdinalIgnoreCase);
            var endpointTableIndex = new Dictionary<string, List<TableMetadata>>(StringComparer.OrdinalIgnoreCase);
            BuildTableIndexes(snapshot, exactTableIndex, endpointTableIndex);
            IDictionary<FieldMetadata, RelationNameBucket> relationBuckets = BuildRelationBuckets(
                snapshot, exactTableIndex, endpointTableIndex);
            ApplyRelationNames(relationBuckets, nameLayer);
            ApplyRelationSignatureReferences(snapshot, relationBuckets, nameLayer);
            ApplyUniquePrimaryKeySignatures(snapshot, relationBuckets, nameLayer,
                generatedEntityPrimaryKeys);
        }

        /// <summary>XMZADD 20260918 用生成实体 mIDCol 和可靠业务表名补全未建立数据库主键约束的对象标识字段。</summary>
        private static void ApplyGeneratedEntityPrimaryKeyNames(SnapshotData snapshot,
            IList<SourceEvidence> sourceEvidence, BusinessNameLayerService nameLayer,
            ISet<FieldMetadata> generatedEntityPrimaryKeys)
        {
            if (sourceEvidence == null || sourceEvidence.Count == 0)
            {
                return;
            }

            var tableIndex = new Dictionary<string, IList<TableMetadata>>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                if (table == null || string.IsNullOrWhiteSpace(table.ObjectName))
                {
                    continue;
                }
                IList<TableMetadata> tables;
                if (!tableIndex.TryGetValue(table.ObjectName, out tables))
                {
                    tables = new List<TableMetadata>();
                    tableIndex.Add(table.ObjectName, tables);
                }
                tables.Add(table);
            }

            var appliedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int evidenceIndex = 0; evidenceIndex < sourceEvidence.Count; evidenceIndex++)
            {
                SourceEvidence evidence = sourceEvidence[evidenceIndex];
                if (!IsGeneratedEntityPrimaryKeyEvidence(evidence))
                {
                    continue;
                }
                string evidenceKey = evidence.ObjectName + "\u001F" + evidence.FieldName;
                if (!appliedKeys.Add(evidenceKey))
                {
                    continue;
                }

                IList<TableMetadata> tables;
                if (!tableIndex.TryGetValue(evidence.ObjectName, out tables))
                {
                    continue;
                }
                for (int tableNumber = 0; tableNumber < tables.Count; tableNumber++)
                {
                    ApplyGeneratedEntityPrimaryKeyName(tables[tableNumber], evidence, nameLayer,
                        generatedEntityPrimaryKeys);
                }
            }
        }

        /// <summary>XMZADD 20260918 校验一条证据确实来自生成实体的权威 mIDCol 声明。</summary>
        private static bool IsGeneratedEntityPrimaryKeyEvidence(SourceEvidence evidence)
        {
            return evidence != null && evidence.Evidence != null &&
                   evidence.Origin == SourceEvidenceOrigin.GeneratedEntity &&
                   evidence.Strength == SourceEvidenceStrength.Authoritative &&
                   string.Equals(evidence.Evidence.RuleName, "EntityPrimaryKey", StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(evidence.ObjectName) &&
                   !string.IsNullOrWhiteSpace(evidence.FieldName);
        }

        /// <summary>XMZADD 20260918 将单个实体主键声明应用到同名快照表，并按表名层级决定正式名或参考名。</summary>
        private static void ApplyGeneratedEntityPrimaryKeyName(TableMetadata table,
            SourceEvidence evidence, BusinessNameLayerService nameLayer,
            ISet<FieldMetadata> generatedEntityPrimaryKeys)
        {
            FieldMetadata field = FindField(table, evidence.FieldName);
            string suffix = GetIdentifierSuffix(evidence.FieldName);
            if (field == null || string.IsNullOrWhiteSpace(suffix))
            {
                return;
            }

            MetadataValue tableNameSource = GetReliableNameValue(table.ChineseName);
            bool authoritativeTableName = tableNameSource != null;
            if (tableNameSource == null)
            {
                tableNameSource = GetReliableNameValue(table.SuggestedChineseName);
            }
            string tableBusinessName = GetBusinessObjectName(tableNameSource);
            if (string.IsNullOrWhiteSpace(tableBusinessName))
            {
                return;
            }

            nameLayer.ApplyFieldCandidate(field, CreateEntityPrimaryKeyCandidate(
                table, field, tableNameSource, authoritativeTableName,
                tableBusinessName + suffix, evidence));
            generatedEntityPrimaryKeys.Add(field);
        }

        /// <summary>XMZADD 20260917 用可靠表业务名和数据库主键约束生成“业务对象ID”正式字段名。</summary>
        private static void ApplyPrimaryKeyNames(SnapshotData snapshot, BusinessNameLayerService nameLayer)
        {
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                MetadataValue tableNameSource = GetReliableNameValue(
                    table == null ? null : table.ChineseName);
                bool authoritativeTableName = tableNameSource != null;
                if (tableNameSource == null)
                {
                    tableNameSource = GetReliableNameValue(
                        table == null ? null : table.SuggestedChineseName);
                }
                string tableBusinessName = GetBusinessObjectName(tableNameSource);
                if (table == null || table.Fields == null || string.IsNullOrWhiteSpace(tableBusinessName))
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    string suffix = field == null || !field.IsPrimaryKey
                        ? null
                        : GetIdentifierSuffix(field.FieldName);
                    if (string.IsNullOrWhiteSpace(suffix))
                    {
                        continue;
                    }
                    nameLayer.ApplyFieldCandidate(field, CreatePrimaryKeyCandidate(
                        table, field, tableNameSource, authoritativeTableName,
                        tableBusinessName + suffix));
                }
            }
        }

        /// <summary>XMZADD 20260917 建立带作用域和无作用域端点索引，避免七万张表关系解析发生二次扫描。</summary>
        private static void BuildTableIndexes(SnapshotData snapshot,
            IDictionary<string, List<TableMetadata>> exactIndex,
            IDictionary<string, List<TableMetadata>> endpointIndex)
        {
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                if (table == null || string.IsNullOrWhiteSpace(table.ObjectName))
                {
                    continue;
                }
                AddTable(exactIndex, MakeExactTableKey(table.ScopeKey, table.SchemaName, table.ObjectName), table);
                AddTable(endpointIndex, MakeEndpointKey(table.SchemaName, table.ObjectName), table);
            }
        }

        /// <summary>XMZADD 20260917 将一个表加入端点多值索引，使跨作用域同名对象保持可检测歧义。</summary>
        private static void AddTable(IDictionary<string, List<TableMetadata>> index,
            string key, TableMetadata table)
        {
            List<TableMetadata> values;
            if (!index.TryGetValue(key, out values))
            {
                values = new List<TableMetadata>();
                index.Add(key, values);
            }
            values.Add(table);
        }

        /// <summary>XMZADD 20260917 聚合同一子字段指向的不同父业务对象，冲突目标留给人工判断而不自动命名。</summary>
        private static IDictionary<FieldMetadata, RelationNameBucket> BuildRelationBuckets(
            SnapshotData snapshot,
            IDictionary<string, List<TableMetadata>> exactTableIndex,
            IDictionary<string, List<TableMetadata>> endpointTableIndex)
        {
            var result = new Dictionary<FieldMetadata, RelationNameBucket>();
            var visitedRelations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata owner = snapshot.Tables[tableIndex];
                if (owner == null || owner.Relations == null)
                {
                    continue;
                }
                for (int relationIndex = 0; relationIndex < owner.Relations.Count; relationIndex++)
                {
                    RelationMetadata relation = owner.Relations[relationIndex];
                    string relationKey = MakeRelationKey(relation);
                    if (relation == null || !visitedRelations.Add(relationKey))
                    {
                        continue;
                    }
                    TableMetadata parent = FindTable(relation.ScopeKey, relation.ParentSchemaName,
                        relation.ParentTableName, exactTableIndex, endpointTableIndex);
                    TableMetadata child = FindTable(relation.ScopeKey, relation.ChildSchemaName,
                        relation.ChildTableName, exactTableIndex, endpointTableIndex);
                    FieldMetadata parentField = FindField(parent, relation.ParentFieldName);
                    FieldMetadata childField = FindField(child, relation.ChildFieldName);
                    string targetName = GetRelatedFieldName(parent, parentField);
                    if (childField == null || string.IsNullOrWhiteSpace(targetName))
                    {
                        continue;
                    }
                    RelationNameBucket bucket;
                    if (!result.TryGetValue(childField, out bucket))
                    {
                        bucket = new RelationNameBucket();
                        result.Add(childField, bucket);
                    }
                    bucket.Add(targetName, relation);
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260917 对唯一父业务对象应用关系候选，明确代码或数据库关系可成为正式名，命名关系仅作参考。</summary>
        private static void ApplyRelationNames(IDictionary<FieldMetadata, RelationNameBucket> buckets,
            BusinessNameLayerService nameLayer)
        {
            foreach (KeyValuePair<FieldMetadata, RelationNameBucket> pair in buckets)
            {
                // 真实数据库或业务 SQL 关系应优先于字段命名猜测，避免弱端点阻断已经核验的父对象。
                RelationTarget target = pair.Value.GetUniqueStrongTarget();
                if (target == null)
                {
                    target = pair.Value.GetUniqueTarget();
                }
                if (target == null)
                {
                    continue;
                }
                nameLayer.ApplyFieldCandidate(pair.Key,
                    CreateRelationCandidate(target.Name, target.Relation));
            }
        }

        /// <summary>XMZADD 20260917 将同名字段唯一的已核验关系业务解释传播为保守参考名，覆盖缺少显式关系的历史表。</summary>
        private static void ApplyRelationSignatureReferences(SnapshotData snapshot,
            IDictionary<FieldMetadata, RelationNameBucket> relationBuckets,
            BusinessNameLayerService nameLayer)
        {
            var signatures = new Dictionary<string, RelationFieldSignature>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<FieldMetadata, RelationNameBucket> pair in relationBuckets)
            {
                if (pair.Key == null || !IsSpecificIdentifier(pair.Key.FieldName))
                {
                    continue;
                }
                RelationTarget target = pair.Value.GetUniqueStrongTarget();
                if (target == null)
                {
                    continue;
                }
                RelationFieldSignature signature;
                if (!signatures.TryGetValue(pair.Key.FieldName, out signature))
                {
                    signatures.Add(pair.Key.FieldName,
                        new RelationFieldSignature(target.Name, target.Relation));
                }
                else if (!string.Equals(signature.Name, target.Name, StringComparison.Ordinal))
                {
                    signature.IsAmbiguous = true;
                }
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
                    RelationFieldSignature signature;
                    if (field == null || field.IsPrimaryKey || relationBuckets.ContainsKey(field) ||
                        !signatures.TryGetValue(field.FieldName, out signature) || signature.IsAmbiguous)
                    {
                        continue;
                    }
                    nameLayer.ApplyFieldCandidate(field,
                        CreateRelationSignatureCandidate(signature, table, field));
                }
            }
        }

        /// <summary>XMZADD 20260917 用全库唯一的非通用主键字段签名为同名引用字段生成保守参考译名。</summary>
        private static void ApplyUniquePrimaryKeySignatures(SnapshotData snapshot,
            IDictionary<FieldMetadata, RelationNameBucket> relationBuckets,
            BusinessNameLayerService nameLayer,
            ISet<FieldMetadata> generatedEntityPrimaryKeys)
        {
            var signatures = new Dictionary<string, PrimaryKeySignature>(StringComparer.OrdinalIgnoreCase);
            var generatedFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 当前 EOS 生成实体直接声明业务对象主键，应先于历史复制表和年度归档表的物理主键建立签名。
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
                    if (field == null || !generatedEntityPrimaryKeys.Contains(field) ||
                        !IsSpecificIdentifier(field.FieldName))
                    {
                        continue;
                    }
                    string name = GetReliableName(field.ChineseName);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    generatedFieldNames.Add(field.FieldName);
                    AddPrimaryKeySignature(signatures, field.FieldName, name, table, field, true);
                }
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
                    if (field == null || !field.IsPrimaryKey ||
                        generatedEntityPrimaryKeys.Contains(field) ||
                        generatedFieldNames.Contains(field.FieldName) ||
                        !IsSpecificIdentifier(field.FieldName))
                    {
                        continue;
                    }
                    string name = GetReliableName(field.ChineseName);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    AddPrimaryKeySignature(signatures, field.FieldName, name, table, field, false);
                }
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
                    PrimaryKeySignature signature;
                    if (field == null || field.IsPrimaryKey || generatedEntityPrimaryKeys.Contains(field) ||
                        !signatures.TryGetValue(field.FieldName, out signature) || signature.IsAmbiguous)
                    {
                        continue;
                    }
                    nameLayer.ApplyFieldCandidate(field, CreateSignatureCandidate(signature, table, field));
                }
            }
        }

        /// <summary>XMZADD 20260919 聚合同名主键的业务名称，并在真实业务对象之间存在不同解释时保留歧义状态。</summary>
        private static void AddPrimaryKeySignature(
            IDictionary<string, PrimaryKeySignature> signatures, string fieldName,
            string businessName, TableMetadata table, FieldMetadata field,
            bool generatedEntity)
        {
            PrimaryKeySignature signature;
            if (!signatures.TryGetValue(fieldName, out signature))
            {
                signatures.Add(fieldName, new PrimaryKeySignature(
                    businessName, table, field, generatedEntity));
            }
            else if (!string.Equals(signature.Name, businessName, StringComparison.Ordinal))
            {
                signature.IsAmbiguous = true;
            }
        }

        /// <summary>XMZADD 20260917 解析关系端点表；作用域缺失时仅接受全快照唯一端点，拒绝跨库同名歧义。</summary>
        private static TableMetadata FindTable(string scopeKey, string schemaName, string tableName,
            IDictionary<string, List<TableMetadata>> exactIndex,
            IDictionary<string, List<TableMetadata>> endpointIndex)
        {
            List<TableMetadata> candidates;
            if (!string.IsNullOrWhiteSpace(scopeKey) &&
                exactIndex.TryGetValue(MakeExactTableKey(scopeKey, schemaName, tableName), out candidates) &&
                candidates.Count == 1)
            {
                return candidates[0];
            }
            if (endpointIndex.TryGetValue(MakeEndpointKey(schemaName, tableName), out candidates) &&
                candidates.Count == 1)
            {
                return candidates[0];
            }
            return null;
        }

        /// <summary>XMZADD 20260917 按忽略大小写的物理字段名在已定位表内查找关系端点。</summary>
        private static FieldMetadata FindField(TableMetadata table, string fieldName)
        {
            if (table == null || table.Fields == null)
            {
                return null;
            }
            for (int index = 0; index < table.Fields.Count; index++)
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

        /// <summary>XMZADD 20260917 优先使用父字段正式中文名，缺失时才由父业务对象和标识后缀组合名称。</summary>
        private static string GetRelatedFieldName(TableMetadata parent, FieldMetadata parentField)
        {
            string fieldName = GetReliableName(parentField == null ? null : parentField.ChineseName);
            if (!string.IsNullOrWhiteSpace(fieldName))
            {
                return fieldName;
            }
            string tableName = GetBusinessObjectName(parent == null ? null : parent.ChineseName);
            string suffix = GetIdentifierSuffix(parentField == null ? null : parentField.FieldName);
            return string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(suffix)
                ? null
                : tableName + suffix;
        }

        /// <summary>XMZADD 20260917 创建由真实主键与可靠表名共同支撑的数据库结构名称候选。</summary>
        private static MetadataValue CreatePrimaryKeyCandidate(TableMetadata table, FieldMetadata field,
            MetadataValue tableNameSource, bool authoritativeTableName, string value)
        {
            var evidence = new List<EvidenceItem>
            {
                new EvidenceItem
                {
                    SourceType = "数据库结构",
                    RuleName = "PrimaryKeyBusinessName",
                    RawValue = field.FieldName,
                    OriginalText = (table.SchemaName ?? string.Empty) + "." + table.ObjectName + "." + field.FieldName,
                    Explanation = "真实主键字段沿用已核验业务对象名称"
                }
            };
            AddEvidenceCopies(evidence, tableNameSource == null ? null : tableNameSource.Evidence);
            return new MetadataValue
            {
                Value = value,
                Status = authoritativeTableName
                    ? ConfidenceStatus.DatabaseEvidence
                    : ConfidenceStatus.Guessed,
                ConfidenceScore = authoritativeTableName ? 98 : 82,
                SourceType = authoritativeTableName ? "数据库结构+正式表名" : "数据库结构+参考表名",
                SourceSummary = authoritativeTableName
                    ? "真实主键与可靠业务表名共同确定"
                    : "真实主键沿用可靠参考表名，保留为待确认字段名",
                Evidence = evidence
            };
        }

        /// <summary>XMZADD 20260918 创建保留 mIDCol 源码位置和表名依据的实体主键业务名称候选。</summary>
        private static MetadataValue CreateEntityPrimaryKeyCandidate(TableMetadata table,
            FieldMetadata field, MetadataValue tableNameSource, bool authoritativeTableName,
            string value, SourceEvidence sourceEvidence)
        {
            var evidence = new List<EvidenceItem>
            {
                new EvidenceItem
                {
                    SourceType = "EOS生成实体",
                    SourcePath = sourceEvidence.Evidence.SourcePath,
                    SourceLine = sourceEvidence.Evidence.SourceLine,
                    RuleName = "EntityPrimaryKeyBusinessName",
                    RawValue = field.FieldName,
                    OriginalText = sourceEvidence.Evidence.OriginalText,
                    Explanation = "生成实体主键声明沿用已核验业务对象名称"
                }
            };
            AddEvidenceCopies(evidence, tableNameSource == null ? null : tableNameSource.Evidence);
            return new MetadataValue
            {
                Value = value,
                Status = authoritativeTableName
                    ? ConfidenceStatus.CodeEvidence
                    : ConfidenceStatus.Guessed,
                ConfidenceScore = authoritativeTableName ? 97 : 84,
                SourceType = authoritativeTableName ? "EOS生成实体+正式表名" : "EOS生成实体+参考表名",
                SourceSummary = authoritativeTableName
                    ? "生成实体主键声明与可靠业务表名共同确定"
                    : "生成实体主键声明沿用可靠参考表名，保留为待确认字段名",
                Evidence = evidence
            };
        }

        /// <summary>XMZADD 20260917 创建保留关系来源位置和父子端点的字段名称候选。</summary>
        private static MetadataValue CreateRelationCandidate(string value, RelationMetadata relation)
        {
            ConfidenceStatus relationStatus = relation == null || relation.RelationType == null
                ? ConfidenceStatus.Guessed
                : relation.RelationType.Status;
            bool strong = relationStatus != ConfidenceStatus.Guessed &&
                          relationStatus != ConfidenceStatus.AiGuessed &&
                          relationStatus != ConfidenceStatus.PendingConfirmation;
            EvidenceItem source = GetFirstEvidence(relation == null ? null : relation.RelationType);
            var evidence = new List<EvidenceItem>
            {
                new EvidenceItem
                {
                    SourceType = strong ? "字段关系" : "结构推测",
                    SourcePath = source == null ? null : source.SourcePath,
                    SourceLine = source == null ? 0 : source.SourceLine,
                    RuleName = strong ? "SqlFieldRelation" : "LogicalRelationFieldName",
                    RawValue = relation == null ? string.Empty : relation.ChildFieldName,
                    OriginalText = relation == null ? string.Empty :
                        relation.ChildTableName + "." + relation.ChildFieldName + " -> " +
                        relation.ParentTableName + "." + relation.ParentFieldName,
                    Explanation = strong
                        ? "已核验父子字段关系沿用父业务对象正式名称"
                        : "命名发现的逻辑关系仅提供父业务对象参考译名"
                }
            };
            AddEvidenceCopies(evidence, relation == null || relation.RelationType == null
                ? null
                : relation.RelationType.Evidence);
            return new MetadataValue
            {
                Value = value,
                Status = strong
                    ? (relationStatus == ConfidenceStatus.DatabaseEvidence
                        ? ConfidenceStatus.DatabaseEvidence
                        : ConfidenceStatus.CodeEvidence)
                    : ConfidenceStatus.Guessed,
                ConfidenceScore = strong ? 94 : 76,
                SourceType = strong ? "父子字段关系" : "逻辑关系推测",
                SourceSummary = strong ? "已核验关系目标业务名称" : "唯一逻辑关系目标业务名称",
                Evidence = evidence
            };
        }

        /// <summary>XMZADD 20260917 创建由全库唯一主键签名推得的保守参考名称候选。</summary>
        private static MetadataValue CreateSignatureCandidate(PrimaryKeySignature signature,
            TableMetadata childTable, FieldMetadata childField)
        {
            return new MetadataValue
            {
                Value = signature.Name,
                Status = ConfidenceStatus.Guessed,
                ConfidenceScore = signature.IsGeneratedEntity ? 86 : 74,
                SourceType = signature.IsGeneratedEntity ? "EOS生成实体主键签名" : "唯一主键签名",
                SourceSummary = signature.IsGeneratedEntity
                    ? "同名字段唯一匹配当前 EOS 生成实体声明的业务主键"
                    : "同名字段唯一匹配已命名主键",
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = signature.IsGeneratedEntity ? "EOS生成实体" : "数据库结构",
                        RuleName = signature.IsGeneratedEntity
                            ? "EntityPrimaryKeySignatureReference"
                            : "PrimaryKeySignatureReference",
                        RawValue = childField.FieldName,
                        OriginalText = childTable.ObjectName + "." + childField.FieldName + " -> " +
                            signature.Table.ObjectName + "." + signature.Field.FieldName,
                        Explanation = "全快照仅有一个同名真实主键业务名称，作为待确认参考"
                    }
                }
            };
        }

        /// <summary>XMZADD 20260917 创建由同名字段唯一关系共识推得的参考名称候选并保留原关系端点。</summary>
        private static MetadataValue CreateRelationSignatureCandidate(RelationFieldSignature signature,
            TableMetadata childTable, FieldMetadata childField)
        {
            RelationMetadata relation = signature.Relation;
            EvidenceItem source = GetFirstEvidence(relation == null ? null : relation.RelationType);
            return new MetadataValue
            {
                Value = signature.Name,
                Status = ConfidenceStatus.Guessed,
                ConfidenceScore = 75,
                SourceType = "同名字段关系共识",
                SourceSummary = "同名字段唯一匹配已核验父业务对象",
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "字段关系",
                        SourcePath = source == null ? null : source.SourcePath,
                        SourceLine = source == null ? 0 : source.SourceLine,
                        RuleName = "RelationFieldSignatureReference",
                        RawValue = childField.FieldName,
                        OriginalText = childTable.ObjectName + "." + childField.FieldName + " -> " +
                            (relation == null ? string.Empty : relation.ParentTableName + "." + relation.ParentFieldName),
                        Explanation = "同名字段仅出现一个已核验父业务对象名称，作为待确认参考"
                    }
                }
            };
        }

        /// <summary>XMZADD 20260917 复制表名或关系原始证据，确保结构推名仍可追溯到知识库或源码位置。</summary>
        private static void AddEvidenceCopies(IList<EvidenceItem> target, IList<EvidenceItem> source)
        {
            if (source == null)
            {
                return;
            }
            for (int index = 0; index < source.Count; index++)
            {
                EvidenceItem item = source[index];
                if (item == null)
                {
                    continue;
                }
                target.Add(new EvidenceItem
                {
                    SourceType = item.SourceType,
                    SourcePath = item.SourcePath,
                    SourceLine = item.SourceLine,
                    RuleName = item.RuleName,
                    RawValue = item.RawValue,
                    OriginalText = item.OriginalText,
                    Explanation = item.Explanation,
                    ExtensionData = item.ExtensionData
                });
            }
        }

        /// <summary>XMZADD 20260917 读取关系的第一条原始证据位置供派生候选定位。</summary>
        private static EvidenceItem GetFirstEvidence(MetadataValue value)
        {
            return value == null || value.Evidence == null || value.Evidence.Count == 0
                ? null
                : value.Evidence[0];
        }

        /// <summary>XMZADD 20260917 从正式表名移除对象类型尾缀，避免生成“物料表ID”等生硬字段名。</summary>
        private static string GetBusinessObjectName(MetadataValue value)
        {
            string name = GetReliableName(value);
            if (name.EndsWith("视图", StringComparison.Ordinal))
            {
                return name.Substring(0, name.Length - 2);
            }
            if (name.EndsWith("表", StringComparison.Ordinal))
            {
                return name.Substring(0, name.Length - 1);
            }
            return name;
        }

        /// <summary>XMZADD 20260917 仅返回达到完整中文标准的正式名称。</summary>
        private static string GetReliableName(MetadataValue value)
        {
            string name = value == null ? string.Empty : (value.Value ?? string.Empty).Trim();
            return IdentifierTranslationService.IsReliableChineseName(name) ? name : string.Empty;
        }

        /// <summary>XMZADD 20260917 返回名称可靠的原始元数据值，供派生候选保留正式或参考层级。</summary>
        private static MetadataValue GetReliableNameValue(MetadataValue value)
        {
            return string.IsNullOrWhiteSpace(GetReliableName(value)) ? null : value;
        }

        /// <summary>XMZADD 20260917 从数据库标识字段识别稳定 ID、UID 或 GUID 后缀。</summary>
        private static string GetIdentifierSuffix(string fieldName)
        {
            string value = (fieldName ?? string.Empty).Trim();
            if (value.EndsWith("GUID", StringComparison.OrdinalIgnoreCase))
            {
                return "GUID";
            }
            if (value.EndsWith("UID", StringComparison.OrdinalIgnoreCase))
            {
                return "UID";
            }
            return value.EndsWith("ID", StringComparison.OrdinalIgnoreCase) ? "ID" : null;
        }

        /// <summary>XMZADD 20260917 排除 ID、FID、SID 等过短通用键，仅传播具有两个以上业务前缀字符的签名。</summary>
        private static bool IsSpecificIdentifier(string fieldName)
        {
            string value = (fieldName ?? string.Empty).Trim();
            string suffix = GetIdentifierSuffix(value);
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return false;
            }
            int prefixLength = value.Length - suffix.Length;
            while (prefixLength > 0 && value[prefixLength - 1] == '_')
            {
                prefixLength--;
            }
            return prefixLength >= 2;
        }

        /// <summary>XMZADD 20260917 生成作用域、架构和表名完整索引键。</summary>
        private static string MakeExactTableKey(string scopeKey, string schemaName, string tableName)
        {
            return string.Concat(scopeKey ?? string.Empty, "\u001F", schemaName ?? string.Empty,
                "\u001F", tableName ?? string.Empty);
        }

        /// <summary>XMZADD 20260917 生成缺少作用域关系的架构表名回退索引键。</summary>
        private static string MakeEndpointKey(string schemaName, string tableName)
        {
            return string.Concat(schemaName ?? string.Empty, "\u001F", tableName ?? string.Empty);
        }

        /// <summary>XMZADD 20260917 生成父子端点稳定键，折叠父表和子表保存的同一关系副本。</summary>
        private static string MakeRelationKey(RelationMetadata relation)
        {
            if (relation == null)
            {
                return string.Empty;
            }
            return string.Concat(relation.ScopeKey ?? string.Empty, "\u001F",
                relation.ParentSchemaName ?? string.Empty, "\u001F", relation.ParentTableName ?? string.Empty,
                "\u001F", relation.ParentFieldName ?? string.Empty, "\u001F",
                relation.ChildSchemaName ?? string.Empty, "\u001F", relation.ChildTableName ?? string.Empty,
                "\u001F", relation.ChildFieldName ?? string.Empty);
        }

        /// <summary>XMZADD 20260917 聚合同一子字段关系目标并保留每个中文名的最强关系证据。</summary>
        private sealed class RelationNameBucket
        {
            private readonly Dictionary<string, RelationMetadata> targets =
                new Dictionary<string, RelationMetadata>(StringComparer.Ordinal);

            /// <summary>XMZADD 20260917 加入关系目标，同名目标优先保留非推测关系。</summary>
            public void Add(string name, RelationMetadata relation)
            {
                RelationMetadata existing;
                if (!targets.TryGetValue(name, out existing))
                {
                    targets.Add(name, relation);
                    return;
                }
                if (GetRelationStrength(relation) > GetRelationStrength(existing))
                {
                    targets[name] = relation;
                }
            }

            /// <summary>XMZADD 20260917 仅在所有关系指向同一业务名称时返回可应用目标。</summary>
            public RelationTarget GetUniqueTarget()
            {
                if (targets.Count != 1)
                {
                    return null;
                }
                foreach (KeyValuePair<string, RelationMetadata> pair in targets)
                {
                    return new RelationTarget(pair.Key, pair.Value);
                }
                return null;
            }

            /// <summary>XMZADD 20260917 仅返回由数据库或代码关系支撑的唯一目标，禁止纯命名推测跨表传播。</summary>
            public RelationTarget GetUniqueStrongTarget()
            {
                RelationTarget result = null;
                foreach (KeyValuePair<string, RelationMetadata> pair in targets)
                {
                    if (GetRelationStrength(pair.Value) < 2)
                    {
                        continue;
                    }
                    if (result != null && !string.Equals(result.Name, pair.Key, StringComparison.Ordinal))
                    {
                        return null;
                    }
                    result = new RelationTarget(pair.Key, pair.Value);
                }
                return result;
            }

            /// <summary>XMZADD 20260917 按数据库、代码和命名推测顺序比较关系证据强度。</summary>
            private static int GetRelationStrength(RelationMetadata relation)
            {
                ConfidenceStatus status = relation == null || relation.RelationType == null
                    ? ConfidenceStatus.Guessed
                    : relation.RelationType.Status;
                if (status == ConfidenceStatus.DatabaseEvidence || status == ConfidenceStatus.Confirmed ||
                    status == ConfidenceStatus.LocalOverride)
                {
                    return 3;
                }
                if (status == ConfidenceStatus.CodeEvidence || status == ConfidenceStatus.KnowledgeBaseEvidence)
                {
                    return 2;
                }
                if (HasRelationEvidence(relation, "SqlFieldRelation"))
                {
                    return 2;
                }
                return 1;
            }

            /// <summary>XMZADD 20260919 识别关系是否由真实业务 SQL 等值条件支撑，而不是仅由字段名称猜测。</summary>
            private static bool HasRelationEvidence(RelationMetadata relation, string ruleName)
            {
                if (relation == null || relation.RelationType == null ||
                    relation.RelationType.Evidence == null)
                {
                    return false;
                }
                for (int index = 0; index < relation.RelationType.Evidence.Count; index++)
                {
                    EvidenceItem evidence = relation.RelationType.Evidence[index];
                    if (evidence != null && string.Equals(
                            evidence.RuleName, ruleName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>XMZADD 20260917 保存唯一关系中文名及其最强来源关系。</summary>
        private sealed class RelationTarget
        {
            /// <summary>XMZADD 20260917 创建可应用的唯一关系目标。</summary>
            public RelationTarget(string name, RelationMetadata relation)
            {
                Name = name;
                Relation = relation;
            }

            public string Name { get; private set; }
            public RelationMetadata Relation { get; private set; }
        }

        /// <summary>XMZADD 20260917 保存一个主键字段名在全快照中的唯一业务解释及歧义状态。</summary>
        private sealed class PrimaryKeySignature
        {
            /// <summary>XMZADD 20260917 创建主键签名来源。</summary>
            public PrimaryKeySignature(string name, TableMetadata table, FieldMetadata field,
                bool isGeneratedEntity)
            {
                Name = name;
                Table = table;
                Field = field;
                IsGeneratedEntity = isGeneratedEntity;
            }

            public string Name { get; private set; }
            public TableMetadata Table { get; private set; }
            public FieldMetadata Field { get; private set; }
            public bool IsGeneratedEntity { get; private set; }
            public bool IsAmbiguous { get; set; }
        }

        /// <summary>XMZADD 20260917 保存同名字段已核验关系的唯一中文解释和冲突状态。</summary>
        private sealed class RelationFieldSignature
        {
            /// <summary>XMZADD 20260917 创建关系字段签名来源。</summary>
            public RelationFieldSignature(string name, RelationMetadata relation)
            {
                Name = name;
                Relation = relation;
            }

            public string Name { get; private set; }
            public RelationMetadata Relation { get; private set; }
            public bool IsAmbiguous { get; set; }
        }
    }
}
