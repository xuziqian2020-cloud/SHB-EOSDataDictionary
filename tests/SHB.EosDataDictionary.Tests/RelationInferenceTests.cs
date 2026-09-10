using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class RelationInferenceTests
    {
        [TestMethod]
        public void Infer_NormalForeignKey_IsDatabaseEvidenceOneToMany()
        {
            RelationMetadata relation = RelationInferenceService.Infer(new RelationInput
            {
                ParentTableName = "T_PARENT",
                ParentFieldName = "FID",
                ChildTableName = "T_CHILD",
                ChildFieldName = "FPARENTID",
                HasRealForeignKey = true,
                ChildForeignKeyIsUnique = false,
                IsBridgeTable = false
            });

            Assert.AreEqual("一对多", relation.RelationType.Value);
            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, relation.RelationType.Status);
        }

        [TestMethod]
        public void Infer_NameOnlyMatch_IsGuessed()
        {
            RelationMetadata relation = RelationInferenceService.Infer(new RelationInput
            {
                ParentTableName = "T_PARENT",
                ParentFieldName = "FID",
                ChildTableName = "T_CHILD",
                ChildFieldName = "FPARENTID",
                HasRealForeignKey = false,
                ChildForeignKeyIsUnique = false,
                IsBridgeTable = false
            });

            Assert.AreEqual(ConfidenceStatus.Guessed, relation.RelationType.Status);
            Assert.IsTrue(relation.RelationType.Value.Contains("推测"));
        }

        /// <summary>XMZADD 20260901 验证关系推理保留父子对象的模式名以形成跨 schema 稳定键。</summary>
        [TestMethod]
        public void Infer_RelationSchemaNames_ArePreserved()
        {
            RelationMetadata relation = RelationInferenceService.Infer(new RelationInput
            {
                ParentSchemaName = "master",
                ParentTableName = "T_SHARED",
                ParentFieldName = "FId",
                ChildSchemaName = "sales",
                ChildTableName = "T_ORDER",
                ChildFieldName = "FSharedId",
                HasRealForeignKey = true
            });

            Assert.AreEqual("master", relation.ParentSchemaName);
            Assert.AreEqual("sales", relation.ChildSchemaName);
        }

        /// <summary>XMZADD 20260831 验证命名外键可通过目标表主键建立双向可见的推测关系。</summary>
        [TestMethod]
        public void Discover_ItemImageItemId_AddsRelationToParentAndChild()
        {
            SnapshotData snapshot = CreateItemImageSnapshot(false);

            LogicalRelationDiscoveryService.Discover(snapshot);

            Assert.AreEqual(1, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(1, snapshot.Tables[1].Relations.Count);
            RelationMetadata relation = snapshot.Tables[1].Relations[0];
            Assert.AreEqual("dbo", relation.ParentSchemaName);
            Assert.AreEqual("Item", relation.ParentTableName);
            Assert.AreEqual("ID", relation.ParentFieldName);
            Assert.AreEqual("dbo", relation.ChildSchemaName);
            Assert.AreEqual("Item_Image", relation.ChildTableName);
            Assert.AreEqual("Item_ID", relation.ChildFieldName);
            Assert.AreEqual(ConfidenceStatus.Guessed, relation.RelationType.Status);
            Assert.AreEqual(1, relation.RelationType.Evidence.Count);
            Assert.AreEqual("LogicalForeignKeyName", relation.RelationType.Evidence[0].RuleName);
            Assert.AreEqual("Item_ID", relation.RelationType.Evidence[0].RawValue);
            Assert.AreEqual(1, snapshot.Tables[1].Fields[1].RelationSummary.Evidence.Count);
            Assert.AreEqual("LogicalForeignKeyName", snapshot.Tables[1].Fields[1].RelationSummary.Evidence[0].RuleName);
            Assert.AreEqual("推测外键", new FieldDisplayModel(snapshot.Tables[1].Fields[1]).KeyText);
        }

        /// <summary>XMZADD 20260831 验证数据库真实外键存在时不会再生成同字段逻辑关系。</summary>
        [TestMethod]
        public void Discover_ExistingPhysicalRelation_DoesNotDuplicate()
        {
            SnapshotData snapshot = CreateItemImageSnapshot(true);

            LogicalRelationDiscoveryService.Discover(snapshot);

            Assert.AreEqual(1, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(1, snapshot.Tables[1].Relations.Count);
        }

        /// <summary>XMZADD 20260831 验证目标表不唯一时不生成可能误导业务代码的关系。</summary>
        [TestMethod]
        public void Discover_AmbiguousTarget_DoesNotGuess()
        {
            SnapshotData snapshot = CreateItemImageSnapshot(false);
            snapshot.Tables.Insert(1, new TableMetadata
            {
                SchemaName = "archive",
                ObjectName = "T_Item",
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { FieldName = "ID", IsPrimaryKey = true }
                }
            });

            LogicalRelationDiscoveryService.Discover(snapshot);

            Assert.AreEqual(0, snapshot.Tables[snapshot.Tables.Count - 1].Relations.Count);
        }

        /// <summary>XMZADD 20260831 验证旧快照反序列化出的只读关系数组会先转换为可追加集合。</summary>
        [TestMethod]
        public void Discover_ArrayBackedRelations_ConvertsToMutableCollections()
        {
            SnapshotData snapshot = CreateItemImageSnapshot(false);
            snapshot.Tables[0].Relations = new RelationMetadata[0];
            snapshot.Tables[1].Relations = new RelationMetadata[0];

            LogicalRelationDiscoveryService.Discover(snapshot);

            Assert.AreEqual(1, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(1, snapshot.Tables[1].Relations.Count);
        }

        /// <summary>XMZADD 20260831 创建物料主表和物料图片表的最小关系快照。</summary>
        private static SnapshotData CreateItemImageSnapshot(bool includePhysicalRelation)
        {
            var parent = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Item",
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { FieldName = "ID", IsPrimaryKey = true }
                }
            };
            var child = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Item_Image",
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { FieldName = "Img_ID", IsPrimaryKey = true },
                    new FieldMetadata { FieldName = "Item_ID" }
                }
            };
            if (includePhysicalRelation)
            {
                var relation = new RelationMetadata
                {
                    ParentTableName = "Item",
                    ParentFieldName = "ID",
                    ChildTableName = "Item_Image",
                    ChildFieldName = "Item_ID",
                    RelationType = new MetadataValue { Value = "一对多", Status = ConfidenceStatus.DatabaseEvidence }
                };
                parent.Relations.Add(relation);
                child.Relations.Add(relation);
                child.Fields[1].IsForeignKey = true;
            }
            return new SnapshotData { Tables = new List<TableMetadata> { parent, child } };
        }
    }
}
