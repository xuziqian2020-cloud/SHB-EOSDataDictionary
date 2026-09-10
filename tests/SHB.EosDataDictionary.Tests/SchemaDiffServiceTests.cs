using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证增量结构比较能识别新增字段。</summary>
    [TestClass]
    public sealed class SchemaDiffServiceTests
    {
        /// <summary>XMZADD 20260831 验证新字段会被标记为需要后续自动解释的结构变化。</summary>
        [TestMethod]
        public void Compare_ReportsFieldAdded()
        {
            const string scope = "dev|SHB";
            var previous = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ScopeKey = scope,
                        SchemaName = "dbo",
                        ObjectName = "T_ORDER",
                        Fields = new List<FieldMetadata>()
                    }
                }
            };
            var current = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ScopeKey = scope,
                        SchemaName = "dbo",
                        ObjectName = "T_ORDER",
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata { FieldName = "FNEW", DataType = "nvarchar", LengthText = "50" }
                        }
                    }
                }
            };

            SchemaDiffResult result = new SchemaDiffService().Compare(scope, previous, current);

            Assert.AreEqual(1, result.Changes.Count);
            Assert.AreEqual(SchemaChangeType.FieldAdded, result.Changes[0].ChangeType);
            Assert.AreEqual("T_ORDER", result.Changes[0].ObjectName);
            Assert.AreEqual("FNEW", result.Changes[0].FieldName);
        }

        /// <summary>XMZADD 20260901 验证字段类型、长度和主键约束变化被合并为字段物理变化。</summary>
        [TestMethod]
        public void Compare_ReportsFieldPhysicalChanges()
        {
            SnapshotData previous = CreateRelationSnapshot(false);
            SnapshotData current = CreateRelationSnapshot(false);
            previous.Tables[0].Fields[0].DataType = "int";
            previous.Tables[0].Fields[0].LengthText = "—";
            current.Tables[0].Fields[0].DataType = "bigint";
            current.Tables[0].Fields[0].LengthText = "—";
            current.Tables[0].Fields[0].IsPrimaryKey = true;

            SchemaDiffResult result = new SchemaDiffService().Compare("test|EOS", previous, current);

            Assert.AreEqual(1, result.Changes.Count);
            Assert.AreEqual(SchemaChangeType.FieldChanged, result.Changes[0].ChangeType);
            StringAssert.Contains(result.Changes[0].CurrentValue, "类型=bigint");
            StringAssert.Contains(result.Changes[0].CurrentValue, "主键=True");
        }

        /// <summary>XMZADD 20260901 验证关系新增和删除按完整父子端点稳定身份输出类型化载荷。</summary>
        [TestMethod]
        public void Compare_ReportsRelationAddedAndRemovedWithTypedPayload()
        {
            SnapshotData withoutRelation = CreateRelationSnapshot(false);
            SnapshotData withRelation = CreateRelationSnapshot(true);
            var service = new SchemaDiffService();

            SchemaDiffResult added = service.Compare("test|EOS", withoutRelation, withRelation);
            SchemaDiffResult removed = service.Compare("test|EOS", withRelation, withoutRelation);

            Assert.AreEqual(1, added.Changes.Count);
            Assert.AreEqual(SchemaChangeType.RelationAdded, added.Changes[0].ChangeType);
            Assert.AreEqual("FK_ORDER_CUSTOMER", added.Changes[0].RelationPayload.ForeignKeyName);
            Assert.AreEqual("T_ORDER", added.Changes[0].RelationPayload.ChildTableName);
            Assert.AreEqual(1, removed.Changes.Count);
            Assert.AreEqual(SchemaChangeType.RelationRemoved, removed.Changes[0].ChangeType);
            Assert.AreEqual("FCUSTOMERID", removed.Changes[0].RelationPayload.ChildFieldName);
        }

        /// <summary>XMZADD 20260901 验证整表删除先移除关系再删除表，且不产生已经由整表删除覆盖的字段删除事件。</summary>
        [TestMethod]
        public void Compare_RemovedTable_OrdersRelationBeforeTableWithoutChildFieldDeletes()
        {
            SnapshotData previous = CreateRelationSnapshot(true);
            SnapshotData current = CreateRelationSnapshot(false);
            current.Tables.RemoveAt(0);

            SchemaDiffResult result = new SchemaDiffService().Compare("test|EOS", previous, current);

            Assert.AreEqual(2, result.Changes.Count);
            Assert.AreEqual(SchemaChangeType.RelationRemoved, result.Changes[0].ChangeType);
            Assert.AreEqual(SchemaChangeType.ObjectRemoved, result.Changes[1].ChangeType);
            Assert.AreEqual("T_ORDER", result.Changes[1].ObjectName);
        }

        /// <summary>XMZADD 20260901 验证字段删除会先移除引用关系，新增表则依次建立表、字段和关系端点。</summary>
        [TestMethod]
        public void Compare_FieldRemovalAndNewTable_UsesProtocolDependencyOrder()
        {
            SnapshotData withRelation = CreateRelationSnapshot(true);
            SnapshotData withoutChildField = CreateRelationSnapshot(false);
            withoutChildField.Tables[0].Fields.Clear();

            SchemaDiffResult removed = new SchemaDiffService().Compare("test|EOS", withRelation, withoutChildField);

            Assert.AreEqual(2, removed.Changes.Count);
            Assert.AreEqual(SchemaChangeType.RelationRemoved, removed.Changes[0].ChangeType);
            Assert.AreEqual(SchemaChangeType.FieldRemoved, removed.Changes[1].ChangeType);

            SnapshotData beforeAdd = CreateRelationSnapshot(false);
            beforeAdd.Tables.RemoveAt(0);
            SnapshotData afterAdd = CreateRelationSnapshot(true);
            SchemaDiffResult added = new SchemaDiffService().Compare("test|EOS", beforeAdd, afterAdd);

            Assert.AreEqual(3, added.Changes.Count);
            Assert.AreEqual(SchemaChangeType.ObjectAdded, added.Changes[0].ChangeType);
            Assert.AreEqual(SchemaChangeType.FieldAdded, added.Changes[1].ChangeType);
            Assert.AreEqual(SchemaChangeType.RelationAdded, added.Changes[2].ChangeType);
        }

        /// <summary>XMZADD 20260901 创建两表字段和可选外键关系的最小结构快照。</summary>
        private static SnapshotData CreateRelationSnapshot(bool includeRelation)
        {
            var order = new TableMetadata
            {
                ScopeKey = "test|EOS",
                SchemaName = "dbo",
                ObjectName = "T_ORDER",
                ObjectType = "TABLE",
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { OwnerTableName = "T_ORDER", FieldName = "FCUSTOMERID", DataType = "int", LengthText = "—" }
                },
                Relations = new List<RelationMetadata>()
            };
            var customer = new TableMetadata
            {
                ScopeKey = "test|EOS",
                SchemaName = "dbo",
                ObjectName = "T_CUSTOMER",
                ObjectType = "TABLE",
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { OwnerTableName = "T_CUSTOMER", FieldName = "FID", DataType = "int", LengthText = "—", IsPrimaryKey = true }
                },
                Relations = new List<RelationMetadata>()
            };
            if (includeRelation)
            {
                var relation = new RelationMetadata
                {
                    ScopeKey = "test|EOS",
                    ForeignKeyName = "FK_ORDER_CUSTOMER",
                    ParentSchemaName = "dbo",
                    ParentTableName = "T_CUSTOMER",
                    ParentFieldName = "FID",
                    ChildSchemaName = "dbo",
                    ChildTableName = "T_ORDER",
                    ChildFieldName = "FCUSTOMERID"
                };
                order.Relations.Add(relation);
                customer.Relations.Add(relation);
            }
            return new SnapshotData { Tables = new List<TableMetadata> { order, customer } };
        }
    }
}
