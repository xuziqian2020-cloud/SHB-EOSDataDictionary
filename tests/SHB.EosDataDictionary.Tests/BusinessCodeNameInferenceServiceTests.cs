using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260904 验证中文名称优先来自所属实体和真实业务代码用途。</summary>
    [TestClass]
    public sealed class BusinessCodeNameInferenceServiceTests
    {
        /// <summary>XMZADD 20260904 验证仓储区配置实体按动态建表用途生成准确中文名称和模块。</summary>
        [TestMethod]
        public void Apply_StoragePartDefinition_UsesDynamicTableBusinessContext()
        {
            SnapshotData snapshot = CreateStorageSnapshot();
            IList<SourceEvidence> evidence = CreateStorageEvidence();

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            TableMetadata table = snapshot.Tables[0];
            Assert.AreEqual("仓储区分表配置", table.ChineseName.Value);
            Assert.AreEqual("仓储与库存", table.ModuleName.Value);
            Assert.AreEqual("仓储区分表配置ID", FindField(table, "ASPD_ID").ChineseName.Value);
            Assert.AreEqual("事业部ID", FindField(table, "Bu_ID").ChineseName.Value);
            Assert.AreEqual("仓储区出入库流水表名", FindField(table, "Table_Storage_Account").ChineseName.Value);
            Assert.AreEqual("仓储区事件表名", FindField(table, "Table_Storage_Event").ChineseName.Value);
            Assert.AreEqual("仓储区事件明细表名", FindField(table, "Table_Storage_Event_Item").ChineseName.Value);
            Assert.AreEqual("仓储区库存表名", FindField(table, "Table_Storage_Inv").ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, table.ChineseName.Status);
            Assert.IsNotNull(table.ChineseName.Evidence);
            Assert.AreEqual("BusinessUsageContext", table.ChineseName.Evidence[0].RuleName);
            Assert.AreEqual("ERP/表-类定义/code_Storage_Partial.vb", table.ChineseName.Evidence[0].SourcePath);
        }

        /// <summary>XMZADD 20260904 验证实体首字母字段前缀可恢复为所属业务对象中文名。</summary>
        [DataTestMethod]
        [DataRow("Account_Storage_Part_Definition", "仓储区分表配置", "ASPD_ID", "仓储区分表配置ID")]
        [DataRow("Purchase_Order_Item", "采购订单明细表", "POI_ID", "采购订单明细ID")]
        public void Apply_EntityInitialsExpandToOwnerBusinessName(
            string tableName, string tableChineseName, string fieldName, string expected)
        {
            var table = new TableMetadata
            {
                ObjectName = tableName,
                ChineseName = new MetadataValue { Value = tableChineseName, Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = fieldName,
                        ChineseName = new MetadataValue { Value = "旧推测", Status = ConfidenceStatus.Guessed }
                    }
                }
            };
            var snapshot = new SnapshotData { Tables = new List<TableMetadata> { table } };
            var evidence = new List<SourceEvidence>
            {
                CreateEvidence(tableName, null, "EntityClassConvention")
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.AreEqual(expected, table.Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, table.Fields[0].ChineseName.Status);
        }

        /// <summary>XMZADD 20260904 验证带数字业务段不参与首字母缩写，但不会吞掉数字前的大写业务段。</summary>
        [DataTestMethod]
        [DataRow("Storage_Part2_Event")]
        [DataRow("Storage_PART2_Event")]
        public void Apply_EntityInitialsWithNumericPart_ExpandsOwnerBusinessName(string tableName)
        {
            var table = new TableMetadata
            {
                ObjectName = tableName,
                ChineseName = new MetadataValue { Value = "仓储分区事件表", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = "SPE_ID",
                        ChineseName = new MetadataValue { Value = "旧推测", Status = ConfidenceStatus.Guessed }
                    }
                }
            };
            var snapshot = new SnapshotData { Tables = new List<TableMetadata> { table } };
            var evidence = new List<SourceEvidence>
            {
                CreateEvidence(tableName, null, "EntityClassConvention")
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.AreEqual("仓储分区事件ID", table.Fields[0].ChineseName.Value);
        }

        /// <summary>XMZADD 20260904 验证实体首字母匹配但字段后缀完全未知时保留待复核名称，避免丢后缀形成高置信误名。</summary>
        [DataTestMethod]
        [DataRow("POI_XYZ")]
        [DataRow("POI_XYZ_ID")]
        [DataRow("POI_ABC_STATUS")]
        public void Apply_EntityInitialsWithUnknownSuffix_PreservesPendingName(string fieldName)
        {
            var table = new TableMetadata
            {
                ObjectName = "Purchase_Order_Item",
                ChineseName = new MetadataValue { Value = "采购订单明细表", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = fieldName,
                        ChineseName = new MetadataValue { Value = "待复核", Status = ConfidenceStatus.Guessed }
                    }
                }
            };
            var snapshot = new SnapshotData { Tables = new List<TableMetadata> { table } };
            var evidence = new List<SourceEvidence>
            {
                CreateEvidence(table.ObjectName, null, "EntityClassConvention")
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.AreEqual("待复核", table.Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, table.Fields[0].ChineseName.Status);
        }

        /// <summary>XMZADD 20260904 验证业务代码推断绝不覆盖人工、锁定或数据库明确名称。</summary>
        [TestMethod]
        public void Apply_AuthoritativeNames_RemainUnchanged()
        {
            SnapshotData snapshot = CreateStorageSnapshot();
            snapshot.Tables[0].ChineseName = new MetadataValue
            {
                Value = "数据库仓储配置",
                Status = ConfidenceStatus.DatabaseEvidence
            };
            FieldMetadata field = FindField(snapshot.Tables[0], "ASPD_ID");
            field.ChineseName = new MetadataValue
            {
                Value = "人工配置主键",
                Status = ConfidenceStatus.LocalOverride,
                IsManualOverride = true
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, CreateStorageEvidence());

            Assert.AreEqual("数据库仓储配置", snapshot.Tables[0].ChineseName.Value);
            Assert.AreEqual("人工配置主键", field.ChineseName.Value);
        }

        /// <summary>XMZADD 20260905 验证实体、关系和界面绑定证据共同把不完整数据库名称细化为货主公司ID。</summary>
        [TestMethod]
        public void Apply_DaAcceptanceOwnerCompany_AggregatesEvidenceAndRefinesDatabaseName()
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            IList<SourceEvidence> evidence = CreateAcceptanceEvidence();

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            FieldMetadata field = snapshot.Tables[0].Fields[0];
            Assert.AreEqual("货主公司ID", field.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, field.ChineseName.Status);
            Assert.IsTrue(field.ChineseName.ConfidenceScore >= 90);
            Assert.AreEqual("公司ID", field.ChineseName.OriginalAutomaticValue);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/表-类定义/code_DA.vb", 321);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/DA/frmAcceptance.vb", 88);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/DA/frmAcceptance.vb", 96);
            StringAssert.Contains(field.ChineseName.SourceSummary, "ERP/表-类定义/code_DA.vb:321");
            StringAssert.Contains(field.ChineseName.SourceSummary, "ERP/DA/frmAcceptance.vb:88");
            StringAssert.Contains(field.ChineseName.SourceSummary, "ERP/DA/frmAcceptance.vb:96");
            Assert.IsNotNull(field.Usage);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, field.Usage.Status);
            StringAssert.Contains(field.Usage.Value, "界面展示");
            StringAssert.Contains(field.Usage.Value, "关联查询");
            StringAssert.Contains(field.Usage.Value, "实体字段定义");
        }

        /// <summary>XMZADD 20260905 验证弱状态同名中文经有效源码佐证后晋升且不被误记为名称变更。</summary>
        [DataTestMethod]
        [DataRow(ConfidenceStatus.Guessed, null)]
        [DataRow(ConfidenceStatus.PendingConfirmation, "首次自动翻译")]
        public void Apply_DaAcceptanceOwnerCompany_SameWeakNamePromotesEvidenceWithoutFakeRename(
            ConfidenceStatus status, string originalAutomaticValue)
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot("货主公司ID", status);
            FieldMetadata field = snapshot.Tables[0].Fields[0];
            field.ChineseName.ConfidenceScore = 52;
            field.ChineseName.OriginalAutomaticValue = originalAutomaticValue;

            new BusinessCodeNameInferenceService().Apply(snapshot, CreateAcceptanceEvidence());

            Assert.AreEqual("货主公司ID", field.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, field.ChineseName.Status);
            Assert.AreEqual("EOS业务代码", field.ChineseName.SourceType);
            Assert.IsTrue(field.ChineseName.ConfidenceScore >= 90);
            Assert.AreEqual(originalAutomaticValue, field.ChineseName.OriginalAutomaticValue);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/表-类定义/code_DA.vb", 321);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/DA/frmAcceptance.vb", 88);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/DA/frmAcceptance.vb", 96);
            StringAssert.Contains(field.ChineseName.SourceSummary, "ERP/表-类定义/code_DA.vb:321");
            StringAssert.Contains(field.ChineseName.SourceSummary, "ERP/DA/frmAcceptance.vb:88");
            StringAssert.Contains(field.ChineseName.SourceSummary, "ERP/DA/frmAcceptance.vb:96");
            Assert.IsNotNull(field.Usage);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, field.Usage.Status);
            StringAssert.Contains(field.Usage.Value, "界面展示");
            StringAssert.Contains(field.Usage.Value, "关联查询");
            StringAssert.Contains(field.Usage.Value, "实体字段定义");
        }

        /// <summary>XMZADD 20260905 验证实体字段结构和一次实体赋值不能冒充两类独立业务证据。</summary>
        [TestMethod]
        public void Apply_DaAcceptanceOwnerCompany_StructuralAndAssignmentEvidenceDoNotInflateCount()
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            FieldMetadata field = snapshot.Tables[0].Fields[0];
            field.ChineseName.ConfidenceScore = 81;
            var evidence = new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", null, "EntityClassConvention",
                    "ERP/表-类定义/code_DA.vb", 300, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "EntityProperty",
                    "ERP/表-类定义/code_DA.vb", 321, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "EntityFieldAssignment",
                    "ERP/DA/frmAcceptance.vb", 70, null, null, null)
            };
            evidence[2].BusinessIdentifierCandidate = "Owner_Company_ID";

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.AreEqual("公司ID", field.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, field.ChineseName.Status);
            Assert.AreEqual(81, field.ChineseName.ConfidenceScore);
        }

        /// <summary>XMZADD 20260905 验证与当前候选无关的动态配置记录不增加独立业务证据数量。</summary>
        [TestMethod]
        public void Apply_DaAcceptanceOwnerCompany_UnrelatedDynamicUsageDoesNotSupportCandidate()
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            var evidence = new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", null, "EntityClassConvention",
                    "ERP/表-类定义/code_DA.vb", 300, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "EntityFieldAssignment",
                    "ERP/DA/frmAcceptance.vb", 70, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "DynamicTableFieldUsage",
                    "ERP/DA/frmAcceptance.vb", 71, null, null, null)
            };
            evidence[1].BusinessIdentifierCandidate = "Owner_Company_ID";
            evidence[2].BusinessIdentifierCandidate = "UnrelatedTemporaryTable";

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.AreEqual("公司ID", snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, snapshot.Tables[0].Fields[0].ChineseName.Status);
        }

        /// <summary>XMZADD 20260905 验证关系和界面绑定各自可直接佐证数据库简称细化。</summary>
        [TestMethod]
        public void Apply_DaAcceptanceOwnerCompany_RelationOrCaptionDirectEvidenceCanRefine()
        {
            SnapshotData relationSnapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            var relationEvidence = new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", null, "EntityClassConvention",
                    "ERP/表-类定义/code_DA.vb", 300, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "EntityProperty",
                    "ERP/表-类定义/code_DA.vb", 321, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "SqlFieldRelation",
                    "ERP/DA/frmAcceptance.vb", 88, null, "Company", "Company_ID")
            };

            new BusinessCodeNameInferenceService().Apply(relationSnapshot, relationEvidence);

            Assert.AreEqual("货主公司ID", relationSnapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, relationSnapshot.Tables[0].Fields[0].ChineseName.Status);

            SnapshotData captionSnapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            var captionEvidence = new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", null, "EntityClassConvention",
                    "ERP/表-类定义/code_DA.vb", 300, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "EntityProperty",
                    "ERP/表-类定义/code_DA.vb", 321, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "GridColumnCaption",
                    "ERP/DA/frmAcceptance.vb", 96, "货主公司", null, null)
            };

            new BusinessCodeNameInferenceService().Apply(captionSnapshot, captionEvidence);

            Assert.AreEqual("货主公司ID", captionSnapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, captionSnapshot.Tables[0].Fields[0].ChineseName.Status);
        }

        /// <summary>XMZADD 20260905 验证两种控件标题规则归并为一个界面绑定证据类别。</summary>
        [TestMethod]
        public void Apply_DaAcceptanceOwnerCompany_GridAndDataCaptionsCountAsOneEvidenceKind()
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            var evidence = new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", null, "EntityClassConvention",
                    "ERP/表-类定义/code_DA.vb", 300, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "EntityProperty",
                    "ERP/表-类定义/code_DA.vb", 321, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "GridColumnCaption",
                    "ERP/DA/frmAcceptance.vb", 96, "货主公司", null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "DataColumnCaption",
                    "ERP/DA/frmAcceptance.vb", 97, "货主公司", null, null)
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            FieldMetadata field = snapshot.Tables[0].Fields[0];
            Assert.AreEqual("货主公司ID", field.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, field.ChineseName.Status);
            Assert.AreEqual(88, field.ChineseName.ConfidenceScore);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/DA/frmAcceptance.vb", 96);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/DA/frmAcceptance.vb", 97);
        }

        /// <summary>XMZADD 20260905 验证货主候选不覆盖人工名称及与候选不构成完整细化关系的数据库名称。</summary>
        [DataTestMethod]
        [DataRow("结算公司ID", ConfidenceStatus.LocalOverride, true)]
        [DataRow("结算公司ID", ConfidenceStatus.DatabaseEvidence, false)]
        public void Apply_DaAcceptanceOwnerCompany_PreservesManualAndConflictingDatabaseName(
            string existingName, ConfidenceStatus status, bool isManual)
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot(existingName, status);
            snapshot.Tables[0].Fields[0].ChineseName.IsManualOverride = isManual;

            new BusinessCodeNameInferenceService().Apply(snapshot, CreateAcceptanceEvidence());

            Assert.AreEqual(existingName, snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual(status, snapshot.Tables[0].Fields[0].ChineseName.Status);
        }

        /// <summary>XMZADD 20260905 验证无表名界面标题只绑定同文件唯一字段归属，多候选时保留冲突且不误改名称。</summary>
        [TestMethod]
        public void Apply_UnboundCaption_UsesUniqueOwnerAndRecordsAmbiguousConflict()
        {
            SnapshotData uniqueSnapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            IList<SourceEvidence> uniqueEvidence = CreateAcceptanceEvidence();
            uniqueEvidence.Add(CreateDetailedEvidence(null, "Owner_Company_ID", "GridColumnCaption",
                "ERP/DA/frmUnique.vb", 20, "货主公司", null, null));
            uniqueEvidence.Add(CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage",
                "ERP/DA/frmUnique.vb", 10, null, null, null));

            new BusinessCodeNameInferenceService().Apply(uniqueSnapshot, uniqueEvidence);

            Assert.AreEqual("货主公司ID", uniqueSnapshot.Tables[0].Fields[0].ChineseName.Value);
            AssertEvidenceLocation(uniqueSnapshot.Tables[0].Fields[0].ChineseName.Evidence,
                "ERP/DA/frmUnique.vb", 20);

            var secondTable = new TableMetadata
            {
                ObjectName = "Other_Acceptance",
                ChineseName = new MetadataValue { Value = "其他承兑表", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = "Owner_Company_ID",
                        ChineseName = new MetadataValue { Value = "其他公司ID", Status = ConfidenceStatus.DatabaseEvidence }
                    }
                }
            };
            SnapshotData ambiguousSnapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            ambiguousSnapshot.Tables.Add(secondTable);
            IList<SourceEvidence> ambiguousEvidence = CreateAcceptanceEvidence();
            ambiguousEvidence.Add(CreateEvidence("Other_Acceptance", null, "EntityClassConvention"));
            ambiguousEvidence.Add(CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "EntityProperty",
                "ERP/DA/frmAmbiguous.vb", 10, null, null, null));
            ambiguousEvidence.Add(CreateDetailedEvidence("Other_Acceptance", "Owner_Company_ID", "EntityProperty",
                "ERP/DA/frmAmbiguous.vb", 11, null, null, null));
            ambiguousEvidence.Add(CreateDetailedEvidence(null, "Owner_Company_ID", "GridColumnCaption",
                "ERP/DA/frmAmbiguous.vb", 20, "货主公司", null, null));

            new BusinessCodeNameInferenceService().Apply(ambiguousSnapshot, ambiguousEvidence);

            Assert.AreEqual("其他公司ID", secondTable.Fields[0].ChineseName.Value);
            Assert.IsTrue(ContainsEvidenceRule(ambiguousEvidence, "GridColumnCaptionConflict"));
        }

        /// <summary>XMZADD 20260914 验证 SQL 直接列中文别名参与字段命名并保留真实来源分类。</summary>
        [TestMethod]
        public void Apply_SqlColumnAlias_UsesCaptionAndPreservesSourceType()
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            var evidence = new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", null, "EntityClassConvention",
                    "ERP/表-类定义/code_DA.vb", 300, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "EntityProperty",
                    "ERP/表-类定义/code_DA.vb", 321, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "SqlColumnAlias",
                    "ERP/DA/AcceptanceQuery.vb", 42, "货主公司ID", null, null)
            };
            evidence[2].Strength = SourceEvidenceStrength.DirectBusinessCode;
            evidence[2].UsageKind = SourceUsageKind.Display;
            evidence[2].Evidence.SourceType = "EOS业务源码";

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            FieldMetadata field = snapshot.Tables[0].Fields[0];
            Assert.AreEqual("货主公司ID", field.ChineseName.Value);
            Assert.AreEqual("SqlColumnAlias", field.ChineseName.Evidence[0].RuleName);
            Assert.AreEqual("EOS业务源码", field.ChineseName.Evidence[0].SourceType);
            StringAssert.Contains(field.Usage.Value, "界面展示");
        }

        /// <summary>XMZADD 20260914 验证两个独立业务文件的一致 SQL 别名保留双份证据并提升可信分数。</summary>
        [TestMethod]
        public void Apply_SqlColumnAlias_TwoIndependentFiles_RaisesConfidence()
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot("旧推测", ConfidenceStatus.Guessed);
            var evidence = new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "SqlColumnAlias",
                    "ERP/DA/AcceptanceQuery.vb", 42, "货主公司ID", null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "SqlColumnAlias",
                    "ERP/DA/AcceptanceExport.vb", 87, "货主公司ID", null, null)
            };
            evidence[0].Strength = SourceEvidenceStrength.DirectBusinessCode;
            evidence[0].UsageKind = SourceUsageKind.Display;
            evidence[1].Strength = SourceEvidenceStrength.DirectBusinessCode;
            evidence[1].UsageKind = SourceUsageKind.Display;
            evidence[0].Evidence.OriginalText =
                "SELECT A.Owner_Company_ID AS 货主公司ID FROM DA_Acceptance A";
            evidence[1].Evidence.OriginalText =
                "SELECT D.Owner_Company_ID AS 货主公司ID FROM DA_Acceptance D WHERE D.Deleted=0";

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            FieldMetadata field = snapshot.Tables[0].Fields[0];
            Assert.AreEqual("货主公司ID", field.ChineseName.Value);
            Assert.IsTrue(field.ChineseName.ConfidenceScore >= 90);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/DA/AcceptanceQuery.vb", 42);
            AssertEvidenceLocation(field.ChineseName.Evidence, "ERP/DA/AcceptanceExport.vb", 87);
        }

        /// <summary>XMZADD 20260915 验证复制到不同文件的相同 SQL 不会被误算为两份独立标题证据。</summary>
        [TestMethod]
        public void Apply_SqlColumnAlias_CopiedSqlAcrossFiles_DoesNotRaiseConfidence()
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot("旧推测", ConfidenceStatus.Guessed);
            var evidence = new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "SqlColumnAlias",
                    "ERP/DA/AcceptanceQuery.vb", 42, "货主公司ID", null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "SqlColumnAlias",
                    "ERP/DA/AcceptanceQueryCopy.vb", 42, "货主公司ID", null, null)
            };
            const string copiedSql =
                "SELECT A.Owner_Company_ID AS 货主公司ID FROM DA_Acceptance A";
            for (int index = 0; index < evidence.Count; index++)
            {
                evidence[index].Strength = SourceEvidenceStrength.DirectBusinessCode;
                evidence[index].UsageKind = SourceUsageKind.Display;
                evidence[index].Evidence.OriginalText = copiedSql;
            }

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.AreEqual("货主公司ID", snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.IsTrue(snapshot.Tables[0].Fields[0].ChineseName.ConfidenceScore < 90);
        }

        /// <summary>XMZADD 20260914 验证无表名 SQL 别名发生多表冲突时保留原证据强度和展示用途。</summary>
        [TestMethod]
        public void Apply_UnboundSqlColumnAliasConflict_PreservesClassification()
        {
            SnapshotData snapshot = CreateAcceptanceSnapshot("公司ID", ConfidenceStatus.DatabaseEvidence);
            snapshot.Tables.Add(new TableMetadata
            {
                ObjectName = "Other_Acceptance",
                ChineseName = new MetadataValue { Value = "其他承兑表", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = "Owner_Company_ID",
                        ChineseName = new MetadataValue { Value = "其他公司ID", Status = ConfidenceStatus.DatabaseEvidence }
                    }
                }
            });
            var evidence = new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage",
                    "ERP/DA/Ambiguous.vb", 10, null, null, null),
                CreateDetailedEvidence("Other_Acceptance", "Owner_Company_ID", "SqlFieldUsage",
                    "ERP/DA/Ambiguous.vb", 11, null, null, null),
                CreateDetailedEvidence(null, "Owner_Company_ID", "SqlColumnAlias",
                    "ERP/DA/Ambiguous.vb", 20, "货主公司ID", null, null)
            };
            evidence[2].Strength = SourceEvidenceStrength.DirectBusinessCode;
            evidence[2].UsageKind = SourceUsageKind.Display;
            evidence[2].Evidence.SourceType = "EOS业务源码";

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            SourceEvidence conflict = FindSourceEvidenceByRule(evidence, "SqlColumnAliasConflict");
            Assert.IsNotNull(conflict);
            Assert.AreEqual(SourceEvidenceStrength.DirectBusinessCode, conflict.Strength);
            Assert.AreEqual(SourceUsageKind.Display, conflict.UsageKind);
            Assert.AreEqual("EOS业务源码", conflict.Evidence.SourceType);
        }

        /// <summary>XMZADD 20260905 验证仅有表实体证据时不改写未在实体中声明字段的既有稳定翻译。</summary>
        [TestMethod]
        public void Apply_UndeclaredField_PreservesExistingStableTranslation()
        {
            var table = new TableMetadata
            {
                ObjectName = "Item",
                ChineseName = new MetadataValue { Value = "物料主数据", Status = ConfidenceStatus.KnowledgeBaseEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = "KisNumber",
                        ChineseName = new MetadataValue { Value = "金蝶编码", Status = ConfidenceStatus.Guessed }
                    }
                }
            };
            var snapshot = new SnapshotData { Tables = new List<TableMetadata> { table } };
            var evidence = new List<SourceEvidence> { CreateEvidence("Item", null, "EntityClassConvention") };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.AreEqual("金蝶编码", table.Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, table.Fields[0].ChineseName.Status);
        }

        /// <summary>XMZADD 20260915 验证字段绑定的直接业务代码枚举进入快照并保留来源证据。</summary>
        [TestMethod]
        public void Apply_DirectFieldEnumeration_PublishesEnumItems()
        {
            SnapshotData snapshot = CreateEnumerationSnapshot();
            var evidence = new List<SourceEvidence>
            {
                CreateEnumerationEvidence("GridColumnDataMap", "0", "未审核",
                    "ERP/DA/AcceptanceQuery.vb", 20),
                CreateEnumerationEvidence("GridColumnDataMap", "1", "已审核",
                    "ERP/DA/AcceptanceQuery.vb", 21)
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            FieldMetadata field = snapshot.Tables[0].Fields[0];
            Assert.IsNotNull(field.EnumName);
            Assert.AreEqual("Status业务枚举", field.EnumName.Value);
            Assert.AreEqual(2, field.EnumItems.Count);
            EnumItemMetadata pending = FindEnumItem(field, "0", "未审核");
            Assert.IsNotNull(pending);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, pending.ChineseName.Status);
            AssertEvidenceLocation(pending.ChineseName.Evidence, "ERP/DA/AcceptanceQuery.vb", 20);
        }

        /// <summary>XMZADD 20260915 验证同一枚举含义的独立源码位置合并证据而不生成重复项。</summary>
        [TestMethod]
        public void Apply_DuplicateEnumerationMeaning_MergesEvidence()
        {
            SnapshotData snapshot = CreateEnumerationSnapshot();
            var evidence = new List<SourceEvidence>
            {
                CreateEnumerationEvidence("SqlCaseEnum", "1", "已审核",
                    "ERP/DA/AcceptanceQuery.vb", 20),
                CreateEnumerationEvidence("VbSelectCaseEnum", "1", "已审核",
                    "ERP/DA/AcceptanceStatus.vb", 35)
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            FieldMetadata field = snapshot.Tables[0].Fields[0];
            Assert.AreEqual(1, field.EnumItems.Count);
            Assert.AreEqual(2, field.EnumItems[0].ChineseName.Evidence.Count);
            AssertEvidenceLocation(field.EnumItems[0].ChineseName.Evidence,
                "ERP/DA/AcceptanceQuery.vb", 20);
            AssertEvidenceLocation(field.EnumItems[0].ChineseName.Evidence,
                "ERP/DA/AcceptanceStatus.vb", 35);
        }

        /// <summary>XMZADD 20260915 验证同一枚举值出现不同中文时保留全部候选并显式标记冲突。</summary>
        [TestMethod]
        public void Apply_ConflictingEnumerationMeaning_PreservesAllCandidates()
        {
            SnapshotData snapshot = CreateEnumerationSnapshot();
            var evidence = new List<SourceEvidence>
            {
                CreateEnumerationEvidence("SqlCaseEnum", "1", "已审核",
                    "ERP/DA/AcceptanceQuery.vb", 20),
                CreateEnumerationEvidence("GridColumnDataMap", "1", "审核通过",
                    "ERP/DA/AcceptanceEdit.vb", 35)
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            FieldMetadata field = snapshot.Tables[0].Fields[0];
            Assert.AreEqual(2, field.EnumItems.Count);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict,
                FindEnumItem(field, "1", "已审核").ChineseName.Status);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict,
                FindEnumItem(field, "1", "审核通过").ChineseName.Status);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict, field.EnumName.Status);
        }

        /// <summary>XMZADD 20260915 验证业务代码枚举冲突不能覆盖知识库或人工维护的既有含义。</summary>
        [TestMethod]
        public void Apply_AuthoritativeEnumeration_PreservesExistingCandidate()
        {
            SnapshotData snapshot = CreateEnumerationSnapshot();
            FieldMetadata field = snapshot.Tables[0].Fields[0];
            field.EnumName = new MetadataValue
            {
                Value = "验收状态枚举",
                Status = ConfidenceStatus.KnowledgeBaseEvidence,
                Evidence = new List<EvidenceItem> { new EvidenceItem { RuleName = "ProjectFieldEnum" } }
            };
            field.EnumItems.Add(new EnumItemMetadata
            {
                Value = "1",
                ChineseName = new MetadataValue
                {
                    Value = "已审核",
                    Status = ConfidenceStatus.KnowledgeBaseEvidence,
                    IsLocked = true,
                    Evidence = new List<EvidenceItem>()
                }
            });
            var evidence = new List<SourceEvidence>
            {
                CreateEnumerationEvidence("GridColumnDataMap", "1", "审核通过",
                    "ERP/DA/AcceptanceEdit.vb", 35)
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.AreEqual("验收状态枚举", field.EnumName.Value);
            Assert.AreEqual(ConfidenceStatus.KnowledgeBaseEvidence, field.EnumName.Status);
            Assert.AreEqual(2, field.EnumItems.Count);
            Assert.AreEqual(ConfidenceStatus.KnowledgeBaseEvidence,
                FindEnumItem(field, "1", "已审核").ChineseName.Status);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict,
                FindEnumItem(field, "1", "审核通过").ChineseName.Status);
        }

        /// <summary>XMZADD 20260915 验证实体属性声明的命名枚举只采用直接业务源码中的常量成员。</summary>
        [TestMethod]
        public void Apply_NamedEnumProperty_MapsBusinessEnumMembers()
        {
            SnapshotData snapshot = CreateEnumerationSnapshot();
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "DA_Acceptance",
                    FieldName = "Status",
                    PropertyTypeName = "AcceptanceStatus",
                    Strength = SourceEvidenceStrength.Authoritative,
                    Evidence = new EvidenceItem
                    {
                        SourcePath = "ERP/表-类定义/code_DA.vb",
                        SourceLine = 300,
                        RuleName = "EntityProperty"
                    }
                },
                new SourceEvidence
                {
                    EnumName = "AcceptanceStatus",
                    EnumValue = "Approved",
                    EnumRawValue = "1",
                    EnumChineseName = "已审核",
                    Strength = SourceEvidenceStrength.DirectBusinessCode,
                    UsageKind = SourceUsageKind.Enumeration,
                    Evidence = new EvidenceItem
                    {
                        SourcePath = "ERP/DA/AcceptanceStatus.vb",
                        SourceLine = 8,
                        RuleName = "EnumMember"
                    }
                },
                new SourceEvidence
                {
                    EnumName = "AcceptanceStatus",
                    EnumValue = "Dynamic",
                    EnumRawValue = "GetStatusValue()",
                    EnumChineseName = "动态状态",
                    Strength = SourceEvidenceStrength.DirectBusinessCode,
                    UsageKind = SourceUsageKind.Enumeration,
                    Evidence = new EvidenceItem
                    {
                        SourcePath = "ERP/DA/AcceptanceStatus.vb",
                        SourceLine = 9,
                        RuleName = "EnumMember"
                    }
                }
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            FieldMetadata field = snapshot.Tables[0].Fields[0];
            Assert.AreEqual("AcceptanceStatus", field.EnumName.Value);
            Assert.AreEqual(1, field.EnumItems.Count);
            Assert.IsNotNull(FindEnumItem(field, "1", "已审核"));
        }

        /// <summary>XMZADD 20260915 验证只读枚举集合在追加直接代码证据前被安全复制为可写集合。</summary>
        [TestMethod]
        public void Apply_ReadOnlyEnumerationCollection_IsSafelyExtended()
        {
            SnapshotData snapshot = CreateEnumerationSnapshot();
            snapshot.Tables[0].Fields[0].EnumItems = new EnumItemMetadata[0];
            var evidence = new List<SourceEvidence>
            {
                CreateEnumerationEvidence("SqlCaseEnum", "0", "未审核",
                    "ERP/DA/AcceptanceQuery.vb", 20)
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.AreEqual(1, snapshot.Tables[0].Fields[0].EnumItems.Count);
        }

        /// <summary>XMZADD 20260915 验证直接业务枚举允许括号内的必要限定语且不把它误判为说明句。</summary>
        [TestMethod]
        public void Apply_EnumerationCaptionWithQualifier_PublishesCompleteMeaning()
        {
            SnapshotData snapshot = CreateEnumerationSnapshot();
            var evidence = new List<SourceEvidence>
            {
                CreateEnumerationEvidence("VbSelectCaseEnum", "1", "有限公司(公共)",
                    "f_MIS/流水帐/frmDA_Sheet.vb", 4502)
            };

            new BusinessCodeNameInferenceService().Apply(snapshot, evidence);

            Assert.IsNotNull(FindEnumItem(
                snapshot.Tables[0].Fields[0], "1", "有限公司(公共)"));
        }

        /// <summary>XMZADD 20260904 创建截图对象对应的最小快照。</summary>
        private static SnapshotData CreateStorageSnapshot()
        {
            var table = new TableMetadata
            {
                ObjectName = "Account_Storage_Part_Definition",
                ChineseName = new MetadataValue
                {
                    Value = "仓储区定义",
                    Status = ConfidenceStatus.KnowledgeBaseEvidence,
                    Evidence = new List<EvidenceItem> { new EvidenceItem { RuleName = "ExactProjectTable" } }
                },
                ModuleName = new MetadataValue
                {
                    Value = "仓储",
                    Status = ConfidenceStatus.KnowledgeBaseEvidence,
                    Evidence = new List<EvidenceItem> { new EvidenceItem { RuleName = "ProjectModuleHeading" } }
                },
                Fields = new List<FieldMetadata>()
            };
            string[] fieldNames =
            {
                "ASPD_ID", "Bu_ID", "Table_Storage_Account", "Table_Storage_Event",
                "Table_Storage_Event_Item", "Table_Storage_Inv"
            };
            for (int index = 0; index < fieldNames.Length; index++)
            {
                table.Fields.Add(new FieldMetadata
                {
                    FieldName = fieldNames[index],
                    ChineseName = new MetadataValue { Value = fieldNames[index], Status = ConfidenceStatus.Guessed }
                });
            }
            return new SnapshotData { Tables = new List<TableMetadata> { table } };
        }

        /// <summary>XMZADD 20260915 创建只含验收状态字段的枚举富化快照。</summary>
        private static SnapshotData CreateEnumerationSnapshot()
        {
            return new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        SchemaName = "dbo",
                        ObjectName = "DA_Acceptance",
                        ChineseName = new MetadataValue
                        {
                            Value = "承兑验收表",
                            Status = ConfidenceStatus.CodeEvidence
                        },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "Status",
                                ChineseName = new MetadataValue
                                {
                                    Value = "验收状态",
                                    Status = ConfidenceStatus.CodeEvidence
                                },
                                EnumItems = new List<EnumItemMetadata>()
                            }
                        }
                    }
                }
            };
        }

        /// <summary>XMZADD 20260915 创建字段归属、枚举值、中文含义和源码位置完整的直接证据。</summary>
        private static SourceEvidence CreateEnumerationEvidence(string ruleName, string value,
            string chineseName, string sourcePath, int sourceLine)
        {
            return new SourceEvidence
            {
                ObjectName = "DA_Acceptance",
                FieldName = "Status",
                EnumValue = value,
                EnumChineseName = chineseName,
                Strength = SourceEvidenceStrength.DirectBusinessCode,
                UsageKind = SourceUsageKind.Enumeration,
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS业务源码",
                    SourcePath = sourcePath,
                    SourceLine = sourceLine,
                    RuleName = ruleName,
                    RawValue = value,
                    OriginalText = value + "=" + chineseName,
                    Explanation = "业务代码明确映射字段枚举值。"
                }
            };
        }

        /// <summary>XMZADD 20260904 创建仓储配置实体和动态表字段的源码证据。</summary>
        private static IList<SourceEvidence> CreateStorageEvidence()
        {
            return new List<SourceEvidence>
            {
                CreateEvidence("Account_Storage_Part_Definition", null, "EntityClassConvention"),
                CreateEvidence("Account_Storage_Part_Definition", "Table_Storage_Account", "DynamicTableFieldUsage"),
                CreateEvidence("Account_Storage_Part_Definition", "Table_Storage_Event", "DynamicTableFieldUsage"),
                CreateEvidence("Account_Storage_Part_Definition", "Table_Storage_Event_Item", "DynamicTableFieldUsage"),
                CreateEvidence("Account_Storage_Part_Definition", "Table_Storage_Inv", "DynamicTableFieldUsage")
            };
        }

        /// <summary>XMZADD 20260905 创建承兑表及不完整数据库字段名称夹具。</summary>
        private static SnapshotData CreateAcceptanceSnapshot(string fieldChineseName, ConfidenceStatus status)
        {
            return new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "DA_Acceptance",
                        ChineseName = new MetadataValue { Value = "承兑验收表", Status = ConfidenceStatus.CodeEvidence },
                        ModuleName = new MetadataValue { Value = "财务票据", Status = ConfidenceStatus.KnowledgeBaseEvidence },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "Owner_Company_ID",
                                DataType = "bigint",
                                ChineseName = new MetadataValue { Value = fieldChineseName, Status = status }
                            }
                        }
                    }
                }
            };
        }

        /// <summary>XMZADD 20260905 创建承兑实体、公司关系及货主公司列标题三种独立业务证据。</summary>
        private static IList<SourceEvidence> CreateAcceptanceEvidence()
        {
            return new List<SourceEvidence>
            {
                CreateDetailedEvidence("DA_Acceptance", null, "EntityClassConvention",
                    "ERP/表-类定义/code_DA.vb", 300, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "EntityProperty",
                    "ERP/表-类定义/code_DA.vb", 321, null, null, null),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "SqlFieldRelation",
                    "ERP/DA/frmAcceptance.vb", 88, null, "Company", "Company_ID"),
                CreateDetailedEvidence("DA_Acceptance", "Owner_Company_ID", "GridColumnCaption",
                    "ERP/DA/frmAcceptance.vb", 96, "货主公司", null, null)
            };
        }

        /// <summary>XMZADD 20260905 创建包含候选、关系目标和真实源码位置的字段证据。</summary>
        private static SourceEvidence CreateDetailedEvidence(string objectName, string fieldName, string ruleName,
            string sourcePath, int sourceLine, string chineseNameCandidate,
            string relationTargetObjectName, string relationTargetFieldName)
        {
            return new SourceEvidence
            {
                ObjectName = objectName,
                FieldName = fieldName,
                EntityName = string.IsNullOrWhiteSpace(objectName) ? null : "t_" + objectName,
                ModulePath = "ERP/DA",
                ChineseNameCandidate = chineseNameCandidate,
                RelationTargetObjectName = relationTargetObjectName,
                RelationTargetFieldName = relationTargetFieldName,
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS源码",
                    SourcePath = sourcePath,
                    SourceLine = sourceLine,
                    RuleName = ruleName,
                    RawValue = fieldName ?? objectName,
                    OriginalText = "业务源码行",
                    Explanation = "业务代码上下文"
                }
            };
        }

        /// <summary>XMZADD 20260904 创建带源码位置的业务上下文证据。</summary>
        private static SourceEvidence CreateEvidence(string objectName, string fieldName, string ruleName)
        {
            return new SourceEvidence
            {
                ObjectName = objectName,
                FieldName = fieldName,
                EntityName = "t_" + objectName,
                ModulePath = "ERP/表-类定义",
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS源码",
                    SourcePath = "ERP/表-类定义/code_Storage_Partial.vb",
                    SourceLine = 1509,
                    RuleName = ruleName,
                    RawValue = fieldName ?? objectName,
                    Explanation = "业务代码上下文"
                }
            };
        }

        /// <summary>XMZADD 20260904 按字段名定位上下文推断结果。</summary>
        private static FieldMetadata FindField(TableMetadata table, string fieldName)
        {
            for (int index = 0; index < table.Fields.Count; index++)
            {
                if (string.Equals(table.Fields[index].FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return table.Fields[index];
                }
            }
            Assert.Fail("未找到字段：" + fieldName);
            return null;
        }

        /// <summary>XMZADD 20260915 按值和中文含义定位字段枚举候选。</summary>
        private static EnumItemMetadata FindEnumItem(FieldMetadata field, string value, string chineseName)
        {
            for (int index = 0; index < field.EnumItems.Count; index++)
            {
                EnumItemMetadata item = field.EnumItems[index];
                if (item != null && item.ChineseName != null &&
                    string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.ChineseName.Value, chineseName, StringComparison.Ordinal))
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260905 断言聚合结果保留指定源码路径和行号。</summary>
        private static void AssertEvidenceLocation(IList<EvidenceItem> evidence, string sourcePath, int sourceLine)
        {
            for (int index = 0; index < evidence.Count; index++)
            {
                if (string.Equals(evidence[index].SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
                    evidence[index].SourceLine == sourceLine)
                {
                    return;
                }
            }
            Assert.Fail("未找到证据位置：" + sourcePath + ":" + sourceLine);
        }

        /// <summary>XMZADD 20260905 判断源码证据集合是否包含指定冲突规则。</summary>
        private static bool ContainsEvidenceRule(IList<SourceEvidence> evidence, string ruleName)
        {
            for (int index = 0; index < evidence.Count; index++)
            {
                if (evidence[index] != null && evidence[index].Evidence != null &&
                    string.Equals(evidence[index].Evidence.RuleName, ruleName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260914 按规则返回一条源码证据以核对冲突分类传播。</summary>
        private static SourceEvidence FindSourceEvidenceByRule(IList<SourceEvidence> evidence, string ruleName)
        {
            for (int index = 0; index < evidence.Count; index++)
            {
                if (evidence[index] != null && evidence[index].Evidence != null &&
                    string.Equals(evidence[index].Evidence.RuleName, ruleName, StringComparison.Ordinal))
                {
                    return evidence[index];
                }
            }
            return null;
        }
    }
}
