using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Threading;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 保存按连接数据库隔离的 EOS 结构快照并提供快速搜索。</summary>
    public sealed class SnapshotStore
    {
        private readonly string _databasePath;
        private readonly SnapshotCodec _snapshotCodec;

        public SnapshotStore(string databasePath)
        {
            _databasePath = databasePath;
            _snapshotCodec = new SnapshotCodec();
            EnsureSchema();
        }

        /// <summary>XMZADD 20260831 以事务替换指定连接数据库作用域的完整结构快照。</summary>
        public void ReplaceScope(string scopeKey, SnapshotData snapshot)
        {
            ReplaceScope(scopeKey, snapshot, CancellationToken.None);
        }

        /// <summary>XMZADD 20260831 在可取消事务中替换完整快照，取消时回滚并保留上一次成功保存的内容。</summary>
        public void ReplaceScope(string scopeKey, SnapshotData snapshot, CancellationToken cancellationToken)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] snapshotPayload = _snapshotCodec.Encode(snapshot);
            ReplaceScopePayload(scopeKey, snapshot, snapshotPayload, cancellationToken);
        }

        /// <summary>XMZADD 20260903 校验远端规范快照并保留原始压缩字节，避免超大快照重新编码产生完整对象深拷贝。</summary>
        public void ReplaceScopeFromRemote(string scopeKey, byte[] compressedContent, string expectedSha256)
        {
            SnapshotData snapshot = _snapshotCodec.DecodeAndValidate(compressedContent, expectedSha256);
            ReplaceScopePayload(scopeKey, snapshot, compressedContent, CancellationToken.None);
        }

        /// <summary>XMZADD 20260903 校验远端清单版本后原样持久化压缩载荷，并返回已解码对象供界面直接复用。</summary>
        public SnapshotData ReplaceScopeFromRemote(
            string scopeKey,
            byte[] compressedContent,
            string expectedSha256,
            int expectedFormatVersion,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotData snapshot = _snapshotCodec.DecodeAndValidate(compressedContent, expectedSha256);
            if (snapshot.FormatVersion != expectedFormatVersion || snapshot.Revision != expectedRevision)
            {
                throw new InvalidDataException("远程规范快照与清单修订不一致。");
            }
            ReplaceScopePayload(scopeKey, snapshot, compressedContent, cancellationToken);
            return snapshot;
        }

        /// <summary>XMZADD 20260903 持久化协调器已完整校验和解码的远端快照，避免再次解码或重新编码超大对象图。</summary>
        internal void ReplaceValidatedScopeFromRemote(
            string scopeKey,
            SnapshotData snapshot,
            byte[] compressedContent,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (compressedContent == null || compressedContent.Length == 0 ||
                !string.Equals(_snapshotCodec.ComputeSha256(compressedContent), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("远程规范快照哈希校验失败。");
            }
            ReplaceScopePayload(scopeKey, snapshot, compressedContent, cancellationToken);
        }

        /// <summary>XMZADD 20260903 在同一事务中写入摘要行和指定压缩载荷，使本地生成与远端原始快照共用持久化边界。</summary>
        private void ReplaceScopePayload(
            string scopeKey,
            SnapshotData snapshot,
            byte[] snapshotPayload,
            CancellationToken cancellationToken)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (SQLiteCommand delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    // 摘要和完整结构必须同时替换，避免检索结果与详情快照来自不同批次。
                    delete.CommandText = @"DELETE FROM SnapshotTables WHERE ScopeKey = @ScopeKey;
DELETE FROM SnapshotContents WHERE ScopeKey = @ScopeKey;";
                    delete.Parameters.AddWithValue("@ScopeKey", scopeKey ?? string.Empty);
                    delete.ExecuteNonQuery();
                }

                for (int i = 0; i < snapshot.Tables.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TableMetadata table = snapshot.Tables[i];
                    using (SQLiteCommand insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = @"INSERT INTO SnapshotTables
(ScopeKey, SchemaName, ObjectName, ObjectType, ChineseName, ConfidenceStatus, RefreshedAt)
VALUES (@ScopeKey, @SchemaName, @ObjectName, @ObjectType, @ChineseName, @ConfidenceStatus, @RefreshedAt);";
                        insert.Parameters.AddWithValue("@ScopeKey", scopeKey ?? string.Empty);
                        insert.Parameters.AddWithValue("@SchemaName", table.SchemaName ?? string.Empty);
                        insert.Parameters.AddWithValue("@ObjectName", table.ObjectName ?? string.Empty);
                        insert.Parameters.AddWithValue("@ObjectType", table.ObjectType ?? string.Empty);
                        insert.Parameters.AddWithValue("@ChineseName", table.ChineseName == null ? string.Empty : table.ChineseName.Value ?? string.Empty);
                        insert.Parameters.AddWithValue("@ConfidenceStatus", table.ChineseName == null ? (int)ConfidenceStatus.PendingConfirmation : (int)table.ChineseName.Status);
                        insert.Parameters.AddWithValue("@RefreshedAt", snapshot.RefreshedAt.ToString("O", CultureInfo.InvariantCulture));
                        insert.ExecuteNonQuery();
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                using (SQLiteCommand insertPayload = connection.CreateCommand())
                {
                    insertPayload.Transaction = transaction;
                    insertPayload.CommandText = @"INSERT INTO SnapshotContents (ScopeKey, RefreshedAt, Payload)
VALUES (@ScopeKey, @RefreshedAt, @Payload);";
                    insertPayload.Parameters.AddWithValue("@ScopeKey", scopeKey ?? string.Empty);
                    insertPayload.Parameters.AddWithValue("@RefreshedAt", snapshot.RefreshedAt.ToString("O", CultureInfo.InvariantCulture));
                    insertPayload.Parameters.Add("@Payload", System.Data.DbType.Binary).Value = snapshotPayload;
                    insertPayload.ExecuteNonQuery();
                }

                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
        }

        /// <summary>XMZADD 20260831 读取指定作用域最近一次成功保存的完整本地结构快照。</summary>
        public bool TryLoadLatest(string scopeKey, out SnapshotData snapshot)
        {
            snapshot = null;
            byte[] payload;
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT Payload FROM SnapshotContents
WHERE ScopeKey = @ScopeKey;";
                command.Parameters.AddWithValue("@ScopeKey", scopeKey ?? string.Empty);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    payload = (byte[])reader["Payload"];
                }
            }

            bool legacyFormat;
            // 损坏的快照必须显式抛出异常，防止程序把不可用内容误当成没有本地数据。
            snapshot = DeserializeSnapshot(payload, out legacyFormat);
            if (legacyFormat)
            {
                TryMigrateLegacyPayload(scopeKey ?? string.Empty, payload, snapshot);
            }

            return true;
        }

        /// <summary>XMZADD 20260903 不反序列化字段对象即可读取表数和完整载荷哈希，供超大字典同步选择低内存路径。</summary>
        public bool TryGetScopeSummary(string scopeKey, out int tableCount, out string payloadSha256)
        {
            tableCount = 0;
            payloadSha256 = string.Empty;
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT Payload,
(SELECT COUNT(*) FROM SnapshotTables WHERE ScopeKey = @ScopeKey) AS TableCount
FROM SnapshotContents
WHERE ScopeKey = @ScopeKey
LIMIT 1;";
                command.Parameters.AddWithValue("@ScopeKey", scopeKey ?? string.Empty);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        return false;
                    }
                    byte[] payload = (byte[])reader["Payload"];
                    tableCount = Convert.ToInt32(reader["TableCount"], CultureInfo.InvariantCulture);
                    payloadSha256 = _snapshotCodec.ComputeSha256(payload);
                    return true;
                }
            }
        }

        /// <summary>XMZADD 20260831 读取所有作用域中最近一次成功保存的完整本地结构快照。</summary>
        public bool TryLoadMostRecent(out string scopeKey, out SnapshotData snapshot)
        {
            scopeKey = string.Empty;
            snapshot = null;
            byte[] payload;
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ScopeKey, Payload FROM SnapshotContents
ORDER BY RefreshedAt DESC LIMIT 1;";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    // 启动恢复必须使用同一完整快照对应的作用域，避免混入其他连接的结构。
                    scopeKey = reader["ScopeKey"].ToString();
                    payload = (byte[])reader["Payload"];
                }
            }

            bool legacyFormat;
            snapshot = DeserializeSnapshot(payload, out legacyFormat);
            if (legacyFormat)
            {
                TryMigrateLegacyPayload(scopeKey, payload, snapshot);
            }

            return true;
        }

        /// <summary>XMZADD 20260831 按当前连接数据库作用域搜索中文或英文表名。</summary>
        /// <summary>XMZADD 20260831 通过快照存储入口保存人工字典维护项，兼容既有调用方且仍只写入本地 SQLite。</summary>
        public void SaveOverride(DictionaryOverride item)
        {
            new LocalDictionaryStore(_databasePath).SaveOverride(item);
        }

        /// <summary>XMZADD 20260831 通过快照存储入口读取人工字典维护项，供恢复完整快照后应用最高优先级结论。</summary>
        public IList<DictionaryOverride> LoadOverrides(string scopeKey)
        {
            return new LocalDictionaryStore(_databasePath).LoadOverrides(scopeKey);
        }

        public IList<TableMetadata> SearchTables(string scopeKey, string text, bool exact)
        {
            var result = new List<TableMetadata>();
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = exact
                    ? @"SELECT SchemaName, ObjectName, ObjectType, ChineseName, ConfidenceStatus
FROM SnapshotTables WHERE ScopeKey = @ScopeKey
AND (ChineseName = @Text OR ObjectName = @Text OR SchemaName = @Text)
ORDER BY SchemaName, ObjectName;"
                    : @"SELECT SchemaName, ObjectName, ObjectType, ChineseName, ConfidenceStatus
FROM SnapshotTables WHERE ScopeKey = @ScopeKey
AND (ChineseName LIKE @LikeText OR ObjectName LIKE @LikeText OR SchemaName LIKE @LikeText)
ORDER BY SchemaName, ObjectName;";
                command.Parameters.AddWithValue("@ScopeKey", scopeKey ?? string.Empty);
                command.Parameters.AddWithValue("@Text", text ?? string.Empty);
                command.Parameters.AddWithValue("@LikeText", "%" + (text ?? string.Empty) + "%");
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new TableMetadata
                        {
                            ScopeKey = scopeKey,
                            SchemaName = reader["SchemaName"].ToString(),
                            ObjectName = reader["ObjectName"].ToString(),
                            ObjectType = reader["ObjectType"].ToString(),
                            ChineseName = new MetadataValue
                            {
                                Value = reader["ChineseName"].ToString(),
                                Status = (ConfidenceStatus)Convert.ToInt32(reader["ConfidenceStatus"])
                            }
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>XMZADD 20260831 创建完整快照表，所有内容仅保存在应用本地 SQLite。</summary>
        private void EnsureSchema()
        {
            string directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS SnapshotTables (
    ScopeKey TEXT NOT NULL,
    SchemaName TEXT NOT NULL,
    ObjectName TEXT NOT NULL,
    ObjectType TEXT NOT NULL,
    ChineseName TEXT,
    ConfidenceStatus INTEGER NOT NULL,
    RefreshedAt TEXT NOT NULL,
    PRIMARY KEY (ScopeKey, SchemaName, ObjectName)
);
CREATE INDEX IF NOT EXISTS IX_SnapshotTables_Search
ON SnapshotTables (ScopeKey, ChineseName, ObjectName);
CREATE TABLE IF NOT EXISTS SnapshotContents (
    ScopeKey TEXT NOT NULL PRIMARY KEY,
    RefreshedAt TEXT NOT NULL,
    Payload BLOB NOT NULL
);";
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260901 优先读取新 JSON 格式并回退旧 XML 格式以恢复升级前数据。</summary>
        private SnapshotData DeserializeSnapshot(byte[] payload, out bool legacyFormat)
        {
            try
            {
                SnapshotData snapshot = _snapshotCodec.DecodeWithoutValidation(payload);
                legacyFormat = false;
                NormalizeLocalSnapshot(snapshot);
                return snapshot;
            }
            catch (InvalidDataException)
            {
                SnapshotData snapshot = DeserializeLegacySnapshot(payload);
                legacyFormat = true;
                NormalizeLocalSnapshot(snapshot);
                return snapshot;
            }
        }

        /// <summary>XMZADD 20260901 解压升级前 XML 数据契约快照以保障历史本地数据可恢复。</summary>
        private static SnapshotData DeserializeLegacySnapshot(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                throw new SerializationException("本地完整快照内容为空。");
            }

            var serializer = new DataContractSerializer(typeof(SnapshotData), null, Int32.MaxValue, false, false, null);
            using (var stream = new MemoryStream(payload, false))
            using (var compressedStream = new GZipStream(stream, CompressionMode.Decompress))
            {
                var snapshot = serializer.ReadObject(compressedStream) as SnapshotData;
                if (snapshot == null)
                {
                    throw new SerializationException("本地完整快照无法还原为结构数据。");
                }

                return snapshot;
            }
        }

        /// <summary>XMZADD 20260901 补齐旧数据契约中尚不存在的集合以维持升级后的调用约定。</summary>
        private static void NormalizeLocalSnapshot(SnapshotData snapshot)
        {
            if (snapshot.Tables == null)
            {
                snapshot.Tables = new List<TableMetadata>();
            }

            if (snapshot.ExcludedObjects == null)
            {
                snapshot.ExcludedObjects = new List<ExcludedObjectRecord>();
            }

            if (snapshot.Abbreviations == null)
            {
                snapshot.Abbreviations = new List<AbbreviationEntry>();
            }

            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }

                if (table.Fields == null)
                {
                    table.Fields = new List<FieldMetadata>();
                }

                if (table.Relations == null)
                {
                    table.Relations = new List<RelationMetadata>();
                }

                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field != null && field.EnumItems == null)
                    {
                        field.EnumItems = new List<EnumItemMetadata>();
                    }
                }
            }

            for (int abbreviationIndex = 0; abbreviationIndex < snapshot.Abbreviations.Count; abbreviationIndex++)
            {
                AbbreviationEntry abbreviation = snapshot.Abbreviations[abbreviationIndex];
                if (abbreviation != null && abbreviation.Evidence == null)
                {
                    abbreviation.Evidence = new List<AbbreviationEvidence>();
                }
            }
        }

        /// <summary>XMZADD 20260901 在条件更新事务中迁移旧 payload，失败时保留已恢复的数据和原内容。</summary>
        private void TryMigrateLegacyPayload(string scopeKey, byte[] oldPayload, SnapshotData snapshot)
        {
            try
            {
                byte[] migratedPayload = _snapshotCodec.Encode(snapshot);
                using (SQLiteConnection connection = OpenConnection())
                using (SQLiteTransaction transaction = connection.BeginTransaction())
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    // 条件更新避免迁移过程覆盖同一作用域刚完成的较新同步结果。
                    command.CommandText = @"UPDATE SnapshotContents SET Payload = @Payload
WHERE ScopeKey = @ScopeKey AND Payload = @OldPayload;";
                    command.Parameters.Add("@Payload", System.Data.DbType.Binary).Value = migratedPayload;
                    command.Parameters.AddWithValue("@ScopeKey", scopeKey);
                    command.Parameters.Add("@OldPayload", System.Data.DbType.Binary).Value = oldPayload;
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
            catch (SQLiteException)
            {
                // 历史数据已成功恢复时，迁移失败不能阻断本次启动读取。
            }
            catch (IOException)
            {
                // 历史数据已成功恢复时，迁移失败不能阻断本次启动读取。
            }
            catch (SerializationException)
            {
                // 历史数据已成功恢复时，迁移失败不能阻断本次启动读取。
            }
            catch (InvalidOperationException)
            {
                // 历史数据已成功恢复时，迁移失败不能阻断本次启动读取。
            }
        }

        /// <summary>XMZADD 20260831 打开应用专用本地 SQLite 数据库连接。</summary>
        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection("Data Source=" + _databasePath + ";Version=3;");
            connection.Open();
            return connection;
        }
    }
}
