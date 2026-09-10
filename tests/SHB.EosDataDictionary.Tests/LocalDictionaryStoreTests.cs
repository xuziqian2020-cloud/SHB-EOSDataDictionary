using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证 AI 配置与人工字典覆盖仅保存在本机数据库。</summary>
    [TestClass]
    public sealed class LocalDictionaryStoreTests
    {
        /// <summary>XMZADD 20260831 验证默认 AI 提供商唯一且密钥可由当前用户安全恢复。</summary>
        [TestMethod]
        public void SaveAiProvider_ReplacesExistingDefaultAndRestoresApiKey()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveAiProvider(new AiProviderConfiguration
                {
                    Id = "deepseek",
                    Name = "DeepSeek",
                    Protocol = AiProviderProtocol.OpenAiCompatible,
                    Endpoint = "https://api.deepseek.com",
                    Model = "deepseek-chat",
                    ApiKey = "first-key",
                    IsDefault = true,
                    IsEnabled = true
                });
                store.SaveAiProvider(new AiProviderConfiguration
                {
                    Id = "company",
                    Name = "公司模型",
                    Protocol = AiProviderProtocol.OpenAiCompatible,
                    Endpoint = "https://ai.example.internal",
                    Model = "company-model",
                    ApiKey = "second-key",
                    IsDefault = true,
                    IsEnabled = true
                });

                AiProviderConfiguration provider = store.GetDefaultAiProvider();
                Assert.IsNotNull(provider);
                Assert.AreEqual("company", provider.Id);
                Assert.AreEqual("second-key", provider.ApiKey);
                Assert.AreEqual(2, store.LoadAiProviders().Count);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260831 验证编辑已保存连接时留空密钥会沿用原密钥，避免配置界面静默清除凭据。</summary>
        [TestMethod]
        public void SaveAiProvider_EmptyApiKey_PreservesStoredSecret()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveAiProvider(new AiProviderConfiguration
                {
                    Id = "deepseek",
                    Name = "DeepSeek",
                    Endpoint = "https://api.deepseek.com",
                    Model = "deepseek-chat",
                    ApiKey = "stored-key",
                    IsDefault = true,
                    IsEnabled = true
                });

                store.SaveAiProvider(new AiProviderConfiguration
                {
                    Id = "deepseek",
                    Name = "DeepSeek 已编辑",
                    Endpoint = "https://api.deepseek.com",
                    Model = "deepseek-chat",
                    ApiKey = string.Empty,
                    IsDefault = true,
                    IsEnabled = true
                });

                AiProviderConfiguration provider = store.GetDefaultAiProvider();
                Assert.AreEqual("stored-key", provider.ApiKey);
                Assert.AreEqual("DeepSeek 已编辑", provider.Name);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证持久化边界会把异常批量设置限制到服务硬上限，避免旧配置在重启后反复触发失败请求。</summary>
        [TestMethod]
        public void SaveAiProvider_ExcessiveBatchSize_IsClampedToServiceLimit()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveAiProvider(new AiProviderConfiguration
                {
                    Id = "deepseek",
                    Name = "DeepSeek",
                    BatchSize = 500,
                    IsDefault = true,
                    IsEnabled = true
                });

                AiProviderConfiguration provider = store.GetDefaultAiProvider();

                Assert.AreEqual(AiInferenceService.MaximumBatchTargetCount, provider.BatchSize);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260831 验证旧版本本地表会增量补齐新列并保留原 AI 配置。</summary>
        [TestMethod]
        public void Constructor_OldProviderSchema_MigratesWithoutLosingConfiguration()
        {
            string path = CreatePath();
            try
            {
                SQLiteConnection.CreateFile(path);
                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = @"CREATE TABLE LocalAiProviders (
ProviderId TEXT NOT NULL PRIMARY KEY,
Name TEXT NOT NULL,
Protocol INTEGER NOT NULL,
Endpoint TEXT NOT NULL,
Model TEXT NOT NULL,
EncryptedApiKey BLOB,
IsDefault INTEGER NOT NULL,
IsEnabled INTEGER NOT NULL,
TimeoutSeconds INTEGER NOT NULL,
UpdatedAt TEXT NOT NULL);
INSERT INTO LocalAiProviders
(ProviderId, Name, Protocol, Endpoint, Model, EncryptedApiKey, IsDefault, IsEnabled, TimeoutSeconds, UpdatedAt)
VALUES ('deepseek', 'DeepSeek', 0, 'https://api.deepseek.com', 'deepseek-chat', NULL, 1, 1, 90, '2026-08-31T10:00:00.0000000');";
                        command.ExecuteNonQuery();
                    }
                }

                var store = new LocalDictionaryStore(path);
                AiProviderConfiguration provider = store.GetDefaultAiProvider();

                Assert.IsNotNull(provider);
                Assert.AreEqual("deepseek", provider.Id);
                Assert.IsTrue(provider.BatchSize > 0);
                Assert.IsTrue(File.Exists(path + ".backup-v1"));
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260831 验证人工维护值以表字段和属性唯一定位并能跨程序重启读取。</summary>
        [TestMethod]
        public void SaveOverride_RestoresHumanValueByItsCompleteKey()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                var expected = new DictionaryOverride
                {
                    ScopeKey = "dev|SHB",
                    ObjectName = "T_PURCHASE_ORDER",
                    FieldName = "FBILLNO",
                    PropertyName = "ChineseName",
                    ManualValue = "采购单号",
                    OriginalAutomaticValue = "推测：单据编号",
                    IsLocked = true,
                    Remark = "业务确认",
                    UpdatedAt = new DateTime(2026, 8, 31, 11, 20, 0)
                };

                store.SaveOverride(expected);

                DictionaryOverride actual = store.FindOverride("dev|SHB", "T_PURCHASE_ORDER", "FBILLNO", "ChineseName");
                Assert.IsNotNull(actual);
                Assert.AreEqual(expected.ManualValue, actual.ManualValue);
                Assert.AreEqual(expected.OriginalAutomaticValue, actual.OriginalAutomaticValue);
                Assert.IsTrue(actual.IsLocked);
                Assert.AreEqual(expected.Remark, actual.Remark);
                Assert.AreEqual(1, store.LoadOverrides("dev|SHB").Count);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260831 验证删除当前默认 AI 配置后会自动指定剩余配置为默认项。</summary>
        [TestMethod]
        public void DeleteAiProvider_PromotesRemainingProviderToDefault()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveAiProvider(new AiProviderConfiguration
                {
                    Id = "deepseek",
                    Name = "DeepSeek",
                    IsDefault = true,
                    IsEnabled = true
                });
                store.SaveAiProvider(new AiProviderConfiguration
                {
                    Id = "company",
                    Name = "公司模型",
                    IsDefault = true,
                    IsEnabled = true
                });

                store.DeleteAiProvider("company");

                Assert.AreEqual(1, store.LoadAiProviders().Count);
                Assert.AreEqual("deepseek", store.GetDefaultAiProvider().Id);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260831 验证 AI 结果按对象字段独立保存，不需要覆盖完整结构快照。</summary>
        [TestMethod]
        public void SaveAiExplanations_RestoresLatestFieldExplanation()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveAiExplanations(new List<AiExplanationResult>
                {
                    new AiExplanationResult
                    {
                        ScopeKey = "dev|SHB",
                        ObjectName = "Item_Image",
                        FieldName = "Img_ID",
                        ChineseName = "图片ID",
                        BusinessMeaning = "图片记录主键",
                        Usage = "唯一标识图片",
                        ConfidenceScore = 96,
                        ProviderId = "deepseek",
                        ProviderName = "DeepSeek",
                        Model = "deepseek-chat",
                        ConfigurationVersion = "v1",
                        GeneratedAt = new DateTime(2026, 8, 31, 14, 0, 0)
                    }
                });

                IList<AiExplanationResult> results = store.LoadAiExplanations("dev|SHB");

                Assert.AreEqual(1, results.Count);
                Assert.AreEqual("Img_ID", results[0].FieldName);
                Assert.AreEqual("图片ID", results[0].ChineseName);
                Assert.AreEqual("deepseek-chat", results[0].Model);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证一次编辑产生的多项人工覆盖和单个待上传批次在同一事务中落盘。</summary>
        [TestMethod]
        public void SaveOverridesWithPendingOperation_SavesOverridesAndOneBatchAtomically()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                var secondOverride = new DictionaryOverride
                {
                    ScopeKey = "dev|SHB",
                    ObjectName = "T_ORDER",
                    FieldName = "FBILLNO",
                    ObjectKey = "dbo.T_ORDER",
                    FieldKey = "FBILLNO",
                    PropertyName = "BusinessMeaning",
                    ManualValue = "业务单据的唯一编号",
                    UpdatedAt = batch.CreatedAtUtc
                };
                batch.Overrides.Add(secondOverride);

                store.SaveOverridesWithPendingOperation(batch.Overrides, batch);

                Assert.AreEqual(2, store.LoadOverrides("dev|SHB").Count);
                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT COUNT(1) FROM PendingDictionaryOperations;";
                        Assert.AreEqual(1, Convert.ToInt32(command.ExecuteScalar()));
                    }
                }
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证待上传队列插入失败时同一事务中的人工覆盖全部回滚。</summary>
        [TestMethod]
        public void SaveOverridesWithPendingOperation_OutboxInsertFails_RollsBackOverrides()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = @"CREATE TRIGGER RejectPendingBatch
