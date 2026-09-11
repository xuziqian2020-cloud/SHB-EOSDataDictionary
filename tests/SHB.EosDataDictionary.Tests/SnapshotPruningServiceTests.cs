using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class SnapshotPruningServiceTests
    {
        /// <summary>XMZADD 20260902 验证完整公开快照保留空表、技术表、人工排除表和未分类表，仅排除视图。</summary>
        [TestMethod]
        public void PruneCompletePublicSnapshot_AllUserTablesAreRetainedAndViewIsExcluded()
        {
            TableMetadata emptyTable = CreateTable("T_EMPTY", "TABLE", 0L, DictionaryTableCategory.Business);
            TableMetadata technicalTable = CreateTable("T_TECH", "TABLE", 10L, DictionaryTableCategory.Technical);
            TableMetadata manuallyExcludedTable = CreateTable("T_MANUAL_EXCLUDED", "TABLE", 10L, DictionaryTableCategory.Excluded);
            TableMetadata unclassifiedTable = CreateTable("T_UNCLASSIFIED", "TABLE", 10L, DictionaryTableCategory.Unclassified);
            TableMetadata view = CreateTable("V_ORDER", "VIEW", 10L, DictionaryTableCategory.Business);

            SnapshotPruningResult result = new SnapshotPruningService().PruneCompletePublicSnapshot(
                CreateSnapshot(emptyTable, technicalTable, manuallyExcludedTable, unclassifiedTable, view));

            Assert.AreEqual(4, result.Snapshot.Tables.Count);
            Assert.IsNotNull(FindTable(result.Snapshot, "dbo", "T_EMPTY"));
            Assert.IsNotNull(FindTable(result.Snapshot, "dbo", "T_TECH"));
            Assert.IsNotNull(FindTable(result.Snapshot, "dbo", "T_MANUAL_EXCLUDED"));
            Assert.IsNotNull(FindTable(result.Snapshot, "dbo", "T_UNCLASSIFIED"));
            Assert.AreEqual(1, result.ExcludedObjects.Count);
            Assert.AreEqual("dbo.V_ORDER", result.ExcludedObjects[0].ObjectKey);
            Assert.AreEqual("ViewExcluded", result.ExcludedObjects[0].ReasonCode);
        }

        /// <summary>XMZADD 20260902 验证完整公开快照遇到无可用字段的用户表时明确失败，避免静默丢失对象。</summary>
        [TestMethod]
        public void PruneCompletePublicSnapshot_TableWithoutUsableFields_ThrowsFixedError()
        {
            TableMetadata table = CreateTable("T_NO_FIELDS", "TABLE", 10L, DictionaryTableCategory.Business);
            table.Fields.Clear();

            InvalidDataException exception = Assert.ThrowsException<InvalidDataException>(delegate
            {
                new SnapshotPruningService().PruneCompletePublicSnapshot(CreateSnapshot(table));
            });

            Assert.AreEqual("完整公开快照中的用户表没有可用字段。", exception.Message);
        }

        /// <summary>XMZADD 20260901 验证人工命名和分类不能让视图进入规范数据表快照。</summary>
        [TestMethod]
        public void Prune_View_IsAlwaysExcludedEvenWhenManuallyNamed()
        {
            TableMetadata view = CreateTable("VW_ORDER", "VIEW", 10L, DictionaryTableCategory.Business);
            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(view));

            Assert.AreEqual(0, result.Snapshot.Tables.Count);
            Assert.AreEqual(1, result.ExcludedObjects.Count);
            Assert.AreEqual("ViewExcluded", result.ExcludedObjects[0].ReasonCode);
        }

        /// <summary>XMZADD 20260901 验证清理快照复制元数据时不会丢失等号名称的完整原始依据。</summary>
        [TestMethod]
        public void Prune_RetainedMetadata_PreservesOriginalEvidenceText()
        {
            TableMetadata table = CreateTable("T_ORDER", "TABLE", 10L, DictionaryTableCategory.Business);
            table.ChineseName = new MetadataValue
            {
                Value = "订单",
                Status = ConfidenceStatus.CodeEvidence,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem { OriginalText = "T_ORDER=订单", RawValue = "T_ORDER=订单" }
                }
            };

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(table));

            Assert.AreEqual("T_ORDER=订单", result.Snapshot.Tables[0].ChineseName.Evidence[0].OriginalText);
        }

        /// <summary>XMZADD 20260910 验证裁剪深复制正式名称之外的参考层及其审阅证据。</summary>
        [TestMethod]
        public void Prune_RetainedNewNameLayers_PreservesValuesAndEvidence()
        {
            TableMetadata table = CreateTable("T_ORDER", "TABLE", 10L, DictionaryTableCategory.Business);
            FieldMetadata field = table.Fields[0];
            table.SuggestedChineseName = CreateNameValue("订单参考名", "C:\\audit\\table.vb");
            table.AlternativeChineseNames.Add(CreateNameValue("订单单据", "Table/Order.vb"));
            table.RejectedSuggestionFingerprints.Add("table-rejected");
            table.UsedByModules.Add(CreateNameValue("销售模块", "Module/Sales.vb"));
            field.SuggestedChineseName = CreateNameValue("订单内码参考名", "C:\\audit\\field.vb");
            field.AlternativeChineseNames.Add(CreateNameValue("订单主键", "Field/Order.vb"));
            field.RejectedSuggestionFingerprints.Add("field-rejected");

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(table));
            TableMetadata copiedTable = result.Snapshot.Tables[0];
            FieldMetadata copiedField = copiedTable.Fields[0];

            Assert.AreEqual("订单参考名", copiedTable.SuggestedChineseName.Value);
            Assert.AreEqual("C:\\audit\\table.vb", copiedTable.SuggestedChineseName.Evidence[0].SourcePath);
            Assert.AreEqual("订单单据", copiedTable.AlternativeChineseNames[0].Value);
            Assert.AreEqual("table-rejected", copiedTable.RejectedSuggestionFingerprints[0]);
            Assert.AreEqual("销售模块", copiedTable.UsedByModules[0].Value);
            Assert.AreEqual("订单内码参考名", copiedField.SuggestedChineseName.Value);
            Assert.AreEqual("订单主键", copiedField.AlternativeChineseNames[0].Value);
            Assert.AreEqual("field-rejected", copiedField.RejectedSuggestionFingerprints[0]);
            Assert.AreNotSame(table.SuggestedChineseName, copiedTable.SuggestedChineseName);
            Assert.AreNotSame(table.SuggestedChineseName.Evidence[0], copiedTable.SuggestedChineseName.Evidence[0]);
        }

        /// <summary>XMZADD 20260901 验证没有字段的物理表即使有数据和人工业务分类也必须排除。</summary>
        [TestMethod]
        public void Prune_TableWithoutFields_IsAlwaysExcluded()
        {
            TableMetadata table = CreateTable("T_EMPTY_STRUCTURE", "TABLE", 12L, DictionaryTableCategory.Business);
            table.Fields.Clear();

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(table));

            Assert.AreEqual(0, result.Snapshot.Tables.Count);
            Assert.AreEqual("NoFields", result.ExcludedObjects[0].ReasonCode);
        }

        /// <summary>XMZADD 20260901 验证无数据且没有任何依据的表默认不进入规范快照。</summary>
        [TestMethod]
        public void Prune_EmptyTableWithoutEvidence_IsExcluded()
        {
            TableMetadata table = CreateTable("T_NEW_EMPTY", "TABLE", 0L, DictionaryTableCategory.Business);

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(table));

            Assert.AreEqual(0, result.Snapshot.Tables.Count);
            Assert.AreEqual("EmptyWithoutEvidence", result.ExcludedObjects[0].ReasonCode);
        }

        /// <summary>XMZADD 20260901 验证源码中已有字段引用的新空业务表可以进入规范快照。</summary>
        [TestMethod]
        public void Prune_EmptyBusinessTableWithSourceEvidence_IsRetained()
        {
            TableMetadata table = CreateTable("T_NEW_ORDER", "TABLE", 0L, DictionaryTableCategory.Business);
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence { ObjectName = "t_new_order", FieldName = "FId" }
            };

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(table), evidence);

            Assert.AreEqual(1, result.Snapshot.Tables.Count);
            Assert.AreEqual(0, result.ExcludedObjects.Count);
        }

        /// <summary>XMZADD 20260901 验证限定 schema 的源码证据不能误保留其他 schema 的同名空表。</summary>
        [TestMethod]
        public void Prune_QualifiedSourceEvidence_RetainsOnlyMatchingSchemaTable()
        {
            TableMetadata current = CreateTable("T_SHARED", "TABLE", 0L, DictionaryTableCategory.Business);
            current.SchemaName = "dbo";
            TableMetadata archive = CreateTable("T_SHARED", "TABLE", 0L, DictionaryTableCategory.Business);
            archive.SchemaName = "archive";
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence { ObjectName = "dbo.T_SHARED", FieldName = "FId" }
            };

            SnapshotPruningResult result = new SnapshotPruningService().Prune(
                CreateSnapshot(current, archive), evidence);

            Assert.AreEqual(1, result.Snapshot.Tables.Count);
            Assert.IsNotNull(FindTable(result.Snapshot, "dbo", "T_SHARED"));
            Assert.IsNull(FindTable(result.Snapshot, "archive", "T_SHARED"));
            Assert.AreEqual("EmptyWithoutEvidence", FindExclusion(result, "archive.T_SHARED").ReasonCode);
        }

        /// <summary>XMZADD 20260901 验证跨 schema 同名对象无法安全解析裸源码证据时全部保守排除。</summary>
        [TestMethod]
        public void Prune_AmbiguousBareSourceEvidence_DoesNotRetainAnySchemaTable()
        {
            TableMetadata current = CreateTable("T_SHARED", "TABLE", 0L, DictionaryTableCategory.Business);
            current.SchemaName = "dbo";
            TableMetadata archive = CreateTable("T_SHARED", "TABLE", 0L, DictionaryTableCategory.Business);
            archive.SchemaName = "archive";
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence { ObjectName = "T_SHARED", FieldName = "FId" }
            };

            SnapshotPruningResult result = new SnapshotPruningService().Prune(
                CreateSnapshot(current, archive), evidence);

            Assert.AreEqual(0, result.Snapshot.Tables.Count);
            Assert.AreEqual("EmptyWithoutEvidence", FindExclusion(result, "dbo.T_SHARED").ReasonCode);
            Assert.AreEqual("EmptyWithoutEvidence", FindExclusion(result, "archive.T_SHARED").ReasonCode);
        }

        /// <summary>XMZADD 20260901 验证数据库明确描述可以保留有字段的新空业务表。</summary>
        [TestMethod]
        public void Prune_EmptyBusinessTableWithDatabaseDescription_IsRetained()
        {
            TableMetadata table = CreateTable("T_NEW_MASTER", "TABLE", 0L, DictionaryTableCategory.BaseData);
            table.ChineseName = new MetadataValue
            {
                Value = "新基础资料",
                Status = ConfidenceStatus.DatabaseEvidence
            };

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(table));

            Assert.AreEqual(1, result.Snapshot.Tables.Count);
        }

        /// <summary>XMZADD 20260901 验证人工空表保留标记可以保留有字段的业务对象。</summary>
        [TestMethod]
        public void Prune_EmptyBusinessTableWithKeepFlag_IsRetained()
        {
            TableMetadata table = CreateTable("T_RESERVED", "TABLE", 0L, DictionaryTableCategory.Business);
            table.KeepWhenEmpty = true;

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(table));

            Assert.AreEqual(1, result.Snapshot.Tables.Count);
        }

        /// <summary>XMZADD 20260901 验证只有业务表和基础资料表能进入规范快照，人工技术与排除分类优先。</summary>
        [TestMethod]
        public void Prune_OnlyBusinessAndBaseDataCategoriesAreRetained()
        {
            TableMetadata business = CreateTable("T_ORDER", "TABLE", 1L, DictionaryTableCategory.Business);
            TableMetadata baseData = CreateTable("T_CUSTOMER", "TABLE", 1L, DictionaryTableCategory.BaseData);
            TableMetadata technical = CreateTable("T_LOG", "TABLE", 100L, DictionaryTableCategory.Technical);
            TableMetadata excluded = CreateTable("T_UNUSED", "TABLE", 100L, DictionaryTableCategory.Excluded);
            TableMetadata unclassified = CreateTable("T_UNKNOWN", "TABLE", 100L, DictionaryTableCategory.Unclassified);

            SnapshotPruningResult result = new SnapshotPruningService().Prune(
                CreateSnapshot(business, baseData, technical, excluded, unclassified));

            Assert.AreEqual(2, result.Snapshot.Tables.Count);
            Assert.AreEqual("T_ORDER", result.Snapshot.Tables[0].ObjectName);
            Assert.AreEqual("T_CUSTOMER", result.Snapshot.Tables[1].ObjectName);
            Assert.AreEqual("TechnicalCategory", FindExclusion(result, "dbo.T_LOG").ReasonCode);
            Assert.AreEqual("ExcludedCategory", FindExclusion(result, "dbo.T_UNUSED").ReasonCode);
            Assert.AreEqual("UnclassifiedCategory", FindExclusion(result, "dbo.T_UNKNOWN").ReasonCode);
        }

        /// <summary>XMZADD 20260901 验证技术表即使具备说明和人工空表保留标记也不能绕过分类白名单。</summary>
        [TestMethod]
        public void Prune_TechnicalCategoryOverridesEmptyTableEvidence()
        {
            TableMetadata technical = CreateTable("T_TECH_CACHE", "TABLE", 0L, DictionaryTableCategory.Technical);
            technical.KeepWhenEmpty = true;
            technical.ChineseName = new MetadataValue
            {
                Value = "技术缓存",
                Status = ConfidenceStatus.DatabaseEvidence
            };

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(technical));

            Assert.AreEqual(0, result.Snapshot.Tables.Count);
            Assert.AreEqual("TechnicalCategory", result.ExcludedObjects[0].ReasonCode);
        }

        /// <summary>XMZADD 20260901 验证人工技术分类在空表默认规则之前生效并保留明确分类原因。</summary>
        [TestMethod]
        public void Prune_EmptyTechnicalTableWithoutEvidence_UsesTechnicalCategoryReason()
        {
            TableMetadata technical = CreateTable("T_EMPTY_TECH", "TABLE", 0L, DictionaryTableCategory.Technical);

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(technical));

            Assert.AreEqual(0, result.Snapshot.Tables.Count);
            Assert.AreEqual("TechnicalCategory", result.ExcludedObjects[0].ReasonCode);
        }

        /// <summary>XMZADD 20260901 验证人工排除分类在空表默认规则之前生效并保留明确分类原因。</summary>
        [TestMethod]
        public void Prune_EmptyExcludedTableWithoutEvidence_UsesExcludedCategoryReason()
        {
            TableMetadata excluded = CreateTable("T_EMPTY_UNUSED", "TABLE", 0L, DictionaryTableCategory.Excluded);

            SnapshotPruningResult result = new SnapshotPruningService().Prune(CreateSnapshot(excluded));

            Assert.AreEqual(0, result.Snapshot.Tables.Count);
            Assert.AreEqual("ExcludedCategory", result.ExcludedObjects[0].ReasonCode);
        }

        /// <summary>XMZADD 20260901 验证输出只保留两端都存在的关系和仍有表作用域的缩写，且不修改输入集合。</summary>
        [TestMethod]
        public void Prune_RemovesDanglingRelationsAndScopedAbbreviationsWithoutMutatingInput()
        {
            TableMetadata order = CreateTable("T_ORDER", "TABLE", 1L, DictionaryTableCategory.Business);
            TableMetadata customer = CreateTable("T_CUSTOMER", "TABLE", 1L, DictionaryTableCategory.BaseData);
            TableMetadata log = CreateTable("T_LOG", "TABLE", 1L, DictionaryTableCategory.Technical);
            order.Relations.Add(CreateRelation("T_CUSTOMER", "T_ORDER"));
            order.Relations.Add(CreateRelation("T_LOG", "T_ORDER"));
            SnapshotData snapshot = CreateSnapshot(order, customer, log);
            snapshot.Abbreviations.Add(CreateAbbreviation("GLOBAL", null));
            snapshot.Abbreviations.Add(CreateAbbreviation("ORD", "T_ORDER"));
            snapshot.Abbreviations.Add(CreateAbbreviation("LOG", "T_LOG"));

            SnapshotPruningResult result = new SnapshotPruningService().Prune(snapshot);

            Assert.AreEqual(2, result.Snapshot.Tables.Count);
            Assert.AreEqual(1, result.Snapshot.Tables[0].Relations.Count);
            Assert.AreEqual("T_CUSTOMER", result.Snapshot.Tables[0].Relations[0].ParentTableName);
            Assert.AreEqual("dbo", result.Snapshot.Tables[0].Relations[0].ParentSchemaName);
            Assert.AreEqual("dbo", result.Snapshot.Tables[0].Relations[0].ChildSchemaName);
            Assert.AreEqual(2, result.Snapshot.Abbreviations.Count);
            Assert.AreEqual(3, snapshot.Tables.Count);
            Assert.AreEqual(2, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(3, snapshot.Abbreviations.Count);
            Assert.AreNotSame(snapshot.Tables[0], result.Snapshot.Tables[0]);
            Assert.AreNotSame(snapshot.Tables[0].Fields[0], result.Snapshot.Tables[0].Fields[0]);
        }

        /// <summary>XMZADD 20260901 验证跨 schema 同名对象只能由完整稳定键保留关系和表作用域缩写。</summary>
        [TestMethod]
        public void Prune_DuplicateTableNamesAcrossSchemas_UsesQualifiedStableReferences()
        {
            TableMetadata keptShared = CreateTable("T_SHARED", "TABLE", 1L, DictionaryTableCategory.BaseData);
            keptShared.SchemaName = "dbo";
            TableMetadata excludedShared = CreateTable("T_SHARED", "TABLE", 1L, DictionaryTableCategory.Technical);
            excludedShared.SchemaName = "archive";
            TableMetadata child = CreateTable("T_CHILD", "TABLE", 1L, DictionaryTableCategory.Business);
            child.SchemaName = "dbo";
            child.Relations.Add(CreateQualifiedRelation("FK_KEEP", "dbo", "T_SHARED", "dbo", "T_CHILD"));
            child.Relations.Add(CreateQualifiedRelation("FK_DROP", "archive", "T_SHARED", "dbo", "T_CHILD"));
            child.Relations.Add(CreateQualifiedRelation("FK_AMBIGUOUS", null, "T_SHARED", "dbo", "T_CHILD"));
            SnapshotData snapshot = CreateSnapshot(keptShared, excludedShared, child);
            snapshot.Abbreviations.Add(CreateAbbreviation("BARE_SHARED", "T_SHARED"));
            snapshot.Abbreviations.Add(CreateAbbreviation("KEPT_SHARED", "dbo.T_SHARED"));
            snapshot.Abbreviations.Add(CreateAbbreviation("REMOVED_SHARED", "archive.T_SHARED"));
            snapshot.Abbreviations.Add(CreateAbbreviation("UNIQUE_CHILD", "T_CHILD"));

            SnapshotPruningResult result = new SnapshotPruningService().Prune(snapshot);
            TableMetadata retainedChild = FindTable(result.Snapshot, "dbo", "T_CHILD");

            Assert.IsNotNull(retainedChild);
            Assert.AreEqual(1, retainedChild.Relations.Count);
            Assert.AreEqual("FK_KEEP", retainedChild.Relations[0].ForeignKeyName);
            Assert.AreEqual("dbo", retainedChild.Relations[0].ParentSchemaName);
            Assert.AreEqual("dbo", retainedChild.Relations[0].ChildSchemaName);
            Assert.AreEqual(2, result.Snapshot.Abbreviations.Count);
            Assert.IsNotNull(FindAbbreviation(result.Snapshot, "KEPT_SHARED"));
            Assert.IsNotNull(FindAbbreviation(result.Snapshot, "UNIQUE_CHILD"));
            Assert.IsNull(FindAbbreviation(result.Snapshot, "BARE_SHARED"));
            Assert.IsNull(FindAbbreviation(result.Snapshot, "REMOVED_SHARED"));
            Assert.AreEqual(3, child.Relations.Count);
            Assert.AreEqual(4, snapshot.Abbreviations.Count);
        }

        /// <summary>XMZADD 20260901 验证旧排除记录确定性去重，当前判断覆盖旧原因且已恢复对象不再排除。</summary>
        [TestMethod]
        public void Prune_MergesOldExclusionsDeterministicallyAndRemovesRestoredObject()
        {
            TableMetadata archive = CreateTable("T_ARCHIVE", "VIEW", 1L, DictionaryTableCategory.Business);
            TableMetadata restored = CreateTable("T_RESTORED", "TABLE", 1L, DictionaryTableCategory.Business);
            SnapshotData snapshot = CreateSnapshot(archive, restored);
            snapshot.ExcludedObjects.Add(CreateExclusion("dbo.T_HISTORY", null, "旧历史"));
            snapshot.ExcludedObjects.Add(CreateExclusion("dbo.T_ARCHIVE", "Old", "旧原因"));
            snapshot.ExcludedObjects.Add(CreateExclusion("DBO.t_archive", "OlderDuplicate", "重复旧原因"));
            snapshot.ExcludedObjects.Add(CreateExclusion("dbo.T_RESTORED", "Old", "旧排除"));

            SnapshotPruningResult result = new SnapshotPruningService().Prune(snapshot);

            Assert.AreEqual(2, result.ExcludedObjects.Count);
            Assert.AreEqual("dbo.T_ARCHIVE", result.ExcludedObjects[0].ObjectKey);
            Assert.AreEqual("ViewExcluded", result.ExcludedObjects[0].ReasonCode);
            Assert.AreEqual("dbo.T_HISTORY", result.ExcludedObjects[1].ObjectKey);
            Assert.AreEqual("LegacyExcluded", result.ExcludedObjects[1].ReasonCode);
            Assert.IsNull(FindExclusion(result, "dbo.T_RESTORED"));
            Assert.AreEqual(4, snapshot.ExcludedObjects.Count);
        }

        /// <summary>XMZADD 20260901 验证同对象旧排除冲突按稳定内容选择，输入顺序不能改变规范结果。</summary>
        [TestMethod]
        public void Prune_ConflictingLegacyExclusions_SelectSameWinnerInEitherInputOrder()
        {
            var older = new ExcludedObjectRecord
            {
                ObjectKey = "dbo.T_HISTORY",
                ReasonCode = "Zeta",
                Reason = "旧记录",
                Category = DictionaryTableCategory.Technical,
                ApproximateRowCount = 30L,
                ExcludedAtUtc = new DateTime(2026, 8, 31, 7, 0, 0, DateTimeKind.Utc)
            };
            var newerAlpha = new ExcludedObjectRecord
            {
                ObjectKey = "DBO.t_history",
                ReasonCode = "Alpha",
                Reason = "同时间较小原因码",
                Category = DictionaryTableCategory.Excluded,
                ApproximateRowCount = 20L,
                ExcludedAtUtc = new DateTime(2026, 8, 31, 8, 0, 0, DateTimeKind.Utc)
            };
            var newerBeta = new ExcludedObjectRecord
            {
                ObjectKey = "dbo.T_HISTORY",
                ReasonCode = "Beta",
                Reason = "同时间较大原因码",
                Category = DictionaryTableCategory.Technical,
                ApproximateRowCount = 10L,
                ExcludedAtUtc = new DateTime(2026, 8, 31, 8, 0, 0, DateTimeKind.Utc)
            };
            SnapshotData forward = CreateSnapshot();
            forward.ExcludedObjects.Add(older);
            forward.ExcludedObjects.Add(newerAlpha);
            forward.ExcludedObjects.Add(newerBeta);
            SnapshotData reverse = CreateSnapshot();
            reverse.ExcludedObjects.Add(newerBeta);
            reverse.ExcludedObjects.Add(newerAlpha);
            reverse.ExcludedObjects.Add(older);

            ExcludedObjectRecord first = new SnapshotPruningService().Prune(forward).ExcludedObjects[0];
            ExcludedObjectRecord second = new SnapshotPruningService().Prune(reverse).ExcludedObjects[0];

            Assert.AreEqual("Beta", first.ReasonCode);
            AssertExclusionEquals(first, second);
        }

        /// <summary>XMZADD 20260901 验证排除原因和判断时间取自同一输入快照，重复清理结果保持稳定。</summary>
        [TestMethod]
        public void Prune_UsesStableReasonAndSnapshotRefreshTime()
        {
            SnapshotData snapshot = CreateSnapshot(
                CreateTable("T_EMPTY", "TABLE", 0L, DictionaryTableCategory.Business));

            SnapshotPruningResult first = new SnapshotPruningService().Prune(snapshot);
            SnapshotPruningResult second = new SnapshotPruningService().Prune(snapshot);

            Assert.AreEqual(first.ExcludedObjects[0].ReasonCode, second.ExcludedObjects[0].ReasonCode);
            Assert.AreEqual(snapshot.RefreshedAt, first.ExcludedObjects[0].ExcludedAtUtc);
            Assert.AreEqual(first.ExcludedObjects[0].ExcludedAtUtc, second.ExcludedObjects[0].ExcludedAtUtc);
            Assert.IsFalse(string.IsNullOrWhiteSpace(first.ExcludedObjects[0].Reason));
        }

        /// <summary>XMZADD 20260901 验证源码证据只遍历一次建立索引，避免每张表反复扫描全部证据。</summary>
        [TestMethod]
        public void Prune_BuildsSourceEvidenceIndexOnce()
        {
            var tables = new CountingList<TableMetadata>();
            var evidence = new CountingList<SourceEvidence>();
            for (int index = 0; index < 500; index++)
            {
                tables.Add(CreateTable("T_ITEM_" + index, "TABLE", 0L, DictionaryTableCategory.Business));
                evidence.Add(new SourceEvidence { ObjectName = "T_ITEM_" + index, FieldName = "FId" });
            }
            var snapshot = new SnapshotData
            {
                FormatVersion = 1,
                Revision = 8L,
                RefreshedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
                Tables = tables
            };

            SnapshotPruningResult result = new SnapshotPruningService().Prune(snapshot, evidence);

            Assert.AreEqual(500, result.Snapshot.Tables.Count);
            Assert.IsTrue(evidence.IndexerReadCount <= evidence.Count + 1,
                "源码证据应一次建索引，不能按表重复扫描。");
            Assert.IsTrue(tables.IndexerReadCount <= tables.Count * 3,
                "表集合应由少量线性阶段处理，不能嵌套全量扫描。");
        }

        private static SnapshotData CreateSnapshot(params TableMetadata[] tables)
        {
            var snapshot = new SnapshotData
            {
                FormatVersion = 1,
                Revision = 8L,
                RefreshedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc)
            };
            for (int index = 0; index < tables.Length; index++)
            {
                snapshot.Tables.Add(tables[index]);
            }
            return snapshot;
        }

        private static TableMetadata CreateTable(string name, string objectType, long rows,
            DictionaryTableCategory category)
        {
            var table = new TableMetadata
            {
                ScopeKey = "scope",
                SchemaName = "dbo",
                ObjectName = name,
                ObjectType = objectType,
                Category = category,
                ApproximateRowCount = rows,
                Fields = new List<FieldMetadata>(),
                Relations = new List<RelationMetadata>()
            };
            table.Fields.Add(new FieldMetadata
            {
                OwnerTableName = name,
                FieldName = "FId",
                DataType = "int",
                EnumItems = new List<EnumItemMetadata>()
            });
            return table;
        }

        /// <summary>XMZADD 20260910 创建带完整审阅证据的参考名称以验证裁剪复制边界。</summary>
        private static MetadataValue CreateNameValue(string value, string sourcePath)
        {
            return new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.CodeEvidence,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "EOS源码",
                        SourcePath = sourcePath,
                        RawValue = "原始值",
                        OriginalText = "原始源码正文"
                    }
                }
            };
        }

        private static RelationMetadata CreateRelation(string parentTable, string childTable)
        {
            return new RelationMetadata
            {
                ScopeKey = "scope",
                ForeignKeyName = "FK_" + childTable + "_" + parentTable,
                ParentTableName = parentTable,
                ParentFieldName = "FId",
                ChildTableName = childTable,
                ChildFieldName = "FId"
            };
        }

        private static RelationMetadata CreateQualifiedRelation(string foreignKeyName,
            string parentSchema, string parentTable, string childSchema, string childTable)
        {
            return new RelationMetadata
            {
                ScopeKey = "scope",
                ForeignKeyName = foreignKeyName,
                ParentSchemaName = parentSchema,
                ParentTableName = parentTable,
                ParentFieldName = "FId",
                ChildSchemaName = childSchema,
                ChildTableName = childTable,
                ChildFieldName = "FId"
            };
        }

        private static AbbreviationEntry CreateAbbreviation(string abbreviation, string tableScope)
        {
            return new AbbreviationEntry
            {
                Abbreviation = abbreviation,
                ChineseMeaning = abbreviation,
                TableScope = tableScope,
                Evidence = new List<AbbreviationEvidence>()
            };
        }

        private static ExcludedObjectRecord CreateExclusion(string objectKey, string reasonCode, string reason)
        {
            return new ExcludedObjectRecord
            {
                ObjectKey = objectKey,
                ReasonCode = reasonCode,
                Reason = reason,
                ExcludedAtUtc = new DateTime(2026, 8, 31, 8, 0, 0, DateTimeKind.Utc)
            };
        }

        private static ExcludedObjectRecord FindExclusion(SnapshotPruningResult result, string objectKey)
        {
            for (int index = 0; index < result.ExcludedObjects.Count; index++)
            {
                ExcludedObjectRecord item = result.ExcludedObjects[index];
                if (string.Equals(item.ObjectKey, objectKey, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
            return null;
        }

        private static TableMetadata FindTable(SnapshotData snapshot, string schemaName, string objectName)
        {
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                if (string.Equals(table.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(table.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return table;
                }
            }
            return null;
        }

        private static AbbreviationEntry FindAbbreviation(SnapshotData snapshot, string abbreviation)
        {
            for (int index = 0; index < snapshot.Abbreviations.Count; index++)
            {
                AbbreviationEntry item = snapshot.Abbreviations[index];
                if (string.Equals(item.Abbreviation, abbreviation, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
            return null;
        }

        private static void AssertExclusionEquals(ExcludedObjectRecord expected, ExcludedObjectRecord actual)
        {
            Assert.AreEqual(expected.ObjectKey, actual.ObjectKey);
            Assert.AreEqual(expected.ReasonCode, actual.ReasonCode);
            Assert.AreEqual(expected.Reason, actual.Reason);
            Assert.AreEqual(expected.Category, actual.Category);
            Assert.AreEqual(expected.ApproximateRowCount, actual.ApproximateRowCount);
            Assert.AreEqual(expected.ExcludedAtUtc, actual.ExcludedAtUtc);
        }

        private sealed class CountingList<T> : IList<T>
        {
            private readonly List<T> _items = new List<T>();

            public int IndexerReadCount { get; private set; }

            public T this[int index]
            {
                get
                {
                    IndexerReadCount++;
                    return _items[index];
                }
                set { _items[index] = value; }
            }

            public int Count { get { return _items.Count; } }
            public bool IsReadOnly { get { return false; } }
            public void Add(T item) { _items.Add(item); }
            public void Clear() { _items.Clear(); }
            public bool Contains(T item) { return _items.Contains(item); }
            public void CopyTo(T[] array, int arrayIndex) { _items.CopyTo(array, arrayIndex); }
            public IEnumerator<T> GetEnumerator() { return _items.GetEnumerator(); }
            public int IndexOf(T item) { return _items.IndexOf(item); }
            public void Insert(int index, T item) { _items.Insert(index, item); }
            public bool Remove(T item) { return _items.Remove(item); }
            public void RemoveAt(int index) { _items.RemoveAt(index); }
            IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        }
    }
}
