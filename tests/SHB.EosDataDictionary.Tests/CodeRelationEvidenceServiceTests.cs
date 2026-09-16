using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260916 验证 EOS 实体关系与业务 SQL JOIN 必须交叉印证后才能成为正式关联。</summary>
    [TestClass]
    public sealed class CodeRelationEvidenceServiceTests
    {
        /// <summary>XMZADD 20260916 验证实体对象属性和 SQL JOIN 指向同一端点时形成正式代码关系。</summary>
        [TestMethod]
        public void Apply_EntityAndJoinAgree_PromotesCodeRelation()
        {
            SnapshotData snapshot = CreatePurchaseOrderSnapshot(false);
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateEntityEvidence(),
                CreateJoinEvidence("Purchase/OrderQuery.vb", "Purchase_Order_Item", "PO_ID",
                    "Purchase_Order", "PO_ID")
            };

            new CodeRelationEvidenceService().Apply(snapshot, evidence);

            RelationMetadata relation = GetOnlyChildRelation(snapshot);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, relation.RelationType.Status);
            Assert.AreEqual("代码关联：多对一", relation.RelationType.Value);
            Assert.AreEqual("Purchase_Order", relation.ParentTableName);
            Assert.AreEqual("Purchase_Order_Item", relation.ChildTableName);
            AssertHasRule(relation.RelationType.Evidence, "EntityObjectRelation");
            AssertHasRule(relation.RelationType.Evidence, "SqlFieldRelation");
        }

        /// <summary>XMZADD 20260916 验证单个业务 SQL JOIN 只作为参考关系，不能误报正式外键。</summary>
        [TestMethod]
        public void Apply_SingleJoin_CreatesReferenceRelation()
        {
            SnapshotData snapshot = CreatePurchaseOrderSnapshot(false);
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateJoinEvidence("Purchase/OrderQuery.vb", "Purchase_Order_Item", "PO_ID",
                    "Purchase_Order", "PO_ID")
            };

            new CodeRelationEvidenceService().Apply(snapshot, evidence);

            RelationMetadata relation = GetOnlyChildRelation(snapshot);
            Assert.AreEqual(ConfidenceStatus.Guessed, relation.RelationType.Status);
            Assert.AreEqual("参考关联：多对一", relation.RelationType.Value);
            Assert.AreEqual("参考外键", new FieldDisplayModel(snapshot.Tables[1].Fields[1]).KeyText);
        }

        /// <summary>XMZADD 20260916 验证两个独立业务文件中的同向 JOIN 可相互印证为正式关系。</summary>
        [TestMethod]
        public void Apply_TwoIndependentJoinFiles_PromotesCodeRelation()
        {
            SnapshotData snapshot = CreatePurchaseOrderSnapshot(false);
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateJoinEvidence("Purchase/OrderQuery.vb", "Purchase_Order_Item", "PO_ID",
                    "Purchase_Order", "PO_ID"),
                CreateJoinEvidence("Purchase/OrderReport.vb", "Purchase_Order", "PO_ID",
                    "Purchase_Order_Item", "PO_ID")
            };

            new CodeRelationEvidenceService().Apply(snapshot, evidence);

            RelationMetadata relation = GetOnlyChildRelation(snapshot);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, relation.RelationType.Status);
            Assert.AreEqual(2, relation.RelationType.Evidence.Count);
        }

        /// <summary>XMZADD 20260916 验证同一文件同一 JOIN 的双向端点证据不会被误算成两个独立来源。</summary>
        [TestMethod]
        public void Apply_ReciprocalJoinEvidenceInOneFile_RemainsSingleReference()
        {
            SnapshotData snapshot = CreatePurchaseOrderSnapshot(false);
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateJoinEvidence("Purchase/OrderQuery.vb", "Purchase_Order_Item", "PO_ID",
                    "Purchase_Order", "PO_ID"),
                CreateJoinEvidence("Purchase/OrderQuery.vb", "Purchase_Order", "PO_ID",
                    "Purchase_Order_Item", "PO_ID")
            };

            new CodeRelationEvidenceService().Apply(snapshot, evidence);

            RelationMetadata relation = GetOnlyChildRelation(snapshot);
            Assert.AreEqual(ConfidenceStatus.Guessed, relation.RelationType.Status);
            Assert.AreEqual(1, relation.RelationType.Evidence.Count);
            Assert.AreEqual(1, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(1, snapshot.Tables[1].Relations.Count);
        }

        /// <summary>XMZADD 20260916 验证同一子字段指向不同父端点时按完整关系键保留为两个待审参考，而不是静默吞掉冲突。</summary>
        [TestMethod]
        public void Apply_SameChildFieldWithDifferentParents_KeepsDistinctRelations()
        {
            SnapshotData snapshot = CreatePurchaseOrderSnapshot(false);
            snapshot.Tables.Add(new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Purchase_Order_Archive",
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { OwnerTableName = "Purchase_Order_Archive", FieldName = "PO_ID", IsPrimaryKey = true }
                }
            });
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateJoinEvidence("Purchase/CurrentOrder.vb", "Purchase_Order_Item", "PO_ID",
                    "Purchase_Order", "PO_ID"),
                CreateJoinEvidence("Purchase/ArchiveOrder.vb", "Purchase_Order_Item", "PO_ID",
                    "Purchase_Order_Archive", "PO_ID")
            };

            new CodeRelationEvidenceService().Apply(snapshot, evidence);

            Assert.AreEqual(2, snapshot.Tables[1].Relations.Count);
            Assert.AreNotEqual(snapshot.Tables[1].Relations[0].ParentTableName,
                snapshot.Tables[1].Relations[1].ParentTableName);
            Assert.AreEqual(ConfidenceStatus.Guessed, snapshot.Tables[1].Relations[0].RelationType.Status);
            Assert.AreEqual(ConfidenceStatus.Guessed, snapshot.Tables[1].Relations[1].RelationType.Status);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict,
                snapshot.Tables[1].Fields[1].RelationSummary.Status);
            StringAssert.Contains(snapshot.Tables[1].Fields[1].RelationSummary.Value, "多个可能父表");
        }

        /// <summary>XMZADD 20260916 验证物理外键不会被单个代码 JOIN 降级或重复生成。</summary>
        [TestMethod]
        public void Apply_PhysicalForeignKey_RemainsDatabaseEvidence()
        {
            SnapshotData snapshot = CreatePurchaseOrderSnapshot(true);
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateJoinEvidence("Purchase/OrderQuery.vb", "Purchase_Order_Item", "PO_ID",
                    "Purchase_Order", "PO_ID")
            };

            new CodeRelationEvidenceService().Apply(snapshot, evidence);

            RelationMetadata relation = GetOnlyChildRelation(snapshot);
            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, relation.RelationType.Status);
            Assert.AreEqual("一对多", relation.RelationType.Value);
            Assert.AreEqual(1, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(1, snapshot.Tables[1].Relations.Count);
        }

        /// <summary>XMZADD 20260916 验证 SQL 等号左右顺序不决定父子方向，真实主键端始终作为父表。</summary>
        [TestMethod]
        public void Apply_ParentAppearsOnSqlLeft_UsesPrimaryKeyForDirection()
        {
            SnapshotData snapshot = CreatePurchaseOrderSnapshot(false);
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateJoinEvidence("Purchase/OrderQuery.vb", "Purchase_Order", "PO_ID",
                    "Purchase_Order_Item", "PO_ID")
            };

            new CodeRelationEvidenceService().Apply(snapshot, evidence);

            RelationMetadata relation = GetOnlyChildRelation(snapshot);
            Assert.AreEqual("Purchase_Order", relation.ParentTableName);
            Assert.AreEqual("PO_ID", relation.ParentFieldName);
            Assert.AreEqual("Purchase_Order_Item", relation.ChildTableName);
            Assert.AreEqual("PO_ID", relation.ChildFieldName);
        }

        /// <summary>XMZADD 20260916 验证临时表、表变量和复杂字段表达式不能进入正式物理关系集合。</summary>
        [TestMethod]
        public void Apply_TemporaryObjectsAndComplexExpressions_AreExcluded()
        {
            SnapshotData snapshot = CreatePurchaseOrderSnapshot(false);
            snapshot.Tables.Add(new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "#TempOrder",
                Fields = new List<FieldMetadata> { new FieldMetadata { FieldName = "PO_ID" } }
            });
            snapshot.Tables.Add(new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "@OrderRows",
                Fields = new List<FieldMetadata> { new FieldMetadata { FieldName = "PO_ID" } }
            });
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateJoinEvidence("Purchase/Temp.vb", "#TempOrder", "PO_ID", "Purchase_Order", "PO_ID"),
                CreateJoinEvidence("Purchase/Variable.vb", "@OrderRows", "PO_ID", "Purchase_Order", "PO_ID"),
                CreateJoinEvidence("Purchase/Expression.vb", "Purchase_Order_Item", "CONVERT(PO_ID)",
                    "Purchase_Order", "PO_ID")
            };

            new CodeRelationEvidenceService().Apply(snapshot, evidence);

            Assert.AreEqual(0, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(0, snapshot.Tables[1].Relations.Count);
            Assert.AreEqual(0, snapshot.Tables[2].Relations.Count);
            Assert.AreEqual(0, snapshot.Tables[3].Relations.Count);
        }

        /// <summary>XMZADD 20260916 验证只有字段命名相似时仍保持结构推测，不能升级为代码关系。</summary>
        [TestMethod]
        public void Apply_NameSimilarityWithoutCodeUsage_RemainsGuessed()
        {
            SnapshotData snapshot = CreatePurchaseOrderSnapshot(false);
            snapshot.Tables[0].Fields[0].FieldName = "ID";
            snapshot.Tables[1].Fields[1].FieldName = "Purchase_Order_ID";

            new CodeRelationEvidenceService().Apply(snapshot, new List<SourceEvidence>());
            LogicalRelationDiscoveryService.Discover(snapshot);

            RelationMetadata relation = GetOnlyChildRelation(snapshot);
            Assert.AreEqual(ConfidenceStatus.Guessed, relation.RelationType.Status);
            Assert.AreEqual("LogicalForeignKeyName", relation.RelationType.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260916 创建采购订单主表与明细表，并可预置一个真实物理外键。</summary>
        private static SnapshotData CreatePurchaseOrderSnapshot(bool includePhysicalForeignKey)
        {
            var parent = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Purchase_Order",
                EntityName = new MetadataValue { Value = "t_Purchase_Order", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { OwnerTableName = "Purchase_Order", FieldName = "PO_ID", IsPrimaryKey = true }
                }
            };
            var child = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Purchase_Order_Item",
                EntityName = new MetadataValue { Value = "t_Purchase_Order_Item", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { OwnerTableName = "Purchase_Order_Item", FieldName = "POI_ID", IsPrimaryKey = true },
                    new FieldMetadata { OwnerTableName = "Purchase_Order_Item", FieldName = "PO_ID" }
                }
            };
            if (includePhysicalForeignKey)
            {
                RelationMetadata relation = RelationInferenceService.Infer(new RelationInput
                {
                    ParentSchemaName = "dbo",
                    ParentTableName = "Purchase_Order",
                    ParentFieldName = "PO_ID",
                    ChildSchemaName = "dbo",
                    ChildTableName = "Purchase_Order_Item",
                    ChildFieldName = "PO_ID",
                    HasRealForeignKey = true
                });
                relation.ForeignKeyName = "FK_Purchase_Order_Item_Purchase_Order";
                parent.Relations.Add(relation);
                child.Relations.Add(relation);
                child.Fields[1].IsForeignKey = true;
            }
            return new SnapshotData { Tables = new List<TableMetadata> { parent, child } };
        }

        /// <summary>XMZADD 20260916 创建由生成实体 obj 属性提供的子表到父实体关系证据。</summary>
        private static SourceEvidence CreateEntityEvidence()
        {
            return new SourceEvidence
            {
                ObjectName = "Purchase_Order_Item",
                RelationFieldName = "PO_ID",
                RelationTargetEntity = "t_Purchase_Order",
                Origin = SourceEvidenceOrigin.GeneratedEntity,
                UsageKind = SourceUsageKind.Relation,
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS生成实体",
                    SourcePath = "ERP/表-类定义/t_Purchase_Order_Item.vb",
                    SourceLine = 80,
                    RuleName = "EntityObjectRelation"
                }
            };
        }

        /// <summary>XMZADD 20260916 创建一个可控制 SQL 左右端点和独立文件位置的等值 JOIN 证据。</summary>
        private static SourceEvidence CreateJoinEvidence(string sourcePath, string objectName,
            string fieldName, string targetObjectName, string targetFieldName)
        {
            return new SourceEvidence
            {
                ObjectName = objectName,
                FieldName = fieldName,
                RelationTargetObjectName = targetObjectName,
                RelationTargetFieldName = targetFieldName,
                Origin = SourceEvidenceOrigin.BusinessCode,
                UsageKind = SourceUsageKind.Relation,
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS业务源码",
                    SourcePath = sourcePath,
                    SourceLine = 120,
                    RuleName = "SqlFieldRelation"
                }
            };
        }

        /// <summary>XMZADD 20260916 返回明细表唯一关系，确保双向列表没有产生重复项。</summary>
        private static RelationMetadata GetOnlyChildRelation(SnapshotData snapshot)
        {
            Assert.AreEqual(1, snapshot.Tables[1].Relations.Count);
            return snapshot.Tables[1].Relations[0];
        }

        /// <summary>XMZADD 20260916 断言关系证据同时保留指定来源规则。</summary>
        private static void AssertHasRule(IList<EvidenceItem> evidence, string ruleName)
        {
            for (int index = 0; evidence != null && index < evidence.Count; index++)
            {
                if (evidence[index] != null && string.Equals(evidence[index].RuleName, ruleName,
                        StringComparison.Ordinal))
                {
                    return;
                }
            }
            Assert.Fail("未找到关系证据规则：" + ruleName);
        }
    }
}