BEFORE INSERT ON PendingDictionaryOperations
BEGIN
    SELECT RAISE(ABORT, 'forced outbox failure');
END;";
                        command.ExecuteNonQuery();
                    }
                }
                DictionaryChangeBatch batch = CreateBatch("cccccccccccccccccccccccccccccccc");

                Assert.ThrowsException<SQLiteException>(
                    () => store.SaveOverridesWithPendingOperation(batch.Overrides, batch));

                Assert.AreEqual(0, store.LoadOverrides("dev|SHB").Count);
                Assert.AreEqual(0, CountRows(path, "PendingDictionaryOperations"));
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证任一人工覆盖缺少定位键时在事务前拒绝整批数据。</summary>
        [TestMethod]
        public void SaveOverridesWithPendingOperation_InvalidOverride_SavesNothing()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("dddddddddddddddddddddddddddddddd");
                batch.Overrides.Add(new DictionaryOverride
                {
                    ScopeKey = "dev|SHB",
                    ObjectName = "T_ORDER",
                    PropertyName = string.Empty,
                    ManualValue = "无定位值"
                });

                Assert.ThrowsException<ArgumentException>(
                    () => store.SaveOverridesWithPendingOperation(batch.Overrides, batch));

                Assert.AreEqual(0, store.LoadOverrides("dev|SHB").Count);
                Assert.AreEqual(0, CountRows(path, "PendingDictionaryOperations"));
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证同一批次重复保存只保留初次载荷且不会重复入队。</summary>
        [TestMethod]
        public void SaveOverridesWithPendingOperation_SameBatchTwice_KeepsSinglePayload()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

                store.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                string originalPayload = ReadPendingPayload(path, batch.BatchId);
                store.SaveOverridesWithPendingOperation(batch.Overrides, batch);

                Assert.AreEqual(1, CountRows(path, "PendingDictionaryOperations"));
                Assert.AreEqual(originalPayload, ReadPendingPayload(path, batch.BatchId));
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证同一批次编号不能静默替换为不同载荷。</summary>
        [TestMethod]
        public void SaveOverridesWithPendingOperation_SameBatchWithDifferentPayload_Throws()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch firstBatch = CreateBatch("ffffffffffffffffffffffffffffffff");
                DictionaryChangeBatch changedBatch = CreateBatch(firstBatch.BatchId);
                changedBatch.Operations[0].NewValue = "另一份载荷";

                store.SaveOverridesWithPendingOperation(firstBatch.Overrides, firstBatch);

                Assert.ThrowsException<InvalidOperationException>(
                    () => store.SaveOverridesWithPendingOperation(changedBatch.Overrides, changedBatch));
                Assert.AreEqual(1, CountRows(path, "PendingDictionaryOperations"));
                Assert.AreEqual(
                    DictionaryJsonSerializer.SerializeBatch(firstBatch),
                    ReadPendingPayload(path, firstBatch.BatchId));
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证读取待上传队列时恢复批次载荷和全部调度状态字段。</summary>
        [TestMethod]
        public void LoadPendingOperations_RestoresBatchAndQueueMetadata()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("12121212121212121212121212121212");
                store.SaveOverridesWithPendingOperation(batch.Overrides, batch);

                IList<PendingDictionaryOperation> pending = store.LoadPendingOperations();

                Assert.AreEqual(1, pending.Count);
                Assert.AreEqual(batch.BatchId, pending[0].OperationId);
                Assert.AreEqual(batch.BatchId, pending[0].Batch.BatchId);
                Assert.AreEqual(PendingOperationStatus.Pending, pending[0].Status);
                Assert.AreEqual(0, pending[0].AttemptCount);
                Assert.IsNull(pending[0].NextAttemptAtUtc);
                Assert.IsNull(pending[0].GitHubIssueNumber);
                Assert.AreEqual(string.Empty, pending[0].LastError);
                Assert.AreEqual(DateTimeKind.Utc, pending[0].CreatedAtUtc.Kind);
                Assert.AreEqual(DateTimeKind.Utc, pending[0].UpdatedAtUtc.Kind);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260903 验证周期同步只读取仍需处理的队列记录，不反序列化永久保留的完成历史。</summary>
        [TestMethod]
        public void LoadActivePendingOperations_ExcludesCompletedHistory()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("13131313131313131313131313131313");
                store.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                store.MarkOperationCompleted(batch.BatchId);

                Assert.AreEqual(0, store.LoadActivePendingOperations().Count);
                Assert.AreEqual(1, store.LoadPendingOperations().Count);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260909 验证远端拒绝和被后续规范值取代的历史不会继续占用待生效队列。</summary>
        [TestMethod]
        public void LoadActivePendingOperations_ExcludesRejectedAndSupersededHistory()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch rejected = CreateBatch("17171717171717171717171717171717");
                DictionaryChangeBatch superseded = CreateBatch("18181818181818181818181818181818");
                store.SaveOverridesWithPendingOperation(rejected.Overrides, rejected);
                store.SaveOverridesWithPendingOperation(superseded.Overrides, superseded);

                store.MarkOperationRejected(rejected.BatchId, "GitHub Issue 校验失败。");
                store.MarkOperationSuperseded(superseded.BatchId, "已被后续规范值取代。");

                Assert.AreEqual(2, store.LoadPendingOperations().Count);
                Assert.AreEqual(0, store.LoadActivePendingOperations().Count);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证提交成功后保留本地批次并记录远程问题编号。</summary>
        [TestMethod]
        public void MarkOperationSubmitted_KeepsBatchAndStoresIssueNumber()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("13131313131313131313131313131313");
                store.SaveOverridesWithPendingOperation(batch.Overrides, batch);

                store.MarkOperationSubmitted(batch.BatchId, 2468);

                PendingDictionaryOperation pending = store.LoadPendingOperations()[0];
                Assert.AreEqual(PendingOperationStatus.Submitted, pending.Status);
                Assert.AreEqual(2468, pending.GitHubIssueNumber);
                Assert.AreEqual(batch.BatchId, pending.Batch.BatchId);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证远程流程完成后仍保留本地编辑记录用于审计。</summary>
        [TestMethod]
        public void MarkOperationCompleted_KeepsCompletedRecordAndOverrides()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("14141414141414141414141414141414");
                store.SaveOverridesWithPendingOperation(batch.Overrides, batch);

                store.MarkOperationCompleted(batch.BatchId);

                PendingDictionaryOperation pending = store.LoadPendingOperations()[0];
                Assert.AreEqual(PendingOperationStatus.Completed, pending.Status);
                Assert.AreEqual(1, store.LoadOverrides("dev|SHB").Count);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证瞬时失败增加尝试次数并保持待处理状态直到下次调度时间。</summary>
        [TestMethod]
        public void RecordOperationRetry_KeepsPendingAndSchedulesNextAttempt()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("15151515151515151515151515151515");
                DateTime nextAttemptAtUtc = new DateTime(2026, 9, 1, 3, 4, 5, DateTimeKind.Utc);
                store.SaveOverridesWithPendingOperation(batch.Overrides, batch);

                store.RecordOperationRetry(batch.BatchId, nextAttemptAtUtc, "网络暂时不可用");

                PendingDictionaryOperation pending = store.LoadPendingOperations()[0];
                Assert.AreEqual(PendingOperationStatus.Pending, pending.Status);
                Assert.AreEqual(1, pending.AttemptCount);
                Assert.AreEqual(nextAttemptAtUtc, pending.NextAttemptAtUtc);
                Assert.AreEqual("网络暂时不可用", pending.LastError);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证永久失败保留编辑和队列记录并限制诊断文本长度。</summary>
        [TestMethod]
        public void MarkOperationFailed_KeepsRecordAndLimitsErrorLength()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("16161616161616161616161616161616");
                string longError = new string('错', 3000);
                store.SaveOverridesWithPendingOperation(batch.Overrides, batch);

                store.MarkOperationFailed(batch.BatchId, longError);

                PendingDictionaryOperation pending = store.LoadPendingOperations()[0];
                Assert.AreEqual(PendingOperationStatus.Failed, pending.Status);
                Assert.AreEqual(2000, pending.LastError.Length);
                Assert.AreEqual(1, store.LoadOverrides("dev|SHB").Count);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证共享字典修订游标和最后成功操作编号可跨进程恢复。</summary>
        [TestMethod]
        public void SaveSyncState_RoundTripsRevisionAndLastSuccessfulOperation()
        {
            string path = CreatePath();
            try
            {
                var store = new LocalDictionaryStore(path);
                DateTime lastSyncAtUtc = new DateTime(2026, 9, 1, 4, 5, 6, DateTimeKind.Utc);
                var expected = new DictionarySyncState
                {
                    StateKey = "shared-dictionary",
                    Revision = 88L,
                    ManifestHash = "abc123",
                    LastSyncAtUtc = lastSyncAtUtc,
                    LastSuccessfulOperationId = "17171717171717171717171717171717"
                };

                store.SaveSyncState(expected);
                DictionarySyncState actual = store.LoadSyncState(expected.StateKey);

                Assert.IsNotNull(actual);
                Assert.AreEqual(expected.StateKey, actual.StateKey);
                Assert.AreEqual(expected.Revision, actual.Revision);
                Assert.AreEqual(expected.ManifestHash, actual.ManifestHash);
                Assert.AreEqual(lastSyncAtUtc, actual.LastSyncAtUtc);
                Assert.AreEqual(expected.LastSuccessfulOperationId, actual.LastSuccessfulOperationId);
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证版本二数据库无损补建队列和同步游标表且不会误建列迁移备份。</summary>
        [TestMethod]
        public void Constructor_UserVersion2_AddsOutboxAndSyncStateWithoutLosingOverrides()
        {
            string path = CreatePath();
            try
            {
                var oldStore = new LocalDictionaryStore(path);
                oldStore.SaveOverride(CreateBatch("18181818181818181818181818181818").Overrides[0]);
                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = @"DROP TABLE PendingDictionaryOperations;
DROP TABLE DictionarySyncState;
PRAGMA user_version = 2;";
                        command.ExecuteNonQuery();
                    }
                }

                var upgradedStore = new LocalDictionaryStore(path);

                Assert.AreEqual(1, upgradedStore.LoadOverrides("dev|SHB").Count);
                Assert.IsTrue(TableExists(path, "PendingDictionaryOperations"));
                Assert.IsTrue(TableExists(path, "DictionarySyncState"));
                Assert.AreEqual(3, ReadUserVersion(path));
                Assert.IsFalse(File.Exists(path + ".backup-v1"));
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证版本二的部分队列表和游标表补齐非主键列并保留可映射基础行。</summary>
        [TestMethod]
        public void Constructor_UserVersion2PartialSyncTables_AddsColumnsPreservesRowsAndBacksUpOnce()
        {
            string path = CreatePath();
            try
            {
                var initialStore = new LocalDictionaryStore(path);
                DictionaryChangeBatch batch = CreateBatch("19191919191919191919191919191919");
                string payloadJson = DictionaryJsonSerializer.SerializeBatch(batch);
                DateTime createdAtUtc = new DateTime(2026, 9, 1, 5, 6, 7, DateTimeKind.Utc);
                DateTime lastSyncAtUtc = new DateTime(2026, 9, 1, 6, 7, 8, DateTimeKind.Utc);
                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = @"DROP TABLE PendingDictionaryOperations;
DROP TABLE DictionarySyncState;
CREATE TABLE PendingDictionaryOperations (
    OperationId TEXT PRIMARY KEY,
    PayloadJson TEXT NOT NULL,
    Status INTEGER NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);
CREATE TABLE DictionarySyncState (
    StateKey TEXT PRIMARY KEY,
    Revision INTEGER NOT NULL,
    LastSyncAtUtc TEXT
);
INSERT INTO PendingDictionaryOperations
(OperationId, PayloadJson, Status, CreatedAtUtc)
VALUES (@OperationId, @PayloadJson, 0, @CreatedAtUtc);
INSERT INTO DictionarySyncState
(StateKey, Revision, LastSyncAtUtc)
VALUES ('shared-dictionary', 37, @LastSyncAtUtc);
PRAGMA user_version = 2;";
                        command.Parameters.AddWithValue("@OperationId", batch.BatchId);
                        command.Parameters.AddWithValue("@PayloadJson", payloadJson);
                        command.Parameters.AddWithValue("@CreatedAtUtc", createdAtUtc.ToString("O"));
                        command.Parameters.AddWithValue("@LastSyncAtUtc", lastSyncAtUtc.ToString("O"));
                        command.ExecuteNonQuery();
                    }
                }

                var upgradedStore = new LocalDictionaryStore(path);

                AssertTableHasColumns(path, "PendingDictionaryOperations", new[]
                {
                    "OperationId", "PayloadJson", "Status", "AttemptCount", "NextAttemptAtUtc",
                    "GitHubIssueNumber", "LastError", "CreatedAtUtc", "UpdatedAtUtc"
                });
                AssertTableHasColumns(path, "DictionarySyncState", new[]
                {
                    "StateKey", "Revision", "ManifestHash", "LastSyncAtUtc", "LastSuccessfulOperationId"
                });
                IList<PendingDictionaryOperation> pending = upgradedStore.LoadPendingOperations();
                DictionarySyncState state = upgradedStore.LoadSyncState("shared-dictionary");
                Assert.AreEqual(1, pending.Count);
                Assert.AreEqual(batch.BatchId, pending[0].Batch.BatchId);
                Assert.AreEqual(0, pending[0].AttemptCount);
                Assert.AreEqual(37L, state.Revision);
                Assert.AreEqual(lastSyncAtUtc, state.LastSyncAtUtc);
                Assert.AreEqual(3, ReadUserVersion(path));
                Assert.IsTrue(File.Exists(path + ".backup-v1"));
                DateTime backupWriteTimeUtc = File.GetLastWriteTimeUtc(path + ".backup-v1");

                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteTransaction transaction = connection.BeginTransaction())
                    {
                        using (SQLiteCommand command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = "UPDATE DictionarySyncState SET Revision = Revision;";
                            command.ExecuteNonQuery();
                        }
                        var reopenedStore = new LocalDictionaryStore(path);
                        Assert.IsNotNull(reopenedStore);
                        transaction.Rollback();
                    }
                }
                Assert.AreEqual(backupWriteTimeUtc, File.GetLastWriteTimeUtc(path + ".backup-v1"));
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证列齐但缺少主键约束的本地同步表可重建并按行序保留每键首条数据。</summary>
        [TestMethod]
        public void Constructor_SyncTablesWithoutPrimaryKeys_RebuildsAndDeduplicatesPreservingFirstRows()
        {
            string path = CreatePath();
            try
            {
                new LocalDictionaryStore(path);
                DictionaryChangeBatch firstBatch = CreateBatch("21212121212121212121212121212121");
                DictionaryChangeBatch duplicateBatch = CreateBatch(firstBatch.BatchId);
                duplicateBatch.Operations[0].NewValue = "重复键的第二份载荷";
                DictionaryChangeBatch uniqueBatch = CreateBatch("22222222222222222222222222222222");
                string firstPayload = DictionaryJsonSerializer.SerializeBatch(firstBatch);
                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = @"DROP TABLE PendingDictionaryOperations;
DROP TABLE DictionarySyncState;
CREATE TABLE PendingDictionaryOperations (
    OperationId TEXT NOT NULL,
    PayloadJson TEXT NOT NULL,
    Status INTEGER NOT NULL,
    AttemptCount INTEGER NOT NULL,
    NextAttemptAtUtc TEXT,
    GitHubIssueNumber INTEGER,
    LastError TEXT,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE TABLE DictionarySyncState (
    StateKey TEXT NOT NULL,
    Revision INTEGER NOT NULL,
    ManifestHash TEXT,
    LastSyncAtUtc TEXT,
    LastSuccessfulOperationId TEXT
);
INSERT INTO PendingDictionaryOperations VALUES
(@DuplicateId, @FirstPayload, 0, 1, NULL, NULL, 'first', @FirstCreated, @FirstCreated);
INSERT INTO PendingDictionaryOperations VALUES
(@DuplicateId, @DuplicatePayload, 3, 9, NULL, 99, 'second', @SecondCreated, @SecondCreated);
INSERT INTO PendingDictionaryOperations VALUES
(@UniqueId, @UniquePayload, 1, 2, NULL, 100, 'unique', @FirstCreated, @FirstCreated);
INSERT INTO DictionarySyncState VALUES
('shared-dictionary', 11, 'first-hash', @FirstCreated, @DuplicateId);
INSERT INTO DictionarySyncState VALUES
('shared-dictionary', 22, 'second-hash', @SecondCreated, @DuplicateId);
INSERT INTO DictionarySyncState VALUES
('another-dictionary', 33, 'unique-hash', @FirstCreated, @UniqueId);
PRAGMA user_version = 3;";
                        command.Parameters.AddWithValue("@DuplicateId", firstBatch.BatchId);
                        command.Parameters.AddWithValue("@FirstPayload", firstPayload);
                        command.Parameters.AddWithValue("@DuplicatePayload", DictionaryJsonSerializer.SerializeBatch(duplicateBatch));
                        command.Parameters.AddWithValue("@UniqueId", uniqueBatch.BatchId);
                        command.Parameters.AddWithValue("@UniquePayload", DictionaryJsonSerializer.SerializeBatch(uniqueBatch));
                        command.Parameters.AddWithValue("@FirstCreated", "2026-09-01T07:08:09.0000000Z");
                        command.Parameters.AddWithValue("@SecondCreated", "2026-09-01T08:09:10.0000000Z");
                        command.ExecuteNonQuery();
                    }
                }
                Assert.AreEqual(0, ReadPrimaryKeyPosition(path, "PendingDictionaryOperations", "OperationId"));
                Assert.AreEqual(0, ReadPrimaryKeyPosition(path, "DictionarySyncState", "StateKey"));
                Assert.AreEqual(3, CountRows(path, "PendingDictionaryOperations"));
                Assert.AreEqual(3, CountRows(path, "DictionarySyncState"));

                var upgradedStore = new LocalDictionaryStore(path);

                Assert.IsTrue(ReadPrimaryKeyPosition(path, "PendingDictionaryOperations", "OperationId") > 0);
                Assert.IsTrue(ReadPrimaryKeyPosition(path, "DictionarySyncState", "StateKey") > 0);
                Assert.AreEqual(2, CountRows(path, "PendingDictionaryOperations"));
                Assert.AreEqual(2, CountRows(path, "DictionarySyncState"));
                Assert.AreEqual(firstPayload, ReadPendingPayload(path, firstBatch.BatchId));
                Assert.AreEqual(11L, upgradedStore.LoadSyncState("shared-dictionary").Revision);
                Assert.AreEqual(33L, upgradedStore.LoadSyncState("another-dictionary").Revision);
                Assert.AreEqual(3, ReadUserVersion(path));
                Assert.IsTrue(File.Exists(path + ".backup-v1"));
                DateTime backupWriteTimeUtc = File.GetLastWriteTimeUtc(path + ".backup-v1");

                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteTransaction transaction = connection.BeginTransaction())
                    {
                        using (SQLiteCommand command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = "UPDATE DictionarySyncState SET Revision = Revision;";
                            command.ExecuteNonQuery();
                        }
                        var reopenedStore = new LocalDictionaryStore(path);
                        Assert.IsNotNull(reopenedStore);
                        transaction.Rollback();
                    }
                }
                Assert.AreEqual(backupWriteTimeUtc, File.GetLastWriteTimeUtc(path + ".backup-v1"));
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证当前结构启动只读检测不会因其他连接持有写事务而申请写锁。</summary>
        [TestMethod]
        public void Constructor_CurrentSchema_DoesNotRequireWriteLock()
        {
            string path = CreatePath();
            try
            {
                new LocalDictionaryStore(path);
                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteTransaction transaction = connection.BeginTransaction())
                    {
                        using (SQLiteCommand command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = "UPDATE DictionaryOverrides SET Remark = Remark;";
                            command.ExecuteNonQuery();
                        }
                        var reopenedStore = new LocalDictionaryStore(path);
                        Assert.IsNotNull(reopenedStore);
                        transaction.Rollback();
                    }
                }
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 创建包含稳定本地批次键和远程幂等操作键的最小字典编辑批次。</summary>
        private static DictionaryChangeBatch CreateBatch(string batchId)
        {
            DateTime createdAtUtc = new DateTime(2026, 9, 1, 2, 3, 4, DateTimeKind.Utc);
            var batch = new DictionaryChangeBatch
            {
                BatchId = batchId,
                AuthorGitHubUserId = "github-user-1",
                CreatedAtUtc = createdAtUtc
            };
            batch.Overrides.Add(new DictionaryOverride
            {
                ScopeKey = "dev|SHB",
                ObjectName = "T_ORDER",
                FieldName = "FBILLNO",
                ObjectKey = "dbo.T_ORDER",
                FieldKey = "FBILLNO",
                PropertyName = "ChineseName",
                ManualValue = "单据编号",
                UpdatedAt = createdAtUtc
            });
            batch.Operations.Add(new DictionaryChangeOperation
            {
                OperationId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                AuthorGitHubUserId = "github-user-1",
                ObjectKey = "dbo.T_ORDER",
                FieldKey = "FBILLNO",
                PropertyName = "ChineseName",
                OldValue = "编号",
                NewValue = "单据编号",
                ChangeKind = "Set",
                CreatedAtUtc = createdAtUtc
            });
            return batch;
        }

        /// <summary>XMZADD 20260901 查询测试数据库中的队列行数以验证事务和幂等结果。</summary>
        private static int CountRows(string path, string tableName)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(1) FROM [" + tableName + "];";
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        /// <summary>XMZADD 20260901 读取指定批次的原始队列载荷以确认重复保存没有生成第二份内容。</summary>
        private static string ReadPendingPayload(string path, string batchId)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT PayloadJson FROM PendingDictionaryOperations WHERE OperationId = @OperationId;";
                    command.Parameters.AddWithValue("@OperationId", batchId);
                    return command.ExecuteScalar().ToString();
                }
            }
        }

        /// <summary>XMZADD 20260901 查询迁移后的 SQLite 表是否存在。</summary>
        private static bool TableExists(string path, string tableName)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @TableName;";
                    command.Parameters.AddWithValue("@TableName", tableName);
                    return Convert.ToInt32(command.ExecuteScalar()) == 1;
                }
            }
        }

        /// <summary>XMZADD 20260901 读取测试数据库结构版本以确认迁移游标已推进。</summary>
        private static int ReadUserVersion(string path)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA user_version;";
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        /// <summary>XMZADD 20260901 断言部分旧表迁移后包含计划中的全部列。</summary>
        private static void AssertTableHasColumns(string path, string tableName, string[] columnNames)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                for (int i = 0; i < columnNames.Length; i++)
                {
                    bool found = false;
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "PRAGMA table_info([" + tableName + "]);";
                        using (SQLiteDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                if (string.Equals(reader["name"].ToString(), columnNames[i], StringComparison.OrdinalIgnoreCase))
                                {
                                    found = true;
                                    break;
                                }
                            }
                        }
                    }
                    Assert.IsTrue(found, tableName + " 缺少列 " + columnNames[i]);
                }
            }
        }

        /// <summary>XMZADD 20260901 读取 SQLite 列的主键序号以区分普通列和真实主键约束。</summary>
        private static int ReadPrimaryKeyPosition(string path, string tableName, string columnName)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA table_info([" + tableName + "]);";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (string.Equals(reader["name"].ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                            {
                                return Convert.ToInt32(reader["pk"]);
                            }
                        }
                    }
                }
            }
            return 0;
        }

        /// <summary>XMZADD 20260831 创建与其他测试隔离的本地字典数据库路径。</summary>
        private static string CreatePath()
        {
            return Path.Combine(Path.GetTempPath(), "shb-local-dictionary-" + Guid.NewGuid().ToString("N") + ".db");
        }

        /// <summary>XMZADD 20260831 删除测试产生的本地字典数据库及其 SQLite 日志文件。</summary>
        private static void DeletePath(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            if (File.Exists(path + "-journal"))
            {
                File.Delete(path + "-journal");
            }
            if (File.Exists(path + ".backup-v1"))
            {
                File.Delete(path + ".backup-v1");
            }
        }
    }
}
