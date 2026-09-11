using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证共享事件按服务端修订幂等应用、后写覆盖和结构增量原子更新。</summary>
    [TestClass]
    public sealed class DictionaryEventApplyServiceTests
    {
        /// <summary>XMZADD 20260901 验证后修订覆盖同属性且历史保留服务端实际前值和可信作者。</summary>
        [TestMethod]
        public void Apply_LaterRevisionWinsSamePropertyAndKeepsActualHistory()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();

            service.Apply(snapshot, CreateSet("op_1", "dbo.T_ORDER", "FNAME", "ChineseName", "伪造旧值", "名称甲"), 1L, "111", EmptyPublishers());
            service.Apply(snapshot, CreateSet("op_2", "DBO.t_order", "fname", "ChineseName", "名称甲", "名称乙"), 2L, "222", EmptyPublishers());

            Assert.AreEqual("名称乙", snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Confirmed, snapshot.Tables[0].Fields[1].ChineseName.Status);
            Assert.IsTrue(snapshot.Tables[0].Fields[1].ChineseName.IsManualOverride);
            Assert.IsTrue(snapshot.Tables[0].Fields[1].ChineseName.IsLocked);
            Assert.AreEqual(2L, snapshot.Revision);
            Assert.AreEqual(2, service.AppliedHistory.Count);
            Assert.AreEqual("旧名称", service.AppliedHistory[0].OldValue);
            Assert.AreEqual("名称乙", service.AppliedHistory[1].NewValue);
            Assert.AreEqual("222", service.AppliedHistory[1].AuthorGitHubUserId);
        }

        /// <summary>XMZADD 20260910 验证事务克隆不会合并或冻结正式名称之外的参考名称层。</summary>
        [TestMethod]
        public void Apply_NewNameLayersRemainIndependentAndMutableAfterTransactionalClone()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            TableMetadata sourceTable = snapshot.Tables[0];
            FieldMetadata sourceField = sourceTable.Fields[1];
            sourceTable.SuggestedChineseName = ManualValue("订单参考名");
            sourceTable.AlternativeChineseNames.Add(ManualValue("订单单据"));
            sourceTable.RejectedSuggestionFingerprints.Add("table-rejected");
            sourceTable.UsedByModules.Add(ManualValue("销售模块"));
            sourceField.SuggestedChineseName = ManualValue("名称字段参考名");
            sourceField.AlternativeChineseNames.Add(ManualValue("订单名称"));
            sourceField.RejectedSuggestionFingerprints.Add("field-rejected");
            sourceTable.AlternativeChineseNames[0].Evidence.Add(new EvidenceItem { RuleName = "TableNameEvidence" });
            sourceField.AlternativeChineseNames[0].Evidence.Add(new EvidenceItem { RuleName = "FieldNameEvidence" });
            var service = new DictionaryEventApplyService();

            service.Apply(snapshot,
                CreateSet("keep_name_layers", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "正式名称"),
                1L, "111", EmptyPublishers());

            TableMetadata appliedTable = snapshot.Tables[0];
            FieldMetadata appliedField = appliedTable.Fields[1];
            Assert.AreNotSame(sourceTable, appliedTable);
            Assert.AreNotSame(sourceField, appliedField);
            Assert.AreNotSame(sourceTable.AlternativeChineseNames, appliedTable.AlternativeChineseNames);
            Assert.AreNotSame(sourceField.AlternativeChineseNames, appliedField.AlternativeChineseNames);
            Assert.AreNotSame(sourceTable.AlternativeChineseNames[0], appliedTable.AlternativeChineseNames[0]);
            Assert.AreNotSame(sourceField.AlternativeChineseNames[0], appliedField.AlternativeChineseNames[0]);
            Assert.AreNotSame(sourceTable.AlternativeChineseNames[0].Evidence,
                appliedTable.AlternativeChineseNames[0].Evidence);
            Assert.AreNotSame(sourceField.AlternativeChineseNames[0].Evidence,
                appliedField.AlternativeChineseNames[0].Evidence);
            Assert.AreNotSame(sourceTable.AlternativeChineseNames[0].Evidence[0],
                appliedTable.AlternativeChineseNames[0].Evidence[0]);
            Assert.AreNotSame(sourceField.AlternativeChineseNames[0].Evidence[0],
                appliedField.AlternativeChineseNames[0].Evidence[0]);
            Assert.AreEqual("订单", appliedTable.ChineseName.Value);
            Assert.AreEqual("自动名称", appliedTable.ChineseName.OriginalAutomaticValue);
            Assert.AreEqual("订单参考名", appliedTable.SuggestedChineseName.Value);
            Assert.AreEqual("订单单据", appliedTable.AlternativeChineseNames[0].Value);
            Assert.AreEqual("table-rejected", appliedTable.RejectedSuggestionFingerprints[0]);
            Assert.AreEqual("销售模块", appliedTable.UsedByModules[0].Value);
            Assert.AreEqual("名称字段参考名", appliedField.SuggestedChineseName.Value);
            Assert.AreEqual("订单名称", appliedField.AlternativeChineseNames[0].Value);
            Assert.AreEqual("field-rejected", appliedField.RejectedSuggestionFingerprints[0]);
            appliedTable.AlternativeChineseNames.Add(ManualValue("订单档案"));
            appliedTable.RejectedSuggestionFingerprints.Add("table-rejected-2");
            appliedTable.UsedByModules.Add(ManualValue("仓储模块"));
            appliedField.AlternativeChineseNames.Add(ManualValue("名称属性"));
            appliedField.RejectedSuggestionFingerprints.Add("field-rejected-2");
            Assert.AreEqual(2, appliedTable.AlternativeChineseNames.Count);
            Assert.AreEqual(2, appliedTable.RejectedSuggestionFingerprints.Count);
            Assert.AreEqual(2, appliedTable.UsedByModules.Count);
            Assert.AreEqual(2, appliedField.AlternativeChineseNames.Count);
            Assert.AreEqual(2, appliedField.RejectedSuggestionFingerprints.Count);
        }

        /// <summary>XMZADD 20260901 验证结构发布证据值在增量重放中保持自动可信状态和相对证据，而非误锁为人工确认。</summary>
        [TestMethod]
        public void Apply_PublisherEvidenceSet_PreservesAutomaticEvidenceState()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            DictionaryChangeOperation operation = CreateSet(
                "evidence_apply", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "订单名称");
            operation.Evidence = new List<EvidenceItem>
            {
                new EvidenceItem
                {
                    SourceType = "EOS源码",
                    SourcePath = "Order/OrderEntity.vb",
                    SourceLine = 12,
                    RuleName = "EntityProperty",
                    Explanation = "实体属性中文摘要"
                }
            };

            service.Apply(snapshot, operation, 1L, "999", new List<string> { "999" });

            MetadataValue value = snapshot.Tables[0].Fields[1].ChineseName;
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, value.Status);
            Assert.IsFalse(value.IsManualOverride);
            Assert.IsFalse(value.IsLocked);
            Assert.AreEqual("Order/OrderEntity.vb", value.Evidence[0].SourcePath);
            Assert.AreEqual("Order/OrderEntity.vb", service.AppliedHistory[0].Evidence[0].SourcePath);
        }

        /// <summary>XMZADD 20260901 验证重复操作号优先忽略，即使重试携带不同修订号也不推进快照。</summary>
        [TestMethod]
        public void Apply_DuplicateOperationId_IsIgnoredBeforeRevisionCheck()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            DictionaryChangeOperation operation = CreateSet("same_1", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "新名称");

            DictionaryEventApplyResult first = service.Apply(snapshot, operation, 5L, "111", EmptyPublishers());
            operation.NewValue = "重试篡改值";
            DictionaryEventApplyResult duplicate = service.Apply(snapshot, operation, 3L, "111", EmptyPublishers());

            Assert.AreEqual(DictionaryEventApplyStatus.Applied, first.Status);
            Assert.AreEqual(DictionaryEventApplyStatus.DuplicateIgnored, duplicate.Status);
            Assert.AreEqual("新名称", snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual(5L, snapshot.Revision);
            Assert.AreEqual(1, service.AppliedHistory.Count);
        }

        /// <summary>XMZADD 20260901 验证相同或更旧修订的新事件被拒绝且不改变快照与历史。</summary>
        [TestMethod]
        public void Apply_StaleNewOperation_RejectsWithoutMutation()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            snapshot.Revision = 4L;
            var service = new DictionaryEventApplyService();

            Assert.ThrowsException<InvalidOperationException>(() => service.Apply(
                snapshot, CreateSet("stale_1", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "新名称"),
                4L, "111", EmptyPublishers()));

            Assert.AreEqual("旧名称", snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual(4L, snapshot.Revision);
            Assert.AreEqual(0, service.AppliedHistory.Count);
        }

        /// <summary>XMZADD 20260901 验证分类和空表规则按强类型应用，非法文本不能部分改变对象。</summary>
        [TestMethod]
        public void Apply_CategoryAndKeepWhenEmpty_UseStrongTypes()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();

            service.Apply(snapshot, CreateSet("category_1", "dbo.T_ORDER", string.Empty, "Category", "Business", "BaseData"), 1L, "111", EmptyPublishers());
            service.Apply(snapshot, CreateSet("keep_1", "dbo.T_ORDER", string.Empty, "KeepWhenEmpty", "False", "True"), 2L, "111", EmptyPublishers());

            Assert.AreEqual(DictionaryTableCategory.BaseData, snapshot.Tables[0].Category);
            Assert.IsTrue(snapshot.Tables[0].KeepWhenEmpty);
        }

        /// <summary>XMZADD 20260901 验证 publisher 物理标量只经显式类型分支写入，普通用户无法利用正文身份绕过。</summary>
        [TestMethod]
        public void Apply_PublisherPhysicalSet_IsTypedAndOrdinaryUserIsRejected()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            IList<string> publishers = new List<string> { "999" };

            service.Apply(snapshot, CreateSet("length_1", "dbo.T_ORDER", "FNAME", "LengthText", "20", "200"), 1L, "999", publishers);
            DictionaryChangeOperation forged = CreateSet("required_1", "dbo.T_ORDER", "FNAME", "IsRequired", "False", "True");
            forged.AuthorGitHubUserId = "999";
            Assert.ThrowsException<InvalidOperationException>(() => service.Apply(snapshot, forged, 2L, "111", publishers));

            Assert.AreEqual("200", snapshot.Tables[0].Fields[1].LengthText);
            Assert.IsFalse(snapshot.Tables[0].Fields[1].IsRequired);
            Assert.AreEqual(1L, snapshot.Revision);
        }

        /// <summary>XMZADD 20260901 验证单项物理修改必须按字段最终类型长度组合原子校验。</summary>
        [TestMethod]
        public void Apply_DataTypeOrLengthSet_ValidatesFinalCombinationAtomically()
        {
            SnapshotData validSnapshot = CreateSnapshot("旧名称");
            var validService = new DictionaryEventApplyService();
            IList<string> publishers = new List<string> { "999" };

            validService.Apply(validSnapshot,
                CreateSet("nvarchar_10", "dbo.T_ORDER", "FNAME", "LengthText", "20", "10"),
                1L, "999", publishers);

            Assert.AreEqual("nvarchar", validSnapshot.Tables[0].Fields[1].DataType);
            Assert.AreEqual("10", validSnapshot.Tables[0].Fields[1].LengthText);
            Assert.AreEqual(1L, validSnapshot.Revision);

            SnapshotData invalidSnapshot = CreateSnapshot("旧名称");
            var invalidService = new DictionaryEventApplyService();
            Assert.ThrowsException<InvalidOperationException>(() => invalidService.Apply(invalidSnapshot,
                CreateSet("int_with_length", "dbo.T_ORDER", "FNAME", "DataType", "nvarchar", "int"),
                1L, "999", publishers));

            Assert.AreEqual("nvarchar", invalidSnapshot.Tables[0].Fields[1].DataType);
            Assert.AreEqual("20", invalidSnapshot.Tables[0].Fields[1].LengthText);
            Assert.AreEqual(0L, invalidSnapshot.Revision);
            Assert.AreEqual(0, invalidService.AppliedHistory.Count);
            Assert.AreEqual(0, invalidService.AppliedOperationIds.Count);
        }

        /// <summary>XMZADD 20260901 验证表字段关系可由 publisher 类型化增量增加并挂接到两端。</summary>
        [TestMethod]
        public void Apply_TypedAddOperations_CreateTableFieldAndRelation()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            IList<string> publishers = new List<string> { "999" };

            service.Apply(snapshot, CreateAddTable("table_add", "dbo.T_CHILD"), 1L, "999", publishers);
            service.Apply(snapshot, CreateAddField("field_add", "dbo.T_CHILD", "FID"), 2L, "999", publishers);
            service.Apply(snapshot, CreateAddField("field_add2", "dbo.T_CHILD", "FORDERID"), 3L, "999", publishers);
            service.Apply(snapshot, CreateAddRelation("relation_add"), 4L, "999", publishers);

            TableMetadata child = snapshot.Tables[1];
            Assert.AreEqual("T_CHILD", child.ObjectName);
            Assert.AreEqual(2, child.Fields.Count);
            Assert.AreEqual(1, child.Relations.Count);
            Assert.AreEqual(1, snapshot.Tables[0].Relations.Count);
            Assert.AreSame(snapshot.Tables[0].Relations[0], child.Relations[0]);
            Assert.AreEqual("FK_CHILD_ORDER", service.AppliedHistory[3].NewRelationPayload.ForeignKeyName);
        }

        /// <summary>XMZADD 20260901 验证删除字段和表会同步清理所有引用关系且保留实际旧结构历史。</summary>
        [TestMethod]
        public void Apply_RemoveFieldAndTable_CleansRelationsAndKeepsOldPayload()
        {
            SnapshotData snapshot = CreateSnapshotWithChildAndRelation();
            var service = new DictionaryEventApplyService();
            IList<string> publishers = new List<string> { "999" };

            service.Apply(snapshot, CreateRemoveField("field_remove", "dbo.T_CHILD", "FORDERID"), 1L, "999", publishers);
            Assert.AreEqual(0, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(0, snapshot.Tables[1].Relations.Count);
            Assert.AreEqual("FORDERID", service.AppliedHistory[0].OldFieldPayload.FieldName);

            service.Apply(snapshot, CreateRemoveTable("table_remove", "dbo.T_CHILD"), 2L, "999", publishers);
            Assert.AreEqual(1, snapshot.Tables.Count);
            Assert.AreEqual("T_CHILD", service.AppliedHistory[1].OldTablePayload.ObjectName);
        }

        /// <summary>XMZADD 20260901 验证关系稳定键防止重复并可从父子两端同时移除。</summary>
        [TestMethod]
        public void Apply_RelationStableIdentity_PreventsDuplicateAndRemovesBothSides()
        {
            SnapshotData snapshot = CreateSnapshotWithChildAndRelation();
            var service = new DictionaryEventApplyService();
            IList<string> publishers = new List<string> { "999" };

            Assert.ThrowsException<InvalidOperationException>(() => service.Apply(
                snapshot, CreateAddRelation("relation_duplicate"), 1L, "999", publishers));
            Assert.AreEqual(1, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(0L, snapshot.Revision);

            service.Apply(snapshot, CreateRemoveRelation("relation_remove"), 1L, "999", publishers);
            Assert.AreEqual(0, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(0, snapshot.Tables[1].Relations.Count);
            Assert.AreEqual("FK_CHILD_ORDER", service.AppliedHistory[0].OldRelationPayload.ForeignKeyName);
        }

        /// <summary>XMZADD 20260901 验证单个结构事件在目标集合损坏时也不会只写入关系一端。</summary>
        [TestMethod]
        public void Apply_AddRelationWithInvalidChildCollection_DoesNotPartiallyAttachParent()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var child = new TableMetadata { SchemaName = "dbo", ObjectName = "T_CHILD", ObjectType = "TABLE", Category = DictionaryTableCategory.Business };
            child.Fields.Add(new FieldMetadata { FieldName = "FID", OwnerTableName = "T_CHILD", DataType = "int" });
            child.Fields.Add(new FieldMetadata { FieldName = "FORDERID", OwnerTableName = "T_CHILD", DataType = "int" });
            child.Relations = null;
            snapshot.Tables.Add(child);
            var service = new DictionaryEventApplyService();

            Assert.ThrowsException<InvalidOperationException>(() => service.Apply(
                snapshot, CreateAddRelation("relation_invalid_collection"), 1L, "999", new List<string> { "999" }));

            Assert.AreEqual(0, snapshot.Tables[0].Relations.Count);
            Assert.AreEqual(0L, snapshot.Revision);
            Assert.AreEqual(0, service.AppliedHistory.Count);
        }

        /// <summary>XMZADD 20260901 验证目标不存在或批次后续失败时不会留下半应用结果。</summary>
        [TestMethod]
        public void ApplyBatch_LaterTargetFailure_RollsBackSnapshotHistoryAndIds()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            var batch = new DictionaryChangeBatch { BatchId = "batch_apply", CreatedAtUtc = DateTime.UtcNow };
            batch.Operations.Add(CreateSet("batch_1", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "暂存名称"));
            batch.Operations.Add(CreateSet("batch_2", "dbo.T_MISSING", "FNAME", "ChineseName", "旧名称", "错误名称"));

            Assert.ThrowsException<InvalidOperationException>(() => service.ApplyBatch(
                snapshot, batch, 1L, "111", EmptyPublishers()));

            Assert.AreEqual("旧名称", snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual(0L, snapshot.Revision);
            Assert.AreEqual(0, service.AppliedHistory.Count);
            DictionaryEventApplyResult retry = service.Apply(snapshot, batch.Operations[0], 1L, "111", EmptyPublishers());
            Assert.AreEqual(DictionaryEventApplyStatus.Applied, retry.Status);
        }

        /// <summary>XMZADD 20260901 验证同一仓库修订的结构事件和普通事件按各自真实作者原子应用且只推进一次修订。</summary>
        [TestMethod]
        public void ApplyTrustedRepositoryBatch_MultipleAuthorsPreservesHistoryAndOneRevision()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            var batch = new DictionaryChangeBatch { BatchId = "repository_revision_1" };
            DictionaryChangeOperation addTable = CreateAddTable("trusted_table", "dbo.T_TRUSTED");
            addTable.AuthorGitHubUserId = "101";
            DictionaryChangeOperation setName = CreateSet(
                "trusted_name", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "共享名称");
            setName.AuthorGitHubUserId = "202";
            batch.Operations.Add(addTable);
            batch.Operations.Add(setName);

            IList<DictionaryEventApplyResult> results = service.ApplyTrustedRepositoryBatch(snapshot, batch, 1L);

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(2, snapshot.Tables.Count);
            Assert.AreEqual("共享名称", snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual(1L, snapshot.Revision);
            Assert.AreEqual("101", service.AppliedHistory[0].AuthorGitHubUserId);
            Assert.AreEqual("202", service.AppliedHistory[1].AuthorGitHubUserId);
        }

        /// <summary>XMZADD 20260901 验证可信仓库批次含非法作者时整批回滚快照、历史和幂等操作号。</summary>
        [TestMethod]
        public void ApplyTrustedRepositoryBatch_InvalidAuthorRollsBackWholeRevision()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            var batch = new DictionaryChangeBatch { BatchId = "repository_revision_invalid" };
            DictionaryChangeOperation valid = CreateSet(
                "trusted_valid", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "不应保存");
            valid.AuthorGitHubUserId = "303";
            DictionaryChangeOperation invalid = CreateAddTable("trusted_invalid", "dbo.T_INVALID");
            invalid.AuthorGitHubUserId = "invalid-author";
            batch.Operations.Add(valid);
            batch.Operations.Add(invalid);

            Assert.ThrowsException<InvalidOperationException>(
                delegate { service.ApplyTrustedRepositoryBatch(snapshot, batch, 1L); });

            Assert.AreEqual(1, snapshot.Tables.Count);
            Assert.AreEqual("旧名称", snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual(0L, snapshot.Revision);
            Assert.AreEqual(0, service.AppliedHistory.Count);
            Assert.AreEqual(0, service.AppliedOperationIds.Count);
        }

        /// <summary>XMZADD 20260901 验证历史集合及历史对象都不能被外部修改后污染服务内部记录。</summary>
        [TestMethod]
        public void AppliedHistory_ReturnsDefensiveReadOnlyCopies()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            service.Apply(snapshot, CreateSet("history_1", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "新名称"), 1L, "111", EmptyPublishers());

            IList<AppliedDictionaryEvent> first = service.AppliedHistory;
            Assert.ThrowsException<NotSupportedException>(() => first.Add(first[0]));
            first[0].NewValue = "外部篡改";

            Assert.AreEqual("新名称", service.AppliedHistory[0].NewValue);
        }

        /// <summary>XMZADD 20260901 验证128字符合法操作号不会因内部批次前缀被误拒绝。</summary>
        [TestMethod]
        public void Apply_MaximumLengthOperationId_IsAccepted()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            string operationId = new string('a', 128);

            DictionaryEventApplyResult result = service.Apply(snapshot,
                CreateSet(operationId, "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "新名称"),
                1L, "111", EmptyPublishers());

            Assert.AreEqual(DictionaryEventApplyStatus.Applied, result.Status);
        }

        /// <summary>XMZADD 20260901 验证已应用操作号可跨进程恢复且输入输出集合均不能篡改服务状态。</summary>
        [TestMethod]
        public void AppliedOperationIds_RestoreAcrossServiceInstancesWithDefensiveCopies()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var first = new DictionaryEventApplyService();
            DictionaryChangeOperation operation = CreateSet("restore_1", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "新名称");
            first.Apply(snapshot, operation, 1L, "111", EmptyPublishers());
            var restoredInput = new List<string> { first.AppliedOperationIds[0] };
            var second = new DictionaryEventApplyService(restoredInput);
            restoredInput[0] = "tampered";

            DictionaryEventApplyResult duplicate = second.Apply(snapshot, operation, 999L, "111", EmptyPublishers());

            Assert.AreEqual(DictionaryEventApplyStatus.DuplicateIgnored, duplicate.Status);
            Assert.AreEqual("restore_1", second.AppliedOperationIds[0]);
            Assert.ThrowsException<NotSupportedException>(() => second.AppliedOperationIds.Add("external"));
        }

        /// <summary>XMZADD 20260901 验证整批新事件共享单个服务端修订且快照只推进一次。</summary>
        [TestMethod]
        public void ApplyBatch_NewOperationsShareOneServerRevisionInListOrder()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            snapshot.Revision = 3L;
            var service = new DictionaryEventApplyService();
            var batch = new DictionaryChangeBatch { BatchId = "single_revision" };
            batch.Operations.Add(CreateSet("single_1", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "名称甲"));
            batch.Operations.Add(CreateSet("single_2", "dbo.T_ORDER", "FNAME", "Usage", string.Empty, "展示"));

            IList<DictionaryEventApplyResult> results = service.ApplyBatch(snapshot, batch, 4L, "111", EmptyPublishers());

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(4L, results[0].Revision);
            Assert.AreEqual(4L, results[1].Revision);
            Assert.AreEqual(4L, service.AppliedHistory[0].Revision);
            Assert.AreEqual(4L, service.AppliedHistory[1].Revision);
            Assert.AreEqual(4L, snapshot.Revision);
            Assert.AreEqual("名称甲", snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual("展示", snapshot.Tables[0].Fields[1].Usage.Value);
        }

        /// <summary>XMZADD 20260901 验证同修订可暂时不兼容但必须在整批结束形成合法字段类型长度组合。</summary>
        [TestMethod]
        public void ApplyBatch_DataTypeAndLengthSet_ValidatesOnlyFinalCombination()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            snapshot.Tables[0].Fields[1].LengthText = "10";
            var service = new DictionaryEventApplyService();
            var batch = new DictionaryChangeBatch { BatchId = "field_shape_batch" };
            batch.Operations.Add(CreateSet("length_dash", "dbo.T_ORDER", "FNAME", "LengthText", "10", "—"));
            batch.Operations.Add(CreateSet("type_int", "dbo.T_ORDER", "FNAME", "DataType", "nvarchar", "int"));

            IList<DictionaryEventApplyResult> results = service.ApplyBatch(
                snapshot, batch, 1L, "999", new List<string> { "999" });

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual("int", snapshot.Tables[0].Fields[1].DataType);
            Assert.AreEqual("—", snapshot.Tables[0].Fields[1].LengthText);
            Assert.AreEqual(1L, snapshot.Revision);
        }

        /// <summary>XMZADD 20260901 验证批次缺少配套长度修改时整体回滚快照、历史和幂等操作号。</summary>
        [TestMethod]
        public void ApplyBatch_DataTypeSetWithoutCompatibleLength_RollsBackAllState()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            snapshot.Tables[0].Fields[1].LengthText = "10";
            var service = new DictionaryEventApplyService();
            var batch = new DictionaryChangeBatch { BatchId = "field_shape_incomplete" };
            batch.Operations.Add(CreateSet("type_int_only", "dbo.T_ORDER", "FNAME", "DataType", "nvarchar", "int"));

            Assert.ThrowsException<InvalidOperationException>(() => service.ApplyBatch(
                snapshot, batch, 1L, "999", new List<string> { "999" }));

            Assert.AreEqual("nvarchar", snapshot.Tables[0].Fields[1].DataType);
            Assert.AreEqual("10", snapshot.Tables[0].Fields[1].LengthText);
            Assert.AreEqual(0L, snapshot.Revision);
            Assert.AreEqual(0, service.AppliedHistory.Count);
            Assert.AreEqual(0, service.AppliedOperationIds.Count);
        }

        /// <summary>XMZADD 20260901 验证批量重试先忽略已应用恶意正文并保持原结果顺序。</summary>
        [TestMethod]
        public void ApplyBatch_AlreadyAppliedMalformedOperationIgnoredBeforeRemainingValidation()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var first = new DictionaryEventApplyService();
            DictionaryChangeOperation applied = CreateSet("batch_existing", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "名称甲");
            first.Apply(snapshot, applied, 5L, "111", EmptyPublishers());
            var service = new DictionaryEventApplyService(first.AppliedOperationIds);
            applied.ObjectKey = "bad/../../key";
            applied.ChangeKind = "ExecuteWorkflow";
            applied.NewValue = null;
            var batch = new DictionaryChangeBatch { BatchId = "batch_filtered" };
            batch.Operations.Add(applied);
            batch.Operations.Add(CreateSet("batch_new", "dbo.T_ORDER", "FNAME", "Usage", string.Empty, "展示"));

            IList<DictionaryEventApplyResult> results = service.ApplyBatch(snapshot, batch, 6L, "111", EmptyPublishers());

            Assert.AreEqual(DictionaryEventApplyStatus.DuplicateIgnored, results[0].Status);
            Assert.AreEqual(DictionaryEventApplyStatus.Applied, results[1].Status);
            Assert.AreEqual("展示", snapshot.Tables[0].Fields[1].Usage.Value);
            Assert.AreEqual(6L, snapshot.Revision);
        }

        /// <summary>XMZADD 20260901 验证全批均重复时即使修订等于当前也直接忽略。</summary>
        [TestMethod]
        public void ApplyBatch_AllDuplicatesAtCurrentRevision_AreIgnored()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            DictionaryChangeOperation operation = CreateSet("all_duplicate", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "名称甲");
            service.Apply(snapshot, operation, 7L, "111", EmptyPublishers());
            operation.ChangeKind = "malicious";
            var retry = new DictionaryChangeBatch { BatchId = "all_duplicate_retry" };
            retry.Operations.Add(operation);

            IList<DictionaryEventApplyResult> results = service.ApplyBatch(snapshot, retry, 7L, "111", EmptyPublishers());

            Assert.AreEqual(DictionaryEventApplyStatus.DuplicateIgnored, results[0].Status);
            Assert.AreEqual(7L, snapshot.Revision);
            Assert.AreEqual(1, service.AppliedHistory.Count);
        }

        /// <summary>XMZADD 20260901 验证首次出现的批内重复操作号仍整体拒绝且不改变任何状态。</summary>
        [TestMethod]
        public void ApplyBatch_FirstTimeDuplicateOperationId_IsRejectedAtomically()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();
            var batch = new DictionaryChangeBatch { BatchId = "batch_duplicate_new" };
            batch.Operations.Add(CreateSet("duplicate_new", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "甲"));
            batch.Operations.Add(CreateSet("duplicate_new", "dbo.T_ORDER", "FNAME", "ChineseName", "旧名称", "乙"));

            Assert.ThrowsException<InvalidOperationException>(() => service.ApplyBatch(
                snapshot, batch, 1L, "111", EmptyPublishers()));

            Assert.AreEqual("旧名称", snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual(0, service.AppliedHistory.Count);
            Assert.AreEqual(0, service.AppliedOperationIds.Count);
        }

        /// <summary>XMZADD 20260901 验证真实 JSON 往返产生的双端独立关系实例在表字段重命名后全部更新。</summary>
        [TestMethod]
        public void Apply_RenameAfterSnapshotJsonRoundTrip_UpdatesEveryRelationInstance()
        {
            SnapshotData source = CreateSnapshotWithChildAndRelation();
            source.RefreshedAt = DateTime.UtcNow;
            var codec = new SnapshotCodec();
            byte[] payload = codec.Encode(source);
            SnapshotData snapshot = codec.DecodeAndValidate(payload, codec.ComputeSha256(payload));
            var service = new DictionaryEventApplyService();
            IList<string> publishers = new List<string> { "999" };

            service.Apply(snapshot, CreateSet("rename_table", "dbo.T_ORDER", string.Empty, "ObjectName", "T_ORDER", "T_ORDER_NEW"), 1L, "999", publishers);
            service.Apply(snapshot, CreateSet("rename_field", "dbo.T_ORDER_NEW", "FID", "FieldName", "FID", "FID_NEW"), 2L, "999", publishers);

            TableMetadata parent = FindTable(snapshot, "T_ORDER_NEW");
            TableMetadata child = FindTable(snapshot, "T_CHILD");
            Assert.AreEqual("T_ORDER_NEW", parent.Relations[0].ParentTableName);
            Assert.AreEqual("T_ORDER_NEW", child.Relations[0].ParentTableName);
            Assert.AreEqual("FID_NEW", parent.Relations[0].ParentFieldName);
            Assert.AreEqual("FID_NEW", child.Relations[0].ParentFieldName);
        }

        /// <summary>XMZADD 20260901 验证空字符串清空业务值时历史记录服务端实际应用后的规范空值。</summary>
        [TestMethod]
        public void Apply_EmptyStringSet_HistoryUsesActualAppliedValue()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var service = new DictionaryEventApplyService();

            service.Apply(snapshot, CreateSet("empty_history", "dbo.T_ORDER", "FNAME", "ChineseName", "伪造", string.Empty),
                1L, "111", EmptyPublishers());

            Assert.AreEqual(string.Empty, snapshot.Tables[0].Fields[1].ChineseName.Value);
            Assert.AreEqual(string.Empty, service.AppliedHistory[0].NewValue);
            Assert.AreEqual("旧名称", service.AppliedHistory[0].OldValue);
        }

        private static SnapshotData CreateSnapshot(string fieldChineseName)
        {
            var snapshot = new SnapshotData { FormatVersion = 1, Revision = 0L };
            var table = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_ORDER",
                ObjectType = "TABLE",
                Category = DictionaryTableCategory.Business,
                ChineseName = ManualValue("订单")
            };
            table.Fields.Add(new FieldMetadata { FieldName = "FID", OwnerTableName = "T_ORDER", DataType = "int", IsPrimaryKey = true });
            table.Fields.Add(new FieldMetadata
            {
                FieldName = "FNAME",
                OwnerTableName = "T_ORDER",
                DataType = "nvarchar",
                LengthText = "20",
                ChineseName = ManualValue(fieldChineseName)
            });
            snapshot.Tables.Add(table);
            return snapshot;
        }

        private static SnapshotData CreateSnapshotWithChildAndRelation()
        {
            SnapshotData snapshot = CreateSnapshot("旧名称");
            var child = new TableMetadata { SchemaName = "dbo", ObjectName = "T_CHILD", ObjectType = "TABLE", Category = DictionaryTableCategory.Business };
            child.Fields.Add(new FieldMetadata { FieldName = "FID", OwnerTableName = "T_CHILD", DataType = "int" });
            child.Fields.Add(new FieldMetadata { FieldName = "FORDERID", OwnerTableName = "T_CHILD", DataType = "int" });
            snapshot.Tables.Add(child);
            var relation = new RelationMetadata
            {
                ForeignKeyName = "FK_CHILD_ORDER",
                ParentSchemaName = "dbo",
                ParentTableName = "T_ORDER",
                ParentFieldName = "FID",
                ChildSchemaName = "dbo",
                ChildTableName = "T_CHILD",
                ChildFieldName = "FORDERID"
            };
            snapshot.Tables[0].Relations.Add(relation);
            child.Relations.Add(relation);
            return snapshot;
        }

        private static MetadataValue ManualValue(string value)
        {
            return new MetadataValue { Value = value, OriginalAutomaticValue = "自动名称", Evidence = new List<EvidenceItem>() };
        }

        private static DictionaryChangeOperation CreateSet(string id, string objectKey, string fieldKey, string property, string oldValue, string newValue)
        {
            return new DictionaryChangeOperation
            {
                OperationId = id,
                AuthorGitHubUserId = "forged",
                ObjectKey = objectKey,
                FieldKey = fieldKey,
                PropertyName = property,
                OldValue = oldValue,
                NewValue = newValue,
                ChangeKind = "Set",
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        private static DictionaryChangeOperation CreateAddTable(string id, string objectKey)
        {
            string[] parts = objectKey.Split('.');
            return new DictionaryChangeOperation
            {
                OperationId = id,
                ObjectKey = objectKey,
                FieldKey = string.Empty,
                PropertyName = string.Empty,
                ChangeKind = "AddTable",
                TablePayload = new TableStructurePayload { SchemaName = parts[0], ObjectName = parts[1], ObjectType = "TABLE" }
            };
        }

        private static DictionaryChangeOperation CreateRemoveTable(string id, string objectKey)
        {
            return new DictionaryChangeOperation { OperationId = id, ObjectKey = objectKey, FieldKey = string.Empty, PropertyName = string.Empty, ChangeKind = "RemoveTable" };
        }

        private static DictionaryChangeOperation CreateAddField(string id, string objectKey, string fieldName)
        {
            string owner = objectKey.Substring(objectKey.IndexOf('.') + 1);
            return new DictionaryChangeOperation
            {
                OperationId = id,
                ObjectKey = objectKey,
                FieldKey = fieldName,
                PropertyName = string.Empty,
                ChangeKind = "AddField",
                FieldPayload = new FieldStructurePayload { FieldName = fieldName, OwnerTableName = owner, DataType = "int", LengthText = "—" }
            };
        }

        private static DictionaryChangeOperation CreateRemoveField(string id, string objectKey, string fieldName)
        {
            return new DictionaryChangeOperation { OperationId = id, ObjectKey = objectKey, FieldKey = fieldName, PropertyName = string.Empty, ChangeKind = "RemoveField" };
        }

        private static DictionaryChangeOperation CreateAddRelation(string id)
        {
            return CreateRelationOperation(id, "AddRelation");
        }

        private static DictionaryChangeOperation CreateRemoveRelation(string id)
        {
            return CreateRelationOperation(id, "RemoveRelation");
        }

        private static DictionaryChangeOperation CreateRelationOperation(string id, string kind)
        {
            return new DictionaryChangeOperation
            {
                OperationId = id,
                ObjectKey = "dbo.T_CHILD",
                FieldKey = string.Empty,
                PropertyName = string.Empty,
                ChangeKind = kind,
                RelationPayload = new RelationStructurePayload
                {
                    ForeignKeyName = "FK_CHILD_ORDER",
                    ParentSchemaName = "dbo",
                    ParentTableName = "T_ORDER",
                    ParentFieldName = "FID",
                    ChildSchemaName = "dbo",
                    ChildTableName = "T_CHILD",
                    ChildFieldName = "FORDERID"
                }
            };
        }

        private static IList<string> EmptyPublishers()
        {
            return new List<string>();
        }

        private static TableMetadata FindTable(SnapshotData snapshot, string objectName)
        {
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                if (string.Equals(snapshot.Tables[index].ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return snapshot.Tables[index];
                }
            }
            return null;
        }
    }
}
