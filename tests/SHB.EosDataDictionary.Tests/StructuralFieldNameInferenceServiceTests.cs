using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260917 验证主键、代码关系和唯一键签名可审计地补全字段业务中文名。</summary>
    [TestClass]
    public sealed class StructuralFieldNameInferenceServiceTests
    {
        /// <summary>XMZADD 20260917 验证可靠业务表名与真实主键共同确定主键字段名称。</summary>
        [TestMethod]
        public void Apply_PrimaryKeyWithReliableTableName_CreatesOfficialBusinessIdentifier()
        {
            TableMetadata parent = CreateTable("DS_Item_Income", "物料入库");
            FieldMetadata key = CreateField("DII_ID", true);
            parent.Fields.Add(key);

            new StructuralFieldNameInferenceService().Apply(CreateSnapshot(parent));

            Assert.AreEqual("物料入库ID", key.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, key.ChineseName.Status);
        }

        /// <summary>XMZADD 20260917 验证可靠参考表名只能生成主键参考名，不能越级成为正式结论。</summary>
        [TestMethod]
        public void Apply_PrimaryKeyWithReliableSuggestedTableName_CreatesSuggestionOnly()
        {
            TableMetadata table = CreateTable("OQC_Detail", string.Empty);
            table.SuggestedChineseName = new MetadataValue
            {
                Value = "出货质量检验明细",
                Status = ConfidenceStatus.Guessed,
                ConfidenceScore = 82,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "EOS业务代码",
                        SourcePath = "Quality_x/Out_Quality_Control.vb",
                        SourceLine = 490,
                        RuleName = "VerifiedBusinessTableContext"
                    }
                }
            };
            FieldMetadata key = CreateField("OQCD_ID", true);
            table.Fields.Add(key);

            new StructuralFieldNameInferenceService().Apply(CreateSnapshot(table));

            Assert.AreEqual(string.Empty, key.ChineseName.Value);
            Assert.AreEqual("出货质量检验明细ID", key.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, key.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260917 验证源码核验关系把父业务对象名称传递到子表外键字段。</summary>
        [TestMethod]
        public void Apply_CodeRelation_CreatesOfficialChildFieldName()
        {
            TableMetadata parent = CreateTable("DS_Item_Income", "物料入库");
            parent.Fields.Add(CreateField("DII_ID", true));
            TableMetadata child = CreateTable("DS_Item_Income_Item", "物料入库明细");
            FieldMetadata childField = CreateField("DII_ID", false);
            child.Fields.Add(childField);
            child.Relations.Add(CreateRelation(parent, child, "DII_ID", "DII_ID",
                ConfidenceStatus.CodeEvidence, "SqlFieldRelation"));

            new StructuralFieldNameInferenceService().Apply(CreateSnapshot(parent, child));

            Assert.AreEqual("物料入库ID", childField.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, childField.ChineseName.Status);
        }

        /// <summary>XMZADD 20260917 验证仅由命名发现的逻辑关系只进入参考译名而不伪装成正式结论。</summary>
        [TestMethod]
        public void Apply_GuessedRelation_CreatesSuggestionOnly()
        {
            TableMetadata parent = CreateTable("DS_Item_Income", "物料入库");
            parent.Fields.Add(CreateField("DII_ID", true));
            TableMetadata child = CreateTable("DS_Item_Income_Item", "物料入库明细");
            FieldMetadata childField = CreateField("DII_ID", false);
            child.Fields.Add(childField);
            child.Relations.Add(CreateRelation(parent, child, "DII_ID", "DII_ID",
                ConfidenceStatus.Guessed, "LogicalForeignKeyName"));

            new StructuralFieldNameInferenceService().Apply(CreateSnapshot(parent, child));

            Assert.AreEqual(string.Empty, childField.ChineseName.Value);
            Assert.AreEqual("物料入库ID", childField.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, childField.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260917 验证同一子字段存在多个不同父业务对象时不自动选择任何一个名称。</summary>
        [TestMethod]
        public void Apply_ConflictingParentTargets_DoesNotChooseName()
        {
            TableMetadata first = CreateTable("First_Order", "首件订单");
            first.Fields.Add(CreateField("First_ID", true));
            TableMetadata second = CreateTable("Second_Order", "复核订单");
            second.Fields.Add(CreateField("Second_ID", true));
            TableMetadata child = CreateTable("Order_Link", "订单关联");
            FieldMetadata childField = CreateField("Target_ID", false);
            child.Fields.Add(childField);
            child.Relations.Add(CreateRelation(first, child, "First_ID", "Target_ID",
                ConfidenceStatus.CodeEvidence, "SqlFieldRelation"));
            child.Relations.Add(CreateRelation(second, child, "Second_ID", "Target_ID",
                ConfidenceStatus.CodeEvidence, "SqlFieldRelation"));

            new StructuralFieldNameInferenceService().Apply(CreateSnapshot(first, second, child));

            Assert.AreEqual(string.Empty, childField.ChineseName.Value);
            Assert.IsNull(childField.SuggestedChineseName);
        }

        /// <summary>XMZADD 20260917 验证全库唯一的业务主键签名只为同名引用字段提供保守参考译名。</summary>
        [TestMethod]
        public void Apply_UniquePrimaryKeySignature_CreatesSuggestionOnly()
        {
            TableMetadata parent = CreateTable("DS_Item_Income", "物料入库");
            parent.Fields.Add(CreateField("DII_ID", true));
            TableMetadata child = CreateTable("External_Record", "外部记录");
            FieldMetadata childField = CreateField("DII_ID", false);
            child.Fields.Add(childField);

            new StructuralFieldNameInferenceService().Apply(CreateSnapshot(parent, child));

            Assert.AreEqual(string.Empty, childField.ChineseName.Value);
            Assert.AreEqual("物料入库ID", childField.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, childField.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260918 验证生成实体声明的主键在数据库未建主键约束时仍可沿用正式业务表名。</summary>
        [TestMethod]
        public void Apply_EntityDeclaredPrimaryKeyWithoutDatabaseConstraint_CreatesOfficialName()
        {
            TableMetadata table = CreateTable("CPO_Item", "客户采购订单明细");
            FieldMetadata field = CreateField("CPOI_ID", false);
            table.Fields.Add(field);
            var evidence = new List<SourceEvidence>
            {
                CreateEntityPrimaryKeyEvidence("CPO_Item", "CPOI_ID")
            };

            new StructuralFieldNameInferenceService().Apply(CreateSnapshot(table), evidence);

            Assert.AreEqual("客户采购订单明细ID", field.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, field.ChineseName.Status);
            Assert.AreEqual("EntityPrimaryKeyBusinessName", field.ChineseName.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260918 验证生成实体主键与参考表名组合后仍停留在待确认层，不越级成为正式名。</summary>
        [TestMethod]
        public void Apply_EntityDeclaredPrimaryKeyWithSuggestedTableName_CreatesSuggestionOnly()
        {
            TableMetadata table = CreateTable("CPO_Item", string.Empty);
            table.SuggestedChineseName = new MetadataValue
            {
                Value = "客户采购订单明细",
                Status = ConfidenceStatus.Guessed,
                ConfidenceScore = 82
            };
            FieldMetadata field = CreateField("CPOI_ID", false);
            table.Fields.Add(field);
            var evidence = new List<SourceEvidence>
            {
                CreateEntityPrimaryKeyEvidence("CPO_Item", "CPOI_ID")
            };

            new StructuralFieldNameInferenceService().Apply(CreateSnapshot(table), evidence);

            Assert.AreEqual(string.Empty, field.ChineseName.Value);
            Assert.AreEqual("客户采购订单明细ID", field.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, field.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260918 验证生成实体主键的唯一字段签名可为缺少显式关系的同名引用提供参考译名。</summary>
        [TestMethod]
        public void Apply_EntityDeclaredPrimaryKeySignature_PropagatesSuggestion()
        {
            TableMetadata parent = CreateTable("CPO_Item", "客户采购订单明细");
            parent.Fields.Add(CreateField("CPOI_ID", false));
            TableMetadata child = CreateTable("MPI_WC", "车间主计划");
            FieldMetadata reference = CreateField("CPOI_ID", false);
            child.Fields.Add(reference);
            var evidence = new List<SourceEvidence>
            {
                CreateEntityPrimaryKeyEvidence("CPO_Item", "CPOI_ID")
            };

            new StructuralFieldNameInferenceService().Apply(
                CreateSnapshot(parent, child), evidence);

            Assert.AreEqual("客户采购订单明细ID", reference.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, reference.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260919 验证当前 EOS 实体主键优先于历史复制表的同名物理主键，避免归档表名称制造伪冲突。</summary>
        [TestMethod]
        public void Apply_EntityPrimaryKeySignature_IgnoresHistoricalPhysicalKeyConflict()
        {
            TableMetadata parent = CreateTable("Sale_Value", "销售来源项");
            parent.Fields.Add(CreateField("SV_ID", false));
            TableMetadata archive = CreateTable("Sale_Value_1", "历史销售来源项");
            archive.Fields.Add(CreateField("SV_ID", true));
            TableMetadata child = CreateTable("AcPro", "产品流水账");
            FieldMetadata reference = CreateField("SV_ID", false);
            child.Fields.Add(reference);
            var evidence = new List<SourceEvidence>
            {
                CreateEntityPrimaryKeyEvidence("Sale_Value", "SV_ID")
            };

            new StructuralFieldNameInferenceService().Apply(
                CreateSnapshot(parent, archive, child), evidence);

            Assert.AreEqual("销售来源项ID", reference.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, reference.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260919 验证真实 SQL 字段关系可压过同字段的弱命名关系，避免已核验父对象被猜测端点阻断。</summary>
        [TestMethod]
        public void Apply_StrongRelation_WinsOverConflictingGuessedRelation()
        {
            TableMetadata verifiedParent = CreateTable("DP_Order", "发货指令");
            verifiedParent.Fields.Add(CreateField("DO_ID", true));
            TableMetadata guessedParent = CreateTable("DO", "单据对象");
            guessedParent.Fields.Add(CreateField("DO_ID", true));
            TableMetadata child = CreateTable("Account_Sheet", "流水账单据");
            FieldMetadata reference = CreateField("DO_ID", false);
            child.Fields.Add(reference);
            child.Relations.Add(CreateRelation(verifiedParent, child, "DO_ID", "DO_ID",
                ConfidenceStatus.CodeEvidence, "SqlFieldRelation"));
            child.Relations.Add(CreateRelation(guessedParent, child, "DO_ID", "DO_ID",
                ConfidenceStatus.Guessed, "LogicalForeignKeyName"));

            new StructuralFieldNameInferenceService().Apply(
                CreateSnapshot(verifiedParent, guessedParent, child));

            Assert.AreEqual("发货指令ID", reference.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, reference.ChineseName.Status);
        }

        /// <summary>XMZADD 20260919 验证单条业务 SQL 等值关系虽保留为参考层，仍优先于纯字段命名推测。</summary>
        [TestMethod]
        public void Apply_SingleSqlRelation_WinsOverLogicalNameRelation()
        {
            TableMetadata verifiedParent = CreateTable("DP_Order", "发货指令");
            verifiedParent.Fields.Add(CreateField("DO_ID", true));
            TableMetadata guessedParent = CreateTable("DO", "单据对象");
            guessedParent.Fields.Add(CreateField("DO_ID", true));
            TableMetadata child = CreateTable("Account_Sheet", "流水账单据");
            FieldMetadata reference = CreateField("DO_ID", false);
            child.Fields.Add(reference);
            child.Relations.Add(CreateRelation(verifiedParent, child, "DO_ID", "DO_ID",
                ConfidenceStatus.Guessed, "SqlFieldRelation"));
            child.Relations.Add(CreateRelation(guessedParent, child, "DO_ID", "DO_ID",
                ConfidenceStatus.Guessed, "LogicalForeignKeyName"));

            new StructuralFieldNameInferenceService().Apply(
                CreateSnapshot(verifiedParent, guessedParent, child));

            Assert.AreEqual(string.Empty, reference.ChineseName.Value);
            Assert.AreEqual("发货指令ID", reference.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, reference.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260919 验证唯一生成实体主键签名可纠正同字段的弱命名关系，而不覆盖真实 SQL 关系。</summary>
        [TestMethod]
        public void Apply_EntityPrimaryKeySignature_WinsOverLogicalNameRelation()
        {
            TableMetadata verifiedParent = CreateTable("Sale_Value", "销售来源项");
            verifiedParent.Fields.Add(CreateField("SV_ID", false));
            TableMetadata guessedParent = CreateTable("SV", "服务对象");
            guessedParent.Fields.Add(CreateField("SV_ID", true));
            TableMetadata child = CreateTable("AcPro", "产品流水账");
            FieldMetadata reference = CreateField("SV_ID", false);
            child.Fields.Add(reference);
            child.Relations.Add(CreateRelation(guessedParent, child, "SV_ID", "SV_ID",
                ConfidenceStatus.Guessed, "LogicalForeignKeyName"));
            var evidence = new List<SourceEvidence>
            {
                CreateEntityPrimaryKeyEvidence("Sale_Value", "SV_ID")
            };

            new StructuralFieldNameInferenceService().Apply(
                CreateSnapshot(verifiedParent, guessedParent, child), evidence);

            Assert.AreEqual("销售来源项ID", reference.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, reference.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260917 验证同名字段的唯一已核验关系目标可补充无显式关系字段且不会受归档主键干扰。</summary>
        [TestMethod]
        public void Apply_UniqueRelationSignature_PropagatesSuggestionToSameNamedField()
        {
            TableMetadata parent = CreateTable("PFT", "文件目录树");
            parent.Fields.Add(CreateField("FI_ID", true));
            TableMetadata archive = CreateTable("File_Archive", "附件归档");
            archive.Fields.Add(CreateField("FI_ID", true));
            TableMetadata knownChild = CreateTable("FV", "文件版本");
            knownChild.Fields.Add(CreateField("FI_ID", false));
            knownChild.Relations.Add(CreateRelation(parent, knownChild, "FI_ID", "FI_ID",
                ConfidenceStatus.CodeEvidence, "SqlFieldRelation"));
            TableMetadata unknownChild = CreateTable("Au_File", "审核附件");
            FieldMetadata unknownField = CreateField("FI_ID", false);
            unknownChild.Fields.Add(unknownField);

            new StructuralFieldNameInferenceService().Apply(
                CreateSnapshot(parent, archive, knownChild, unknownChild));

            Assert.AreEqual(string.Empty, unknownField.ChineseName.Value);
            Assert.AreEqual("文件目录树ID", unknownField.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, unknownField.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260917 验证纯命名关系不能跨表传播，避免一个弱推测污染所有同名字段。</summary>
        [TestMethod]
        public void Apply_GuessedRelationSignature_DoesNotPropagateToOtherTables()
        {
            TableMetadata parent = CreateTable("PFT", "文件目录树");
            parent.Fields.Add(CreateField("FI_ID", true));
            TableMetadata archive = CreateTable("File_Archive", "附件归档");
            archive.Fields.Add(CreateField("FI_ID", true));
            TableMetadata knownChild = CreateTable("MyCare", "我的关注");
            knownChild.Fields.Add(CreateField("FI_ID", false));
            knownChild.Relations.Add(CreateRelation(parent, knownChild, "FI_ID", "FI_ID",
                ConfidenceStatus.Guessed, "LogicalForeignKeyName"));
            TableMetadata unknownChild = CreateTable("Au_File", "审核附件");
            FieldMetadata unknownField = CreateField("FI_ID", false);
            unknownChild.Fields.Add(unknownField);

            new StructuralFieldNameInferenceService().Apply(
                CreateSnapshot(parent, archive, knownChild, unknownChild));

            Assert.IsNull(unknownField.SuggestedChineseName);
        }

        /// <summary>XMZADD 20260917 创建带可靠知识库正式名的测试表。</summary>
        private static TableMetadata CreateTable(string objectName, string chineseName)
        {
            return new TableMetadata
            {
                ScopeKey = "github-shared-dictionary",
                SchemaName = "dbo",
                ObjectName = objectName,
                ChineseName = new MetadataValue
                {
                    Value = chineseName,
                    Status = ConfidenceStatus.KnowledgeBaseEvidence,
                    ConfidenceScore = 98,
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem
                        {
                            SourceType = "EOS知识库",
                            SourcePath = "docs_knowledge/01_数据库表结构/_00_表索引.md",
                            SourceLine = 10,
                            RuleName = "ExactProjectTable"
                        }
                    }
                }
            };
        }

        /// <summary>XMZADD 20260917 创建具有空名称和指定主键属性的测试字段。</summary>
        private static FieldMetadata CreateField(string fieldName, bool isPrimaryKey)
        {
            return new FieldMetadata
            {
                FieldName = fieldName,
                IsPrimaryKey = isPrimaryKey,
                ChineseName = new MetadataValue { Value = string.Empty }
            };
        }

        /// <summary>XMZADD 20260917 创建带可追溯关系规则的测试父子字段映射。</summary>
        private static RelationMetadata CreateRelation(TableMetadata parent, TableMetadata child,
            string parentField, string childField, ConfidenceStatus status, string ruleName)
        {
            return new RelationMetadata
            {
                ScopeKey = child.ScopeKey,
                ParentSchemaName = parent.SchemaName,
                ParentTableName = parent.ObjectName,
                ParentFieldName = parentField,
                ChildSchemaName = child.SchemaName,
                ChildTableName = child.ObjectName,
                ChildFieldName = childField,
                RelationType = new MetadataValue
                {
                    Value = status == ConfidenceStatus.Guessed ? "推测：多对一" : "多对一",
                    Status = status,
                    ConfidenceScore = status == ConfidenceStatus.Guessed ? 70 : 96,
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem
                        {
                            SourceType = "测试关系",
                            SourcePath = "ERP/Test/Relation.vb",
                            SourceLine = 20,
                            RuleName = ruleName,
                            OriginalText = child.ObjectName + "." + childField + " -> " +
                                parent.ObjectName + "." + parentField
                        }
                    }
                }
            };
        }

        /// <summary>XMZADD 20260918 创建由 EOS 生成实体 mIDCol 声明提供的代码级主键测试证据。</summary>
        private static SourceEvidence CreateEntityPrimaryKeyEvidence(string objectName, string fieldName)
        {
            return new SourceEvidence
            {
                ObjectName = objectName,
                FieldName = fieldName,
                Origin = SourceEvidenceOrigin.GeneratedEntity,
                Strength = SourceEvidenceStrength.Authoritative,
                UsageKind = SourceUsageKind.Unknown,
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS生成实体",
                    SourcePath = "ERP/表-类定义/code_Test.vb",
                    SourceLine = 20,
                    RuleName = "EntityPrimaryKey",
                    OriginalText = "Shared ReadOnly mIDCol As String = \"" + fieldName + "\""
                }
            };
        }

        /// <summary>XMZADD 20260917 创建仅包含指定测试表的快照。</summary>
        private static SnapshotData CreateSnapshot(params TableMetadata[] tables)
        {
            return new SnapshotData { Tables = new List<TableMetadata>(tables) };
        }
    }
}
