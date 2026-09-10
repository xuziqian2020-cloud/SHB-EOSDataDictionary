using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class BusinessDictionaryV1EnrichmentServiceTests
    {
        /// <summary>XMZADD 20260903 验证第一版富化流水线合并知识库名称、业务枚举和 EOS 对象关系。</summary>
        [TestMethod]
        public void Enrich_HighConfidenceEvidence_ProducesAuditableBusinessDictionary()
        {
            string root = CreateKnowledgeFixture();
            try
            {
                SnapshotData snapshot = CreateSnapshot();
                IList<SourceEvidence> evidence = CreateSourceEvidence();

                new BusinessDictionaryV1EnrichmentService().Enrich(
                    snapshot, evidence, new EosProjectKnowledgeCatalog(root));

                TableMetadata item = snapshot.Tables[0];
                Assert.AreEqual("物料主数据", item.ChineseName.Value);
                Assert.AreEqual(ConfidenceStatus.KnowledgeBaseEvidence, item.ChineseName.Status);
                Assert.AreEqual("物料与BOM", item.ModuleName.Value);
                Assert.AreEqual("t_Item", item.EntityName.Value);
                Assert.AreEqual("物料名称", item.Fields[1].ChineseName.Value);
                Assert.AreEqual(3, item.Fields[2].EnumItems.Count);
                Assert.AreEqual("自制件", item.Fields[2].EnumItems[0].ChineseName.Value);
                Assert.AreEqual(1, item.Relations.Count);
                Assert.AreEqual("Company", item.Relations[0].ParentTableName);
                Assert.AreEqual("Item", item.Relations[0].ChildTableName);
                Assert.AreEqual("Company_ID", item.Relations[0].ChildFieldName);
                Assert.AreEqual(ConfidenceStatus.CodeEvidence, item.Relations[0].RelationType.Status);
                Assert.AreEqual(1, item.Relations[0].RelationType.Evidence.Count);
                Assert.AreEqual("EntityObjectRelation", item.Relations[0].RelationType.Evidence[0].RuleName);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260903 验证第一版富化不会覆盖数据库注释和人工锁定模块。</summary>
        [TestMethod]
        public void Enrich_ProtectedValues_AreNotOverwrittenByKnowledgeBase()
        {
            string root = CreateKnowledgeFixture();
            try
            {
                SnapshotData snapshot = CreateSnapshot();
                snapshot.Tables[0].ChineseName = new MetadataValue
                {
                    Value = "数据库物料表",
                    Status = ConfidenceStatus.DatabaseEvidence
                };
                snapshot.Tables[0].ModuleName = new MetadataValue
                {
                    Value = "人工模块",
                    Status = ConfidenceStatus.LocalOverride,
                    IsManualOverride = true,
                    IsLocked = true
                };

                new BusinessDictionaryV1EnrichmentService().Enrich(
                    snapshot, CreateSourceEvidence(), new EosProjectKnowledgeCatalog(root));

                Assert.AreEqual("数据库物料表", snapshot.Tables[0].ChineseName.Value);
                Assert.AreEqual("人工模块", snapshot.Tables[0].ModuleName.Value);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260903 验证用户确认的 ACCOUNT 业务词义按真实上下文生成名称、模块和可审计证据。</summary>
        [TestMethod]
        public void Enrich_ConfirmedAccountSemantics_DisambiguatesWarehouseAndFinanceLedgers()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "Account_Pallet_FA_Not_IO",
                        ChineseName = new MetadataValue { Value = "财务非托盘出入库表", Status = ConfidenceStatus.Guessed },
                        ModuleName = new MetadataValue { Value = "财务", Status = ConfidenceStatus.Guessed },
                        Fields = new List<FieldMetadata>()
                    },
                    new TableMetadata
                    {
                        ObjectName = "DA_Account",
                        ChineseName = new MetadataValue { Value = "DA财务表", Status = ConfidenceStatus.CodeEvidence },
                        ModuleName = new MetadataValue { Value = "其他", Status = ConfidenceStatus.Guessed },
                        Fields = new List<FieldMetadata>()
                    }
                }
            };

            new BusinessDictionaryV1EnrichmentService().Enrich(
                snapshot, new List<SourceEvidence>(), null);

            Assert.AreEqual("非托盘出入库流水账", snapshot.Tables[0].ChineseName.Value);
            Assert.AreEqual("仓储", snapshot.Tables[0].ModuleName.Value);
            Assert.AreEqual("财务流水账", snapshot.Tables[1].ChineseName.Value);
            Assert.AreEqual("财务", snapshot.Tables[1].ModuleName.Value);
            Assert.AreEqual(ConfidenceStatus.Confirmed, snapshot.Tables[0].ChineseName.Status);
            Assert.AreEqual(100, snapshot.Tables[0].ChineseName.ConfidenceScore);
            Assert.AreEqual("ConfirmedBusinessSemantic", snapshot.Tables[0].ChineseName.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260903 验证离线富化批次会把唯一命名外键写入快照，而不只在界面查看时临时生成。</summary>
        [TestMethod]
        public void Enrich_NameOnlyForeignKey_PublishesAuditableLogicalRelation()
        {
            var item = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Item",
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { FieldName = "ID", IsPrimaryKey = true }
                }
            };
            var image = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Item_Image",
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { FieldName = "Img_ID", IsPrimaryKey = true },
                    new FieldMetadata { FieldName = "Item_ID" }
                }
            };
            var snapshot = new SnapshotData { Tables = new List<TableMetadata> { item, image } };

            new BusinessDictionaryV1EnrichmentService().Enrich(
                snapshot, new List<SourceEvidence>(), null);

            Assert.AreEqual(1, image.Relations.Count);
            Assert.AreEqual("Item", image.Relations[0].ParentTableName);
            Assert.AreEqual("LogicalForeignKeyName", image.Relations[0].RelationType.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260907 验证富化入口先重算旧弱名称，同时完整保护人工、锁定和数据库精确字段名称。</summary>
        [TestMethod]
        public void Enrich_OldWeakValues_RecalculatesBeforeEvidenceAndPreservesProtectedNames()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "Operation_Record",
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "EditorName",
                                ChineseName = new MetadataValue
                                {
                                    Value = "编辑器名称",
                                    Status = ConfidenceStatus.Guessed
                                }
                            },
                            new FieldMetadata
                            {
                                FieldName = "File_Lib_ID",
                                ChineseName = new MetadataValue
                                {
                                    Value = "数据库文件仓ID",
                                    Status = ConfidenceStatus.DatabaseEvidence
                                }
                            },
                            new FieldMetadata
                            {
                                FieldName = "op_createtime",
                                ChineseName = new MetadataValue
                                {
                                    Value = "人工创建时间",
                                    Status = ConfidenceStatus.Guessed,
                                    IsManualOverride = true,
                                    IsLocked = true
                                }
                            }
                        }
                    }
                }
            };

            new BusinessDictionaryV1EnrichmentService().Enrich(
                snapshot, new List<SourceEvidence>(), null);

            Assert.AreEqual("编辑人姓名", snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual("数据库文件仓ID", snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, snapshot.Tables[0].Fields[1].ChineseName.Status);
            Assert.AreEqual("人工创建时间", snapshot.Tables[0].Fields[2].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, snapshot.Tables[0].Fields[2].ChineseName.Status);
        }

        /// <summary>XMZADD 20260903 创建第一版富化测试所需的最小业务快照。</summary>
        private static SnapshotData CreateSnapshot()
        {
            return new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        SchemaName = "dbo",
                        ObjectName = "Item",
                        ChineseName = new MetadataValue { Value = "项目表", Status = ConfidenceStatus.Guessed },
                        ModuleName = new MetadataValue { Value = "其他", Status = ConfidenceStatus.Guessed },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata { FieldName = "Company_ID" },
                            new FieldMetadata { FieldName = "Item_Name" },
                            new FieldMetadata { FieldName = "Item_Make_Buy" }
                        }
                    },
                    new TableMetadata
                    {
                        SchemaName = "dbo",
                        ObjectName = "Company",
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata { FieldName = "Company_ID", IsPrimaryKey = true }
                        }
                    }
                }
            };
        }

        /// <summary>XMZADD 20260903 创建 EOS 表实体、字段属性和对象关系的最小源码证据。</summary>
        private static IList<SourceEvidence> CreateSourceEvidence()
        {
            return new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "Item",
                    EntityName = "t_Item",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty", SourcePath = "ERP/表-类定义/t_Item.vb", SourceLine = 3 }
                },
                new SourceEvidence
                {
                    ObjectName = "Company",
                    EntityName = "t_Company",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty", SourcePath = "ERP/表-类定义/t_Company.vb", SourceLine = 3 }
                },
                new SourceEvidence
                {
                    ObjectName = "Item",
                    FieldName = "Item_Name",
                    EntityName = "t_Item",
                    PropertyName = "f_Item_Name",
                    Evidence = new EvidenceItem { RuleName = "EntityProperty", SourcePath = "ERP/表-类定义/t_Item.vb", SourceLine = 20 }
                },
                new SourceEvidence
                {
                    ObjectName = "Item",
                    EntityName = "t_Item",
                    RelationFieldName = "Company_ID",
                    RelationTargetEntity = "t_Company",
                    Evidence = new EvidenceItem { RuleName = "EntityObjectRelation", SourcePath = "ERP/表-类定义/t_Item.vb", SourceLine = 30 }
                }
            };
        }

        /// <summary>XMZADD 20260903 创建表索引、核心字段和常用枚举的最小 EOS 知识库。</summary>
        private static string CreateKnowledgeFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-business-v1-" + Guid.NewGuid().ToString("N"));
            string tableDirectory = Path.Combine(root, "01_数据库表结构");
            string fieldDirectory = Path.Combine(root, "02_字段字典");
            Directory.CreateDirectory(tableDirectory);
            Directory.CreateDirectory(fieldDirectory);
            File.WriteAllText(Path.Combine(tableDirectory, "_00_表索引.md"),
                "## 物料/BOM相关\r\n\r\n| 表名 | 中文含义 | 备注 |\r\n|---|---|---|\r\n| Item | 物料主数据 | 物料编码、名称和规格 |\r\n",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(fieldDirectory, "_00_核心字段字典.md"),
                "## 一、物料相关字段\r\n\r\n| 字段名 | 中文含义 | 出现表 | 数据类型 |\r\n|---|---|---|---|\r\n" +
                "| Item_Name | 物料名称 | Item | nvarchar(100) |\r\n| Item_Make_Buy | 自制/外购标识 | Item | nvarchar(1) |\r\n\r\n" +
                "## 十一、常用枚举/常量说明\r\n\r\n| 字段 | 值 | 含义 |\r\n|---|---|---|\r\n" +
                "| Item_Make_Buy | M | 自制件 |\r\n| Item_Make_Buy | B | 外购件 |\r\n| Item_Make_Buy | P | 委外加工件 |\r\n",
                Encoding.UTF8);
            return root;
        }
    }
}
