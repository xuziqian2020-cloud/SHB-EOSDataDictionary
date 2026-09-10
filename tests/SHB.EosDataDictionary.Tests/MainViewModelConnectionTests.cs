using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260828 验证连接验证与结构加载使用彼此独立的界面状态。</summary>
    [TestClass]
    public sealed class MainViewModelConnectionTests
    {
        /// <summary>XMZADD 20260828 验证连接成功只更新当前连接，不提前替换用户正在查看的结构。</summary>
        [TestMethod]
        public void MarkConnectionValidated_KeepsPreviewUntilLoadAll()
        {
            var viewModel = new MainViewModel();
            int originalCount = viewModel.TableRows.Count;
            var profile = new ConnectionProfile
            {
                Name = "EOS 正式库",
                Server = "database.example.local",
                Database = "EOS_TEST"
            };

            viewModel.MarkConnectionValidated(profile, "EOS_ACTUAL");

            Assert.AreSame(profile, viewModel.CurrentProfile);
            Assert.AreEqual("EOS 正式库", viewModel.CurrentConnectionText);
            Assert.AreEqual("EOS_ACTUAL", viewModel.CurrentDatabaseText);
            Assert.AreEqual(originalCount, viewModel.TableRows.Count);
            StringAssert.Contains(viewModel.StatusLine, "连接成功");
        }

        /// <summary>XMZADD 20260828 验证取消全量加载会保留上一次完整结构并恢复非忙碌状态。</summary>
        [TestMethod]
        public async Task LoadProfileAsync_PreCancelled_KeepsPreviousSnapshot()
        {
            var viewModel = new MainViewModel();
            int originalCount = viewModel.TableRows.Count;
            var profile = new ConnectionProfile
            {
                Name = "测试库",
                Server = "invalid.example.local",
                Database = "EOS_INVALID"
            };
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => viewModel.LoadProfileAsync(profile, cancellation.Token));

            Assert.AreEqual(originalCount, viewModel.TableRows.Count);
            Assert.IsFalse(viewModel.IsBusy);
            Assert.AreEqual("已取消加载，保留上一次结构。", viewModel.StatusLine);
        }

        /// <summary>XMZADD 20260828 验证本地快照提交成功后即进入不可取消发布阶段，避免本地与界面状态分裂。</summary>
        [TestMethod]
        public void CompleteCommittedLoad_CancellationAfterPersistence_StillPublishesSnapshot()
        {
            MethodInfo commitMethod = typeof(MainViewModel).GetMethod(
                "CompleteCommittedLoad",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(commitMethod);
            var cancellation = new CancellationTokenSource();
            bool published = false;
            Action persist = () => cancellation.Cancel();
            Action publish = () => published = true;

            commitMethod.Invoke(null, new object[] { persist, publish });

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.IsTrue(published);
        }

        /// <summary>XMZADD 20260901 验证主视图公开共享字典同步状态，且默认不把连接信息当作主界面状态。</summary>
        [TestMethod]
        public void Constructor_ExposesSharedDictionarySyncState()
        {
            var viewModel = new MainViewModel();

            Assert.AreEqual("等待同步", viewModel.SyncStatusText);
            Assert.AreEqual(0, viewModel.PendingUploadCount);
            Assert.AreEqual(0, viewModel.PendingApplyCount);
            Assert.AreEqual("尚未获取", viewModel.LastSyncText);
            Assert.IsTrue(viewModel.IsOffline);
            Assert.IsFalse(viewModel.CanOpenStructureMaintenance);
        }

        /// <summary>XMZADD 20260903 验证本机队列分别统计尚未上传和已上传待远端生效，避免人工修改被误显示为零。</summary>
        [TestMethod]
        public void NotifyLocalDictionarySaved_RefreshesPendingAndSubmittedCounts()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shb-sync-summary-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new LocalDictionaryStore(path);
                DictionaryChangeBatch pending = CreatePendingBatch();
                DictionaryChangeBatch submitted = CreatePendingBatch();
                DictionaryChangeBatch legacyFailed = CreatePendingBatch();
                store.SaveOverridesWithPendingOperation(pending.Overrides, pending);
                store.SaveOverridesWithPendingOperation(submitted.Overrides, submitted);
                store.SaveOverridesWithPendingOperation(legacyFailed.Overrides, legacyFailed);
                store.MarkOperationSubmitted(submitted.BatchId, 12);
                store.MarkOperationFailed(legacyFailed.BatchId, "旧版本失败");
                var viewModel = new MainViewModel(path, System.IO.Path.GetTempPath());

                viewModel.NotifyLocalDictionarySaved();

                Assert.AreEqual(2, viewModel.PendingUploadCount);
                Assert.AreEqual(1, viewModel.PendingApplyCount);
            }
            finally
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                if (System.IO.File.Exists(path + "-journal")) System.IO.File.Delete(path + "-journal");
            }
        }

        /// <summary>XMZADD 20260903 验证恢复共享快照时继续叠加尚未生效的本机人工值，防止同步等待期界面回退。</summary>
        [TestMethod]
        public async Task RestoreMostRecentSnapshotAsync_AppliesPendingLocalOverride()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shb-local-override-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                string scopeKey = new DictionaryRepositoryOptions().ScopeKey;
                var snapshotStore = new SnapshotStore(path);
                snapshotStore.ReplaceScope(scopeKey, CreateSnapshot("T_PENDING", DateTime.UtcNow));
                DictionaryChangeBatch batch = CreatePendingBatch();
                batch.Overrides[0].ScopeKey = scopeKey;
                batch.Overrides[0].ObjectName = "T_PENDING";
                new LocalDictionaryStore(path).SaveOverridesWithPendingOperation(batch.Overrides, batch);
                var viewModel = new MainViewModel(path, System.IO.Path.GetTempPath());

                bool restored = await viewModel.RestoreMostRecentSnapshotAsync(CancellationToken.None);

                Assert.IsTrue(restored);
                Assert.AreEqual("本机人工名称", viewModel.TableRows[0].ChineseName);
            }
            finally
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                if (System.IO.File.Exists(path + "-journal")) System.IO.File.Delete(path + "-journal");
            }
        }

        /// <summary>XMZADD 20260901 验证启动恢复固定读取 GitHub 规范缓存，不会被更新较晚的旧连接快照抢占。</summary>
        [TestMethod]
        public async Task RestoreMostRecentSnapshotAsync_PrefersGitHubCanonicalScopeOverLegacyConnectionScope()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shb-view-model-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new SnapshotStore(path);
                store.ReplaceScope(new DictionaryRepositoryOptions().ScopeKey,
                    CreateSnapshot("T_CANONICAL", new DateTime(2026, 9, 1, 8, 0, 0)));
                store.ReplaceScope("legacy|SHB",
                    CreateSnapshot("T_LEGACY", new DateTime(2026, 9, 1, 9, 0, 0)));
                var viewModel = new MainViewModel(path, System.IO.Path.GetTempPath());

                bool restored = await viewModel.RestoreMostRecentSnapshotAsync(CancellationToken.None);

                Assert.IsTrue(restored);
                Assert.AreEqual(1, viewModel.TableRows.Count);
                Assert.AreEqual("T_CANONICAL", viewModel.TableRows[0].ObjectName);
            }
            finally
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证规范缓存恢复失败时提示 GitHub 同步，而不是引导普通用户连接数据库。</summary>
        [TestMethod]
        public void MarkSnapshotRestoreFailed_UsesSharedDictionarySyncState()
        {
            var viewModel = new MainViewModel();

            viewModel.MarkSnapshotRestoreFailed("缓存损坏");

            Assert.AreEqual("本地规范缓存恢复失败", viewModel.SyncStatusText);
            Assert.IsTrue(viewModel.IsOffline);
            StringAssert.Contains(viewModel.BannerText, "GitHub");
            Assert.IsFalse(viewModel.BannerText.Contains("连接 EOS"));
        }

        /// <summary>XMZADD 20260901 构造指定时间的最小表快照，用于区分规范缓存与旧连接缓存。</summary>
        private static SnapshotData CreateSnapshot(string objectName, DateTime refreshedAt)
        {
            var snapshot = new SnapshotData { RefreshedAt = refreshedAt };
            snapshot.Tables.Add(new TableMetadata
            {
                ScopeKey = new DictionaryRepositoryOptions().ScopeKey,
                SchemaName = "dbo",
                ObjectName = objectName,
                ObjectType = "TABLE",
                ChineseName = new MetadataValue { Value = objectName, Status = ConfidenceStatus.CodeEvidence }
            });
            return snapshot;
        }

        /// <summary>XMZADD 20260903 创建可写入本地 outbox 的表名人工修改批次，供同步数量与覆盖恢复测试共用。</summary>
        private static DictionaryChangeBatch CreatePendingBatch()
        {
            DateTime createdAtUtc = new DateTime(2026, 9, 3, 1, 2, 3, DateTimeKind.Utc);
            var batch = new DictionaryChangeBatch
            {
                BatchId = Guid.NewGuid().ToString("N"),
                AuthorGitHubUserId = "123",
                CreatedAtUtc = createdAtUtc
            };
            batch.Overrides.Add(new DictionaryOverride
            {
                ScopeKey = new DictionaryRepositoryOptions().ScopeKey,
                ObjectName = "T_TEST",
                ObjectKey = "dbo.T_TEST",
                PropertyName = "ChineseName",
                ManualValue = "本机人工名称",
                IsLocked = true,
                UpdatedAt = createdAtUtc
            });
            batch.Operations.Add(new DictionaryChangeOperation
            {
                OperationId = Guid.NewGuid().ToString("N"),
                AuthorGitHubUserId = "123",
                ObjectKey = "dbo.T_TEST",
                PropertyName = "ChineseName",
                NewValue = "本机人工名称",
                ChangeKind = "Set",
                CreatedAtUtc = createdAtUtc
            });
            return batch;
        }
    }
}
