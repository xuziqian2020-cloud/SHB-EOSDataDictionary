using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class SnapshotStoreTests
    {
        /// <summary>XMZADD 20260908 验证已是当前结构的快照只读重开不会改写用户数据库文件。</summary>
        [TestMethod]
        public void Constructor_CurrentSchema_PreservesDatabaseHash()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-readonly-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new SnapshotStore(path);
                store.ReplaceScope("shared", CreateSnapshot("shared", "T_ORDER", "订单"));
                SQLiteConnection.ClearAllPools();
                string hashBefore = ComputeFileSha256(path);

                new SnapshotStore(path);
                SQLiteConnection.ClearAllPools();

                Assert.AreEqual(hashBefore, ComputeFileSha256(path));
            }
            finally
            {
                SQLiteConnection.ClearAllPools();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [TestMethod]
        public void ReplaceAndSearch_IsolatedByScope()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new SnapshotStore(path);
                store.ReplaceScope("p1|DB1", new SnapshotData
                {
                    RefreshedAt = DateTime.Now,
                    Tables = new List<TableMetadata>
                    {
                        new TableMetadata
                        {
                            ScopeKey = "p1|DB1",
                            SchemaName = "dbo",
                            ObjectName = "T_DEMO_ORDER",
                            ObjectType = "TABLE",
                            ChineseName = new MetadataValue { Value = "示例订单", Status = ConfidenceStatus.Guessed }
                        }
                    }
                });

                Assert.AreEqual(1, store.SearchTables("p1|DB1", "订单", false).Count);
                Assert.AreEqual(0, store.SearchTables("p2|DB2", "订单", false).Count);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260828 验证取消快照替换会回滚事务并保留上一次完整结构。</summary>
        [TestMethod]
        public void ReplaceScope_PreCancelled_KeepsExistingSnapshot()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "p1|DB1";
            try
            {
                var store = new SnapshotStore(path);
                store.ReplaceScope(scope, CreateSnapshot(scope, "T_OLD", "旧结构"));
                var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                Assert.ThrowsException<OperationCanceledException>(
                    () => store.ReplaceScope(scope, CreateSnapshot(scope, "T_NEW", "新结构"), cancellation.Token));

                Assert.AreEqual(1, store.SearchTables(scope, "T_OLD", true).Count);
                Assert.AreEqual(0, store.SearchTables(scope, "T_NEW", true).Count);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证完整本地快照可在程序重启后还原字段、枚举与关系信息。</summary>
        [TestMethod]
        public void ReplaceAndLoadLatest_RestoresWholeSnapshot()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "p1|DB1";
            try
            {
                var store = new SnapshotStore(path);
                var snapshot = new SnapshotData
                {
                    RefreshedAt = new DateTime(2026, 8, 31, 10, 30, 0),
                    Tables = new List<TableMetadata>
                    {
                        new TableMetadata
                        {
                            ScopeKey = scope,
                            SchemaName = "dbo",
                            ObjectName = "T_ORDER",
                            ObjectType = "TABLE",
                            ChineseName = new MetadataValue { Value = "订单", Status = ConfidenceStatus.AiGuessed },
                            Fields = new List<FieldMetadata>
                            {
                                new FieldMetadata
                                {
                                    FieldName = "FNumber",
                                    OwnerTableName = "T_ORDER",
                                    ChineseName = new MetadataValue { Value = "订单编号", Status = ConfidenceStatus.CodeEvidence },
                                    DataType = "nvarchar",
                                    LengthText = "50",
                                    IsRequired = true,
                                    EnumItems = new List<EnumItemMetadata>
                                    {
                                        new EnumItemMetadata { Value = "A", ChineseName = new MetadataValue { Value = "已审核" } }
                                    }
                                }
                            },
                            Relations = new List<RelationMetadata>
                            {
                                new RelationMetadata
                                {
                                    ScopeKey = scope,
                                    ParentTableName = "T_CUSTOMER",
                                    ParentFieldName = "FId",
                                    ChildTableName = "T_ORDER",
                                    ChildFieldName = "FCustomerId",
                                    RelationType = new MetadataValue { Value = "一对多" }
                                }
                            }
                        }
                    }
                };

                store.ReplaceScope(scope, snapshot);

                SnapshotData restored;
                Assert.IsTrue(store.TryLoadLatest(scope, out restored));
                Assert.AreEqual(snapshot.RefreshedAt, restored.RefreshedAt);
                Assert.AreEqual("订单", restored.Tables[0].ChineseName.Value);
                Assert.AreEqual("订单编号", restored.Tables[0].Fields[0].ChineseName.Value);
                Assert.AreEqual("已审核", restored.Tables[0].Fields[0].EnumItems[0].ChineseName.Value);
                Assert.AreEqual("一对多", restored.Tables[0].Relations[0].RelationType.Value);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证人工维护内容保存在本地数据库后，重新创建存储对象仍可恢复。</summary>
        [TestMethod]
        public void SaveAndLoadOverrides_PreservesManualValueAcrossStoreInstances()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "p1|DB1";
            try
            {
                var firstStore = new SnapshotStore(path);
                firstStore.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_ORDER",
                    FieldName = "FNumber",
                    PropertyName = "ChineseName",
                    ManualValue = "订单编号",
                    OriginalAutomaticValue = "单据编号",
                    IsLocked = true,
                    Remark = "项目统一术语",
                    UpdatedAt = new DateTime(2026, 8, 31, 10, 30, 0)
                });

                var secondStore = new SnapshotStore(path);
                IList<DictionaryOverride> overrides = secondStore.LoadOverrides(scope);

                Assert.AreEqual(1, overrides.Count);
                Assert.AreEqual("订单编号", overrides[0].ManualValue);
                Assert.AreEqual("单据编号", overrides[0].OriginalAutomaticValue);
                Assert.IsTrue(overrides[0].IsLocked);
                Assert.AreEqual("项目统一术语", overrides[0].Remark);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证程序启动时能读取全部连接范围中最近保存的一份完整快照。</summary>
        [TestMethod]
        public void TryLoadMostRecent_RestoresLatestScope()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new SnapshotStore(path);
                store.ReplaceScope("old|DB", new SnapshotData
                {
                    RefreshedAt = new DateTime(2026, 8, 31, 9, 0, 0),
                    Tables = new List<TableMetadata>()
                });
                store.ReplaceScope("new|DB", new SnapshotData
                {
                    RefreshedAt = new DateTime(2026, 8, 31, 10, 0, 0),
                    Tables = new List<TableMetadata>()
                });

                string scope;
                SnapshotData restored;
                Assert.IsTrue(store.TryLoadMostRecent(out scope, out restored));
                Assert.AreEqual("new|DB", scope);
                Assert.AreEqual(new DateTime(2026, 8, 31, 10, 0, 0), restored.RefreshedAt);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证升级前的 XML 压缩快照可恢复并安全迁移为新 JSON 压缩格式。</summary>
        [TestMethod]
        public void TryLoadLatest_LegacyDataContractPayload_RestoresAndMigratesWithoutDataLoss()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "legacy|DB";
            try
            {
                var store = new SnapshotStore(path);
                SnapshotData legacySnapshot = CreateSnapshot(scope, "V_LEGACY_ORDER", "旧版视图");
                legacySnapshot.Tables[0].ObjectType = "VIEW";
                byte[] legacyPayload = SerializeLegacySnapshot(legacySnapshot);
                InsertSnapshotPayload(path, scope, legacySnapshot.RefreshedAt, legacyPayload);

                SnapshotData restored;
                Assert.IsTrue(store.TryLoadLatest(scope, out restored));
                Assert.AreEqual(0, restored.FormatVersion);
                Assert.AreEqual("VIEW", restored.Tables[0].ObjectType);
                Assert.AreEqual("旧版视图", restored.Tables[0].ChineseName.Value);

                byte[] migratedPayload = ReadSnapshotPayload(path, scope);
                CollectionAssert.AreNotEqual(legacyPayload, migratedPayload);

                var restartedStore = new SnapshotStore(path);
                SnapshotData restoredAfterMigration;
                Assert.IsTrue(restartedStore.TryLoadLatest(scope, out restoredAfterMigration));
                Assert.AreEqual("V_LEGACY_ORDER", restoredAfterMigration.Tables[0].ObjectName);
                Assert.AreEqual("旧版视图", restoredAfterMigration.Tables[0].ChineseName.Value);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证旧快照迁移写入失败时仍返回已恢复数据并保留原 payload。</summary>
        [TestMethod]
        public void TryLoadLatest_LegacyMigrationWriteFails_ReturnsSnapshotAndKeepsOriginalPayload()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "legacy-readonly|DB";
            FileAttributes originalAttributes = FileAttributes.Normal;
            bool attributesCaptured = false;
            try
            {
                var store = new SnapshotStore(path);
                SnapshotData legacySnapshot = CreateSnapshot(scope, "V_LEGACY_READONLY", "只读旧快照");
                legacySnapshot.Tables[0].ObjectType = "VIEW";
                byte[] legacyPayload = SerializeLegacySnapshot(legacySnapshot);
                InsertSnapshotPayload(path, scope, legacySnapshot.RefreshedAt, legacyPayload);
                originalAttributes = File.GetAttributes(path);
                attributesCaptured = true;
                File.SetAttributes(path, originalAttributes | FileAttributes.ReadOnly);
                var stopwatch = Stopwatch.StartNew();

                SnapshotData restored;
                Assert.IsTrue(store.TryLoadLatest(scope, out restored));

                stopwatch.Stop();
                Assert.AreEqual("V_LEGACY_READONLY", restored.Tables[0].ObjectName);
                Assert.AreEqual("只读旧快照", restored.Tables[0].ChineseName.Value);
                Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "只读迁移失败不应等待 SQLite 长超时。");

                File.SetAttributes(path, originalAttributes);
                byte[] payloadAfterFailure = ReadSnapshotPayload(path, scope);
                CollectionAssert.AreEqual(legacyPayload, payloadAfterFailure);
            }
            finally
            {
                SQLiteConnection.ClearAllPools();
                if (File.Exists(path))
                {
                    if (attributesCaptured)
                    {
                        File.SetAttributes(path, originalAttributes);
                    }

                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证远程新格式损坏时不会替换 SQLite 中已有的可用快照。</summary>
        [TestMethod]
        public void ReplaceScopeFromRemote_CorruptedContent_KeepsExistingSnapshot()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "p1|DB1";
            try
            {
                var store = new SnapshotStore(path);
                store.ReplaceScope(scope, CreateSnapshot(scope, "T_OLD", "旧结构"));
                var codec = new SnapshotCodec();
                byte[] corruptedContent = { 1, 2, 3, 4, 5 };

                Assert.ThrowsException<InvalidDataException>(
                    delegate
                    {
                        store.ReplaceScopeFromRemote(
                            scope,
                            corruptedContent,
                            codec.ComputeSha256(corruptedContent));
                    });

                SnapshotData restored;
                Assert.IsTrue(store.TryLoadLatest(scope, out restored));
                Assert.AreEqual("T_OLD", restored.Tables[0].ObjectName);
                Assert.AreEqual("旧结构", restored.Tables[0].ChineseName.Value);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260903 验证远端已校验压缩载荷按原始字节落盘，避免七万表快照被深拷贝后重新编码。</summary>
        [TestMethod]
        public void ReplaceScopeFromRemote_ValidContent_PreservesOriginalCompressedBytes()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-snapshot-raw-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "shared-scope";
            try
            {
                var codec = new SnapshotCodec();
                SnapshotData remoteSnapshot = CreateSnapshot(scope, "T_REMOTE", "远端结构");
                remoteSnapshot.FormatVersion = 1;
                remoteSnapshot.Revision = 1;
                byte[] remoteContent = codec.Encode(remoteSnapshot);
                // 不同发布器允许写入不同 GZip 头，原始字节必须保留才能避免无意义的全量重编码。
                remoteContent[4] = 9;
                remoteContent[5] = 8;
                var store = new SnapshotStore(path);

                store.ReplaceScopeFromRemote(scope, remoteContent, codec.ComputeSha256(remoteContent));

                CollectionAssert.AreEqual(remoteContent, ReadSnapshotPayload(path, scope));
            }
            finally
            {
                SQLiteConnection.ClearAllPools();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260828 创建最小结构快照供事务保留测试复用。</summary>
        private static SnapshotData CreateSnapshot(string scope, string objectName, string chineseName)
        {
            return new SnapshotData
            {
                RefreshedAt = DateTime.Now,
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ScopeKey = scope,
                        SchemaName = "dbo",
                        ObjectName = objectName,
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = chineseName, Status = ConfidenceStatus.DatabaseEvidence }
                    }
                }
            };
        }

        /// <summary>XMZADD 20260901 使用升级前的 XML 数据契约生成兼容性测试内容。</summary>
        private static byte[] SerializeLegacySnapshot(SnapshotData snapshot)
        {
            var serializer = new DataContractSerializer(typeof(SnapshotData), null, Int32.MaxValue, false, false, null);
            using (var stream = new MemoryStream())
            {
                using (var compressedStream = new GZipStream(stream, CompressionMode.Compress, true))
                {
                    serializer.WriteObject(compressedStream, snapshot);
                }

                return stream.ToArray();
            }
        }

        /// <summary>XMZADD 20260901 直接写入旧版 payload 以模拟真实升级现场。</summary>
        private static void InsertSnapshotPayload(string path, string scope, DateTime refreshedAt, byte[] payload)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            using (SQLiteCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"INSERT OR REPLACE INTO SnapshotContents (ScopeKey, RefreshedAt, Payload)
VALUES (@ScopeKey, @RefreshedAt, @Payload);";
                command.Parameters.AddWithValue("@ScopeKey", scope);
                command.Parameters.AddWithValue("@RefreshedAt", refreshedAt.ToString("O"));
                command.Parameters.Add("@Payload", System.Data.DbType.Binary).Value = payload;
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260901 读取迁移后的 payload 以确认升级未停留在旧格式。</summary>
        private static byte[] ReadSnapshotPayload(string path, string scope)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            using (SQLiteCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = "SELECT Payload FROM SnapshotContents WHERE ScopeKey = @ScopeKey;";
                command.Parameters.AddWithValue("@ScopeKey", scope);
                return (byte[])command.ExecuteScalar();
            }
        }

        /// <summary>XMZADD 20260908 计算测试快照文件哈希以识别只读构造期间的隐式 SQLite 写入。</summary>
        private static string ComputeFileSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (SHA256 algorithm = SHA256.Create())
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }
    }
}
