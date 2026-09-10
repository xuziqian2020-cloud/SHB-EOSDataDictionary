using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260831 持久化仅属于当前 Windows 用户的 AI 配置和人工维护字典内容，确保不会向 EOS 数据库写入任何数据。</summary>
    public sealed class LocalDictionaryStore
    {
        private static readonly byte[] ApiKeyEntropy = Encoding.UTF8.GetBytes("SHB.EosDataDictionary.AiProvider.ApiKey.v1");
        private static readonly object SchemaSync = new object();
        private static readonly string[] PendingOperationColumns =
        {
            "OperationId", "PayloadJson", "Status", "AttemptCount", "NextAttemptAtUtc",
            "GitHubIssueNumber", "LastError", "CreatedAtUtc", "UpdatedAtUtc"
        };
        private static readonly string[] SyncStateColumns =
        {
            "StateKey", "Revision", "ManifestHash", "LocalPayloadHash", "LastSyncAtUtc", "LastSuccessfulOperationId"
        };
        private readonly string _databasePath;

        /// <summary>XMZADD 20260831 初始化本机字典存储位置并创建 AI 配置与人工覆盖所需的本地表结构。</summary>
        public LocalDictionaryStore(string databasePath = null)
        {
            _databasePath = string.IsNullOrWhiteSpace(databasePath) ? AppPathService.GetLocalDatabasePath() : databasePath;
            lock (SchemaSync)
            {
                // 同一进程中的窗口和后台服务必须串行升级本地结构，避免并发 DDL 锁住配置库。
                EnsureSchema();
            }
        }

        /// <summary>XMZADD 20260831 保存 AI 服务商配置，并在存在配置时始终维护唯一的默认服务商。</summary>
        public void SaveAiProvider(AiProviderConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }
            if (string.IsNullOrWhiteSpace(configuration.Id))
            {
                throw new ArgumentException("AI 服务商标识不能为空。", "configuration");
            }

            DateTime updatedAt = configuration.UpdatedAt == DateTime.MinValue ? DateTime.Now : configuration.UpdatedAt;
            byte[] protectedApiKey = ProtectApiKey(configuration.ApiKey);
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                if (protectedApiKey == null)
                {
                    // 配置界面留空表示沿用原密钥，避免查看或编辑其他参数时意外清除已保存凭据。
                    protectedApiKey = FindEncryptedApiKey(connection, transaction, configuration.Id);
                }
                string existingDefaultId = FindDefaultProviderId(connection, transaction);
                bool shouldBeDefault = configuration.IsDefault
                    || string.IsNullOrWhiteSpace(existingDefaultId)
                    || string.Equals(existingDefaultId, configuration.Id, StringComparison.Ordinal);

                if (shouldBeDefault)
                {
                    // 默认配置决定所有自动推测的调用目标，保存新默认项时必须撤销旧默认项。
                    using (SQLiteCommand clearDefault = connection.CreateCommand())
                    {
                        clearDefault.Transaction = transaction;
                        clearDefault.CommandText = "UPDATE LocalAiProviders SET IsDefault = 0 WHERE ProviderId <> @ProviderId;";
                        clearDefault.Parameters.AddWithValue("@ProviderId", configuration.Id);
                        clearDefault.ExecuteNonQuery();
                    }
                }
                else
                {
                    // 修复异常中断可能遗留的多个默认项，避免自动推测调用目标不确定。
                    using (SQLiteCommand clearDuplicateDefault = connection.CreateCommand())
                    {
                        clearDuplicateDefault.Transaction = transaction;
                        clearDuplicateDefault.CommandText = "UPDATE LocalAiProviders SET IsDefault = 0 WHERE ProviderId <> @ProviderId AND IsDefault = 1;";
                        clearDuplicateDefault.Parameters.AddWithValue("@ProviderId", existingDefaultId);
                        clearDuplicateDefault.ExecuteNonQuery();
                    }
                }

                using (SQLiteCommand save = connection.CreateCommand())
                {
                    save.Transaction = transaction;
                    save.CommandText = @"INSERT OR REPLACE INTO LocalAiProviders
(ProviderId, Name, Protocol, Endpoint, Model, EncryptedApiKey, IsDefault, IsEnabled,
 TimeoutSeconds, MaxConcurrency, BatchSize, ConfigurationVersion, UpdatedAt)
VALUES
(@ProviderId, @Name, @Protocol, @Endpoint, @Model, @EncryptedApiKey, @IsDefault, @IsEnabled,
 @TimeoutSeconds, @MaxConcurrency, @BatchSize, @ConfigurationVersion, @UpdatedAt);";
                    save.Parameters.AddWithValue("@ProviderId", configuration.Id);
                    save.Parameters.AddWithValue("@Name", configuration.Name ?? string.Empty);
                    save.Parameters.AddWithValue("@Protocol", (int)configuration.Protocol);
                    save.Parameters.AddWithValue("@Endpoint", configuration.Endpoint ?? string.Empty);
                    save.Parameters.AddWithValue("@Model", configuration.Model ?? string.Empty);
                    SQLiteParameter apiKeyParameter = save.Parameters.Add("@EncryptedApiKey", DbType.Binary);
                    apiKeyParameter.Value = protectedApiKey == null ? (object)DBNull.Value : protectedApiKey;
                    save.Parameters.AddWithValue("@IsDefault", shouldBeDefault ? 1 : 0);
                    save.Parameters.AddWithValue("@IsEnabled", configuration.IsEnabled ? 1 : 0);
                    save.Parameters.AddWithValue("@TimeoutSeconds", configuration.TimeoutSeconds);
                    save.Parameters.AddWithValue("@MaxConcurrency", configuration.MaxConcurrency);
                    // 持久化值不得超过推测服务硬上限，避免旧配置在重启后持续触发整批拒绝。
                    int batchSize = configuration.BatchSize <= 0
                        ? 10
                        : Math.Min(configuration.BatchSize, AiInferenceService.MaximumBatchTargetCount);
                    save.Parameters.AddWithValue("@BatchSize", batchSize);
                    save.Parameters.AddWithValue("@ConfigurationVersion", configuration.ConfigurationVersion ?? string.Empty);
                    save.Parameters.AddWithValue("@UpdatedAt", updatedAt.ToString("O", CultureInfo.InvariantCulture));
                    save.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        /// <summary>XMZADD 20260831 读取本机保存的全部 AI 服务商配置，并在当前用户上下文中安全恢复 API 密钥。</summary>
        public IList<AiProviderConfiguration> LoadAiProviders()
        {
            var result = new List<AiProviderConfiguration>();
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ProviderId, Name, Protocol, Endpoint, Model, EncryptedApiKey,
IsDefault, IsEnabled, TimeoutSeconds, MaxConcurrency, BatchSize, ConfigurationVersion, UpdatedAt
FROM LocalAiProviders
ORDER BY IsDefault DESC, Name, ProviderId;";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(ReadAiProvider(reader));
                    }
                }
            }

            return result;
        }

        /// <summary>XMZADD 20260831 获取唯一默认 AI 服务商，供后台自动推测统一使用已确认的调用配置。</summary>
        public AiProviderConfiguration GetDefaultAiProvider()
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ProviderId, Name, Protocol, Endpoint, Model, EncryptedApiKey,
IsDefault, IsEnabled, TimeoutSeconds, MaxConcurrency, BatchSize, ConfigurationVersion, UpdatedAt
FROM LocalAiProviders
WHERE IsDefault = 1
ORDER BY UpdatedAt DESC, ProviderId
LIMIT 1;";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    return ReadAiProvider(reader);
                }
            }
        }

        /// <summary>XMZADD 20260831 删除本机 AI 服务商配置，并在删除默认项后为剩余配置指定稳定且唯一的新默认项。</summary>
        public void DeleteAiProvider(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                throw new ArgumentException("AI 服务商标识不能为空。", "providerId");
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                string currentDefaultId = FindDefaultProviderId(connection, transaction);
                bool deletingDefault = string.Equals(currentDefaultId, providerId, StringComparison.Ordinal);
                using (SQLiteCommand delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM LocalAiProviders WHERE ProviderId = @ProviderId;";
                    delete.Parameters.AddWithValue("@ProviderId", providerId);
                    delete.ExecuteNonQuery();
                }

                if (deletingDefault)
                {
                    string replacementProviderId = FindFirstProviderId(connection, transaction);
                    if (!string.IsNullOrWhiteSpace(replacementProviderId))
                    {
                        // 删除默认项后必须立即明确新的调用目标，避免自动推测因配置歧义而停止。
                        using (SQLiteCommand clearDefault = connection.CreateCommand())
                        {
                            clearDefault.Transaction = transaction;
                            clearDefault.CommandText = "UPDATE LocalAiProviders SET IsDefault = 0;";
                            clearDefault.ExecuteNonQuery();
                        }
                        using (SQLiteCommand promote = connection.CreateCommand())
                        {
                            promote.Transaction = transaction;
                            promote.CommandText = "UPDATE LocalAiProviders SET IsDefault = 1 WHERE ProviderId = @ProviderId;";
                            promote.Parameters.AddWithValue("@ProviderId", replacementProviderId);
                            promote.ExecuteNonQuery();
                        }
                    }
                }

                transaction.Commit();
            }
        }

        /// <summary>XMZADD 20260831 按表字段键批量保存 AI 推测，使小批次结果不再触发完整结构快照重写。</summary>
        public void SaveAiExplanations(IList<AiExplanationResult> explanations)
        {
            if (explanations == null || explanations.Count == 0)
            {
                return;
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                for (int i = 0; i < explanations.Count; i++)
                {
                    AiExplanationResult item = explanations[i];
                    if (item == null || string.IsNullOrWhiteSpace(item.ObjectName))
                    {
                        continue;
                    }
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"INSERT OR REPLACE INTO AiExplanations
(ScopeKey, ObjectName, FieldName, ChineseName, BusinessMeaning, Usage, ConfidenceScore,
 ProviderId, ProviderName, Model, ConfigurationVersion, GeneratedAt)
VALUES
(@ScopeKey, @ObjectName, @FieldName, @ChineseName, @BusinessMeaning, @Usage, @ConfidenceScore,
 @ProviderId, @ProviderName, @Model, @ConfigurationVersion, @GeneratedAt);";
                        command.Parameters.AddWithValue("@ScopeKey", NormalizeKey(item.ScopeKey));
                        command.Parameters.AddWithValue("@ObjectName", NormalizeKey(item.ObjectName));
                        command.Parameters.AddWithValue("@FieldName", NormalizeKey(item.FieldName));
                        command.Parameters.AddWithValue("@ChineseName", item.ChineseName ?? string.Empty);
                        command.Parameters.AddWithValue("@BusinessMeaning", item.BusinessMeaning ?? string.Empty);
                        command.Parameters.AddWithValue("@Usage", item.Usage ?? string.Empty);
                        command.Parameters.AddWithValue("@ConfidenceScore", item.ConfidenceScore);
                        command.Parameters.AddWithValue("@ProviderId", item.ProviderId ?? string.Empty);
                        command.Parameters.AddWithValue("@ProviderName", item.ProviderName ?? string.Empty);
                        command.Parameters.AddWithValue("@Model", item.Model ?? string.Empty);
                        command.Parameters.AddWithValue("@ConfigurationVersion", item.ConfigurationVersion ?? string.Empty);
                        DateTime generatedAt = item.GeneratedAt == DateTime.MinValue ? DateTime.Now : item.GeneratedAt;
                        command.Parameters.AddWithValue("@GeneratedAt", generatedAt.ToString("O", CultureInfo.InvariantCulture));
                        command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        /// <summary>XMZADD 20260831 读取指定数据库作用域已完成的 AI 解释，供恢复和增量刷新时重新合并。</summary>
        public IList<AiExplanationResult> LoadAiExplanations(string scopeKey)
        {
            var result = new List<AiExplanationResult>();
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ObjectName, FieldName, ChineseName, BusinessMeaning, Usage,
ConfidenceScore, ProviderId, ProviderName, Model, ConfigurationVersion, GeneratedAt
FROM AiExplanations
WHERE ScopeKey = @ScopeKey
ORDER BY ObjectName, FieldName;";
                command.Parameters.AddWithValue("@ScopeKey", NormalizeKey(scopeKey));
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new AiExplanationResult
                        {
                            ScopeKey = NormalizeKey(scopeKey),
                            ObjectName = reader["ObjectName"].ToString(),
                            FieldName = reader["FieldName"].ToString(),
                            ChineseName = reader["ChineseName"].ToString(),
                            BusinessMeaning = reader["BusinessMeaning"].ToString(),
                            Usage = reader["Usage"].ToString(),
                            ConfidenceScore = Convert.ToInt32(reader["ConfidenceScore"], CultureInfo.InvariantCulture),
                            ProviderId = reader["ProviderId"].ToString(),
                            ProviderName = reader["ProviderName"].ToString(),
                            Model = reader["Model"].ToString(),
                            ConfigurationVersion = reader["ConfigurationVersion"].ToString(),
                            GeneratedAt = DateTime.Parse(reader["GeneratedAt"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                        });
                    }
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260831 保存人工维护的字典属性，以完整业务定位键覆盖后续任何自动生成内容。</summary>
        public void SaveOverride(DictionaryOverride item)
        {
            if (item == null)
            {
                throw new ArgumentNullException("item");
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                SaveOverride(command, item);
            }
        }

        /// <summary>XMZADD 20260901 在单一本地事务中保存一次编辑的全部人工覆盖和一个待上传批次。</summary>
        public void SaveOverridesWithPendingOperation(IList<DictionaryOverride> overrides, DictionaryChangeBatch batch)
        {
            ValidatePendingBatch(overrides, batch);
            string payloadJson = DictionaryJsonSerializer.SerializeBatch(batch);
            DateTime nowUtc = DateTime.UtcNow;

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                string existingPayload = FindPendingPayload(connection, transaction, batch.BatchId);
                if (existingPayload != null && !string.Equals(existingPayload, payloadJson, StringComparison.Ordinal))
                {
                    // 本地批次键承担队列幂等职责，同键不同内容必须显式拒绝以免远程重放语义漂移。
                    throw new InvalidOperationException("相同批次编号已保存为不同载荷。");
                }

                for (int i = 0; i < overrides.Count; i++)
                {
                    using (SQLiteCommand overrideCommand = connection.CreateCommand())
                    {
                        overrideCommand.Transaction = transaction;
                        SaveOverride(overrideCommand, overrides[i]);
                    }
                }

                if (existingPayload == null)
                {
                    using (SQLiteCommand pendingCommand = connection.CreateCommand())
                    {
                        pendingCommand.Transaction = transaction;
                        pendingCommand.CommandText = @"INSERT INTO PendingDictionaryOperations
(OperationId, PayloadJson, Status, AttemptCount, NextAttemptAtUtc, GitHubIssueNumber, LastError, CreatedAtUtc, UpdatedAtUtc)
VALUES
(@OperationId, @PayloadJson, @Status, 0, NULL, NULL, '', @CreatedAtUtc, @UpdatedAtUtc);";
                        pendingCommand.Parameters.AddWithValue("@OperationId", batch.BatchId);
                        pendingCommand.Parameters.AddWithValue("@PayloadJson", payloadJson);
                        pendingCommand.Parameters.AddWithValue("@Status", (int)PendingOperationStatus.Pending);
                        pendingCommand.Parameters.AddWithValue("@CreatedAtUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture));
                        pendingCommand.Parameters.AddWithValue("@UpdatedAtUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture));
                        pendingCommand.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
        }

        /// <summary>XMZADD 20260901 读取全部本地待上传记录并恢复可重试批次和调度元数据。</summary>
        public IList<PendingDictionaryOperation> LoadPendingOperations()
        {
            return LoadPendingOperations(true);
        }

        /// <summary>XMZADD 20260903 只读取待上传、待生效或可恢复失败记录，避免周期任务反序列化永久完成历史。</summary>
        public IList<PendingDictionaryOperation> LoadActivePendingOperations()
        {
            return LoadPendingOperations(false);
        }

        /// <summary>XMZADD 20260903 按业务是否需要全部历史读取本地队列，统一保持反序列化与排序规则。</summary>
        private IList<PendingDictionaryOperation> LoadPendingOperations(bool includeHistory)
        {
            var result = new List<PendingDictionaryOperation>();
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT OperationId, PayloadJson, Status, AttemptCount,
NextAttemptAtUtc, GitHubIssueNumber, LastError, CreatedAtUtc, UpdatedAtUtc
FROM PendingDictionaryOperations
" + (includeHistory
                        ? string.Empty
                        : "WHERE Status IN (@PendingStatus, @SubmittedStatus, @FailedStatus)\n") +
"ORDER BY CreatedAtUtc, OperationId;";
                if (!includeHistory)
                {
                    command.Parameters.AddWithValue("@PendingStatus", (int)PendingOperationStatus.Pending);
                    command.Parameters.AddWithValue("@SubmittedStatus", (int)PendingOperationStatus.Submitted);
                    command.Parameters.AddWithValue("@FailedStatus", (int)PendingOperationStatus.Failed);
                }
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new PendingDictionaryOperation
                        {
                            OperationId = reader["OperationId"].ToString(),
                            Batch = DictionaryJsonSerializer.DeserializeBatch(reader["PayloadJson"].ToString()),
                            Status = (PendingOperationStatus)Convert.ToInt32(reader["Status"], CultureInfo.InvariantCulture),
                            AttemptCount = Convert.ToInt32(reader["AttemptCount"], CultureInfo.InvariantCulture),
                            NextAttemptAtUtc = ReadNullableUtcDateTime(reader["NextAttemptAtUtc"]),
                            GitHubIssueNumber = reader["GitHubIssueNumber"] == DBNull.Value
                                ? (int?)null
                                : Convert.ToInt32(reader["GitHubIssueNumber"], CultureInfo.InvariantCulture),
                            LastError = reader["LastError"] == DBNull.Value ? string.Empty : reader["LastError"].ToString(),
                            CreatedAtUtc = ReadUtcDateTime(reader["CreatedAtUtc"]),
                            UpdatedAtUtc = ReadUtcDateTime(reader["UpdatedAtUtc"])
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>XMZADD 20260901 将本地批次标记为已提交并保存远程问题编号，供后续确认合并结果。</summary>
        public void MarkOperationSubmitted(string operationId, int issueNumber)
        {
            UpdateOperationStatus(
                operationId,
                PendingOperationStatus.Submitted,
                0,
                null,
                issueNumber,
                true,
                string.Empty);
        }

        /// <summary>XMZADD 20260901 将远程流程已确认完成的批次保留为本地审计记录。</summary>
        public void MarkOperationCompleted(string operationId)
        {
            UpdateOperationStatus(
                operationId,
                PendingOperationStatus.Completed,
                0,
                null,
                null,
                false,
                string.Empty);
        }

        /// <summary>XMZADD 20260909 将远端明确拒绝的批次保留为终态审计记录，避免继续显示为待生效。</summary>
        public void MarkOperationRejected(string operationId, string reason)
        {
            UpdateOperationStatus(
                operationId,
                PendingOperationStatus.Rejected,
                0,
                null,
                null,
                false,
                reason);
        }

        /// <summary>XMZADD 20260909 将已被后续规范值取代的批次保留为终态审计记录，防止旧值重新提交。</summary>
        public void MarkOperationSuperseded(string operationId, string reason)
        {
            UpdateOperationStatus(
                operationId,
                PendingOperationStatus.Superseded,
                0,
                null,
                null,
                false,
                reason);
        }

        /// <summary>XMZADD 20260901 记录瞬时上传失败并保持待处理状态，供调度器按 UTC 时间再次尝试。</summary>
        public void RecordOperationRetry(string operationId, DateTime nextAttemptAtUtc, string lastError)
        {
            UpdateOperationStatus(
                operationId,
                PendingOperationStatus.Pending,
                1,
                nextAttemptAtUtc.ToUniversalTime(),
                null,
                false,
                lastError);
        }

        /// <summary>XMZADD 20260901 标记不可自动恢复的永久失败并保留本地编辑和受限长度诊断。</summary>
        public void MarkOperationFailed(string operationId, string lastError)
        {
            UpdateOperationStatus(
                operationId,
                PendingOperationStatus.Failed,
                0,
                null,
                null,
                false,
                lastError);
        }

        /// <summary>XMZADD 20260901 保存共享字典修订游标，使下一次同步可从最后成功位置继续。</summary>
        public void SaveSyncState(DictionarySyncState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException("state");
            }
            if (string.IsNullOrWhiteSpace(state.StateKey))
            {
                throw new ArgumentException("同步状态必须包含稳定键。", "state");
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT OR REPLACE INTO DictionarySyncState
(StateKey, Revision, ManifestHash, LocalPayloadHash, LastSyncAtUtc, LastSuccessfulOperationId)
VALUES
(@StateKey, @Revision, @ManifestHash, @LocalPayloadHash, @LastSyncAtUtc, @LastSuccessfulOperationId);";
                command.Parameters.AddWithValue("@StateKey", state.StateKey);
                command.Parameters.AddWithValue("@Revision", state.Revision);
                command.Parameters.AddWithValue("@ManifestHash", (object)state.ManifestHash ?? DBNull.Value);
                command.Parameters.AddWithValue("@LocalPayloadHash", (object)state.LocalPayloadHash ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "@LastSyncAtUtc",
                    state.LastSyncAtUtc.HasValue
                        ? (object)state.LastSyncAtUtc.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                        : DBNull.Value);
                command.Parameters.AddWithValue(
                    "@LastSuccessfulOperationId",
                    (object)state.LastSuccessfulOperationId ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260901 读取指定共享字典的本地修订游标，未同步时返回空值。</summary>
        public DictionarySyncState LoadSyncState(string stateKey)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT Revision, ManifestHash, LocalPayloadHash, LastSyncAtUtc, LastSuccessfulOperationId
FROM DictionarySyncState
WHERE StateKey = @StateKey
LIMIT 1;";
                command.Parameters.AddWithValue("@StateKey", NormalizeKey(stateKey));
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    return new DictionarySyncState
                    {
                        StateKey = NormalizeKey(stateKey),
                        Revision = Convert.ToInt64(reader["Revision"], CultureInfo.InvariantCulture),
                        ManifestHash = reader["ManifestHash"] == DBNull.Value ? null : reader["ManifestHash"].ToString(),
                        LocalPayloadHash = reader["LocalPayloadHash"] == DBNull.Value ? null : reader["LocalPayloadHash"].ToString(),
                        LastSyncAtUtc = ReadNullableUtcDateTime(reader["LastSyncAtUtc"]),
                        LastSuccessfulOperationId = reader["LastSuccessfulOperationId"] == DBNull.Value
                            ? null
                            : reader["LastSuccessfulOperationId"].ToString()
                    };
                }
            }
        }

        /// <summary>XMZADD 20260831 读取指定作用域的全部人工字典结论，以便快照或 AI 结果刷新后恢复人工优先级。</summary>
        public IList<DictionaryOverride> LoadOverrides(string scopeKey)
        {
            var result = new List<DictionaryOverride>();
            string normalizedScopeKey = NormalizeKey(scopeKey);
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ObjectName, FieldName, PropertyName, ManualValue,
OriginalAutomaticValue, IsLocked, Remark, UpdatedAt
FROM DictionaryOverrides
WHERE ScopeKey = @ScopeKey
ORDER BY ObjectName, FieldName, PropertyName;";
                command.Parameters.AddWithValue("@ScopeKey", normalizedScopeKey);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new DictionaryOverride
                        {
                            ScopeKey = normalizedScopeKey,
                            ObjectName = reader["ObjectName"].ToString(),
                            FieldName = reader["FieldName"].ToString(),
                            PropertyName = reader["PropertyName"].ToString(),
                            ManualValue = reader["ManualValue"].ToString(),
                            OriginalAutomaticValue = reader["OriginalAutomaticValue"].ToString(),
                            IsLocked = Convert.ToInt32(reader["IsLocked"], CultureInfo.InvariantCulture) == 1,
                            Remark = reader["Remark"].ToString(),
                            UpdatedAt = DateTime.Parse(reader["UpdatedAt"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>XMZADD 20260831 按作用域、对象、字段和属性定位人工字典结论，供快照或 AI 结果刷新后恢复人工优先级。</summary>
        public DictionaryOverride FindOverride(string scopeKey, string objectName, string fieldName, string propertyName)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ManualValue, OriginalAutomaticValue, IsLocked, Remark, UpdatedAt
FROM DictionaryOverrides
WHERE ScopeKey = @ScopeKey AND ObjectName = @ObjectName
AND FieldName = @FieldName AND PropertyName = @PropertyName;";
                AddOverrideKeyParameters(command, scopeKey, objectName, fieldName, propertyName);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    return new DictionaryOverride
                    {
                        ScopeKey = NormalizeKey(scopeKey),
                        ObjectName = NormalizeKey(objectName),
                        FieldName = NormalizeKey(fieldName),
                        PropertyName = NormalizeKey(propertyName),
                        ManualValue = reader["ManualValue"].ToString(),
                        OriginalAutomaticValue = reader["OriginalAutomaticValue"].ToString(),
                        IsLocked = Convert.ToInt32(reader["IsLocked"], CultureInfo.InvariantCulture) == 1,
                        Remark = reader["Remark"].ToString(),
                        UpdatedAt = DateTime.Parse(reader["UpdatedAt"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    };
                }
            }
        }

        /// <summary>XMZADD 20260831 创建并增量升级本机字典表，使所有可写信息均被限制在当前用户的 SQLite 文件内。</summary>
        private void EnsureSchema()
        {
            string directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (IsCurrentSchema())
            {
                return;
            }

            if (ExistingSchemaRequiresColumnMigration())
            {
                BackupBeforeMigration();
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
CREATE TABLE IF NOT EXISTS LocalAiProviders (
    ProviderId TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Protocol INTEGER NOT NULL,
    Endpoint TEXT NOT NULL,
    Model TEXT NOT NULL,
    EncryptedApiKey BLOB,
    IsDefault INTEGER NOT NULL,
    IsEnabled INTEGER NOT NULL,
    TimeoutSeconds INTEGER NOT NULL,
    MaxConcurrency INTEGER NOT NULL,
    BatchSize INTEGER NOT NULL,
    ConfigurationVersion TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS DictionaryOverrides (
    ScopeKey TEXT NOT NULL,
    ObjectName TEXT NOT NULL,
    FieldName TEXT NOT NULL,
    PropertyName TEXT NOT NULL,
    ManualValue TEXT NOT NULL,
    OriginalAutomaticValue TEXT NOT NULL,
    IsLocked INTEGER NOT NULL,
    Remark TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY (ScopeKey, ObjectName, FieldName, PropertyName)
);
CREATE TABLE IF NOT EXISTS AiExplanations (
    ScopeKey TEXT NOT NULL,
    ObjectName TEXT NOT NULL,
    FieldName TEXT NOT NULL,
    ChineseName TEXT NOT NULL,
    BusinessMeaning TEXT NOT NULL,
    Usage TEXT NOT NULL,
    ConfidenceScore INTEGER NOT NULL,
    ProviderId TEXT NOT NULL,
    ProviderName TEXT NOT NULL,
    Model TEXT NOT NULL,
    ConfigurationVersion TEXT NOT NULL,
    GeneratedAt TEXT NOT NULL,
    PRIMARY KEY (ScopeKey, ObjectName, FieldName)
);
CREATE TABLE IF NOT EXISTS PendingDictionaryOperations (
    OperationId TEXT PRIMARY KEY,
    PayloadJson TEXT NOT NULL,
    Status INTEGER NOT NULL,
    AttemptCount INTEGER NOT NULL,
    NextAttemptAtUtc TEXT,
    GitHubIssueNumber INTEGER,
    LastError TEXT,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS DictionarySyncState (
    StateKey TEXT PRIMARY KEY,
    Revision INTEGER NOT NULL,
    ManifestHash TEXT,
    LocalPayloadHash TEXT,
    LastSyncAtUtc TEXT,
    LastSuccessfulOperationId TEXT
);";
                    command.ExecuteNonQuery();
                }

                EnsureColumn(connection, transaction, "LocalAiProviders", "MaxConcurrency", "INTEGER NOT NULL DEFAULT 1");
                EnsureColumn(connection, transaction, "LocalAiProviders", "BatchSize", "INTEGER NOT NULL DEFAULT 10");
                EnsureColumn(connection, transaction, "LocalAiProviders", "ConfigurationVersion", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, transaction, "DictionaryOverrides", "OriginalAutomaticValue", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, transaction, "DictionaryOverrides", "IsLocked", "INTEGER NOT NULL DEFAULT 1");
                EnsureColumn(connection, transaction, "DictionaryOverrides", "Remark", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, transaction, "AiExplanations", "BusinessMeaning", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, transaction, "AiExplanations", "Usage", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, transaction, "AiExplanations", "ConfidenceScore", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, transaction, "AiExplanations", "ProviderId", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, transaction, "AiExplanations", "ProviderName", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, transaction, "AiExplanations", "Model", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, transaction, "AiExplanations", "ConfigurationVersion", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, transaction, "AiExplanations", "GeneratedAt", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000Z'");
                EnsureColumn(connection, transaction, "PendingDictionaryOperations", "PayloadJson", "TEXT NOT NULL DEFAULT '{}'");
                EnsureColumn(connection, transaction, "PendingDictionaryOperations", "Status", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, transaction, "PendingDictionaryOperations", "AttemptCount", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, transaction, "PendingDictionaryOperations", "NextAttemptAtUtc", "TEXT");
                EnsureColumn(connection, transaction, "PendingDictionaryOperations", "GitHubIssueNumber", "INTEGER");
                EnsureColumn(connection, transaction, "PendingDictionaryOperations", "LastError", "TEXT DEFAULT ''");
                EnsureColumn(connection, transaction, "PendingDictionaryOperations", "CreatedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000Z'");
                EnsureColumn(connection, transaction, "PendingDictionaryOperations", "UpdatedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000Z'");
                EnsureColumn(connection, transaction, "DictionarySyncState", "Revision", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, transaction, "DictionarySyncState", "ManifestHash", "TEXT");
                EnsureColumn(connection, transaction, "DictionarySyncState", "LocalPayloadHash", "TEXT");
                EnsureColumn(connection, transaction, "DictionarySyncState", "LastSyncAtUtc", "TEXT");
                EnsureColumn(connection, transaction, "DictionarySyncState", "LastSuccessfulOperationId", "TEXT");
                EnsureSyncTablePrimaryKeys(connection, transaction);

                using (SQLiteCommand version = connection.CreateCommand())
                {
                    version.Transaction = transaction;
                    version.CommandText = "PRAGMA user_version = 3;";
                    version.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        /// <summary>XMZADD 20260831 只读确认本地结构已是当前版本，避免每次打开窗口都申请 SQLite 写锁。</summary>
        private bool IsCurrentSchema()
        {
            if (!File.Exists(_databasePath) || new FileInfo(_databasePath).Length == 0)
            {
                return false;
            }

            using (SQLiteConnection connection = OpenConnection())
            {
                using (SQLiteCommand version = connection.CreateCommand())
                {
                    version.CommandText = "PRAGMA user_version;";
                    if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) < 3)
                    {
                        return false;
                    }
                }

                return TableExists(connection, "LocalAiProviders") &&
                       TableExists(connection, "DictionaryOverrides") &&
                       TableExists(connection, "AiExplanations") &&
                       TableExists(connection, "PendingDictionaryOperations") &&
                       TableExists(connection, "DictionarySyncState") &&
                       TableHasPendingOperationPrimaryKey(connection, null) &&
                       TableHasSyncStatePrimaryKey(connection, null) &&
                       !TableNeedsColumns(connection, "LocalAiProviders", new[] { "MaxConcurrency", "BatchSize", "ConfigurationVersion" }) &&
                       !TableNeedsColumns(connection, "DictionaryOverrides", new[] { "OriginalAutomaticValue", "IsLocked", "Remark" }) &&
                       !TableNeedsColumns(connection, "AiExplanations", new[] { "BusinessMeaning", "Usage", "ConfidenceScore", "ProviderId", "ProviderName", "Model", "ConfigurationVersion", "GeneratedAt" }) &&
                       !TableNeedsColumns(connection, "PendingDictionaryOperations", PendingOperationColumns) &&
                       !TableNeedsColumns(connection, "DictionarySyncState", SyncStateColumns);
            }
        }

        /// <summary>XMZADD 20260831 检查旧本地表是否缺少当前版本读取所需列，以决定是否先建立可恢复备份。</summary>
        private bool ExistingSchemaRequiresColumnMigration()
        {
            if (!File.Exists(_databasePath) || new FileInfo(_databasePath).Length == 0)
            {
                return false;
            }

            using (SQLiteConnection connection = OpenConnection())
            {
                return TableNeedsColumns(connection, "LocalAiProviders", new[] { "MaxConcurrency", "BatchSize", "ConfigurationVersion" }) ||
                       TableNeedsColumns(connection, "DictionaryOverrides", new[] { "OriginalAutomaticValue", "IsLocked", "Remark" }) ||
                       TableNeedsColumns(connection, "AiExplanations", new[] { "BusinessMeaning", "Usage", "ConfidenceScore", "ProviderId", "ProviderName", "Model", "ConfigurationVersion", "GeneratedAt" }) ||
                       TableNeedsColumns(connection, "PendingDictionaryOperations", PendingOperationColumns) ||
                       TableNeedsColumns(connection, "DictionarySyncState", SyncStateColumns) ||
                       (TableExists(connection, "PendingDictionaryOperations") && !TableHasPendingOperationPrimaryKey(connection, null)) ||
                       (TableExists(connection, "DictionarySyncState") && !TableHasSyncStatePrimaryKey(connection, null));
            }
        }

        /// <summary>XMZADD 20260831 在修改旧本地表前保留一次原文件副本，迁移异常时可人工恢复配置和字典。</summary>
        private void BackupBeforeMigration()
        {
            string backupPath = _databasePath + ".backup-v1";
            if (!File.Exists(backupPath))
            {
                File.Copy(_databasePath, backupPath, false);
            }
        }

        /// <summary>XMZADD 20260831 判断已存在的本地表是否缺少任一必需列，新表由建表语句负责创建。</summary>
        private static bool TableNeedsColumns(SQLiteConnection connection, string tableName, string[] requiredColumns)
        {
            if (!TableExists(connection, tableName))
            {
                return false;
            }
            for (int i = 0; i < requiredColumns.Length; i++)
            {
                if (!TableHasColumn(connection, null, tableName, requiredColumns[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260831 查询本地 SQLite 表是否存在，避免把首次建表误判为旧结构迁移。</summary>
        private static bool TableExists(SQLiteConnection connection, string tableName)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @TableName;";
                command.Parameters.AddWithValue("@TableName", tableName);
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }

        /// <summary>XMZADD 20260831 以可重复执行方式为旧本地表补列，保证跨版本启动不会因缺列失败。</summary>
        private static void EnsureColumn(SQLiteConnection connection, SQLiteTransaction transaction,
            string tableName, string columnName, string columnDefinition)
        {
            if (TableHasColumn(connection, transaction, tableName, columnName))
            {
                return;
            }
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "ALTER TABLE [" + tableName + "] ADD COLUMN [" + columnName + "] " + columnDefinition + ";";
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260831 读取 SQLite 表列清单以支持无损增量迁移。</summary>
        private static bool TableHasColumn(SQLiteConnection connection, SQLiteTransaction transaction,
            string tableName, string columnName)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA table_info([" + tableName + "]);";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader["name"].ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 在列迁移完成后修复同步表主键约束，并按原 rowid 保留每个业务键的首条记录。</summary>
        private static void EnsureSyncTablePrimaryKeys(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            if (TableHasColumn(connection, transaction, "PendingDictionaryOperations", "OperationId") &&
                !TableHasPendingOperationPrimaryKey(connection, transaction))
            {
                RebuildPendingOperationTable(connection, transaction);
            }
            if (TableHasColumn(connection, transaction, "DictionarySyncState", "StateKey") &&
                !TableHasSyncStatePrimaryKey(connection, transaction))
            {
                RebuildSyncStateTable(connection, transaction);
            }
        }

        /// <summary>XMZADD 20260901 确认待上传队列表仅以 OperationId 作为真实主键。</summary>
        private static bool TableHasPendingOperationPrimaryKey(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            return TableHasSinglePrimaryKey(
                connection,
                transaction,
                "PRAGMA table_info([PendingDictionaryOperations]);",
                "OperationId");
        }

        /// <summary>XMZADD 20260901 确认同步游标表仅以 StateKey 作为真实主键。</summary>
        private static bool TableHasSyncStatePrimaryKey(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            return TableHasSinglePrimaryKey(
                connection,
                transaction,
                "PRAGMA table_info([DictionarySyncState]);",
                "StateKey");
        }

        /// <summary>XMZADD 20260901 读取 SQLite 主键序号并拒绝普通列或复合主键伪装成计划主键。</summary>
        private static bool TableHasSinglePrimaryKey(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string pragmaCommandText,
            string expectedColumnName)
        {
            int primaryKeyColumnCount = 0;
            bool expectedColumnIsPrimaryKey = false;
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = pragmaCommandText;
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int primaryKeyPosition = Convert.ToInt32(reader["pk"], CultureInfo.InvariantCulture);
                        if (primaryKeyPosition <= 0)
                        {
                            continue;
                        }

                        primaryKeyColumnCount++;
                        if (string.Equals(reader["name"].ToString(), expectedColumnName, StringComparison.OrdinalIgnoreCase))
                        {
                            expectedColumnIsPrimaryKey = true;
                        }
                    }
                }
            }
            return primaryKeyColumnCount == 1 && expectedColumnIsPrimaryKey;
        }

        /// <summary>XMZADD 20260901 以固定结构重建待上传队列表，消除重复批次键并完整保留首条载荷和状态。</summary>
        private static void RebuildPendingOperationTable(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"DROP TABLE IF EXISTS [PendingDictionaryOperations_PrimaryKeyMigration];
CREATE TABLE [PendingDictionaryOperations_PrimaryKeyMigration] (
    OperationId TEXT PRIMARY KEY,
    PayloadJson TEXT NOT NULL,
    Status INTEGER NOT NULL,
    AttemptCount INTEGER NOT NULL,
    NextAttemptAtUtc TEXT,
    GitHubIssueNumber INTEGER,
    LastError TEXT,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
INSERT OR IGNORE INTO [PendingDictionaryOperations_PrimaryKeyMigration]
(OperationId, PayloadJson, Status, AttemptCount, NextAttemptAtUtc, GitHubIssueNumber, LastError, CreatedAtUtc, UpdatedAtUtc)
SELECT OperationId, PayloadJson, Status, AttemptCount, NextAttemptAtUtc, GitHubIssueNumber, LastError, CreatedAtUtc, UpdatedAtUtc
FROM [PendingDictionaryOperations]
ORDER BY rowid;
DROP TABLE [PendingDictionaryOperations];
ALTER TABLE [PendingDictionaryOperations_PrimaryKeyMigration] RENAME TO [PendingDictionaryOperations];";
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260901 以固定结构重建同步游标表，消除重复状态键并保留每键最早的本地游标。</summary>
        private static void RebuildSyncStateTable(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"DROP TABLE IF EXISTS [DictionarySyncState_PrimaryKeyMigration];
CREATE TABLE [DictionarySyncState_PrimaryKeyMigration] (
    StateKey TEXT PRIMARY KEY,
    Revision INTEGER NOT NULL,
    ManifestHash TEXT,
    LocalPayloadHash TEXT,
    LastSyncAtUtc TEXT,
    LastSuccessfulOperationId TEXT
);
INSERT OR IGNORE INTO [DictionarySyncState_PrimaryKeyMigration]
(StateKey, Revision, ManifestHash, LocalPayloadHash, LastSyncAtUtc, LastSuccessfulOperationId)
SELECT StateKey, Revision, ManifestHash, LocalPayloadHash, LastSyncAtUtc, LastSuccessfulOperationId
FROM [DictionarySyncState]
ORDER BY rowid;
DROP TABLE [DictionarySyncState];
ALTER TABLE [DictionarySyncState_PrimaryKeyMigration] RENAME TO [DictionarySyncState];";
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260831 查询当前默认服务商标识，以便保存配置时维持自动推测调用目标唯一。</summary>
        private static string FindDefaultProviderId(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT ProviderId FROM LocalAiProviders
WHERE IsDefault = 1
ORDER BY UpdatedAt DESC, ProviderId
LIMIT 1;";
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? string.Empty : value.ToString();
            }
        }

        /// <summary>XMZADD 20260831 读取同一服务商原有密钥密文，使空白编辑保持凭据而不需要在界面回显明文。</summary>
        private static byte[] FindEncryptedApiKey(SQLiteConnection connection, SQLiteTransaction transaction, string providerId)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT EncryptedApiKey FROM LocalAiProviders WHERE ProviderId = @ProviderId LIMIT 1;";
                command.Parameters.AddWithValue("@ProviderId", providerId);
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : (byte[])value;
            }
        }

        /// <summary>XMZADD 20260831 按服务商标识的稳定顺序选取剩余配置，作为删除默认项后的新默认调用目标。</summary>
        private static string FindFirstProviderId(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT ProviderId FROM LocalAiProviders
ORDER BY ProviderId
LIMIT 1;";
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? string.Empty : value.ToString();
            }
        }

        /// <summary>XMZADD 20260831 将 SQLite 记录转换为应用配置，并只在当前 Windows 用户身份下解密 API 密钥。</summary>
        private static AiProviderConfiguration ReadAiProvider(SQLiteDataReader reader)
        {
            byte[] protectedApiKey = reader["EncryptedApiKey"] == DBNull.Value ? null : (byte[])reader["EncryptedApiKey"];
            return new AiProviderConfiguration
            {
                Id = reader["ProviderId"].ToString(),
                Name = reader["Name"].ToString(),
                Protocol = (AiProviderProtocol)Convert.ToInt32(reader["Protocol"], CultureInfo.InvariantCulture),
                Endpoint = reader["Endpoint"].ToString(),
                Model = reader["Model"].ToString(),
                ApiKey = UnprotectApiKey(protectedApiKey),
                IsDefault = Convert.ToInt32(reader["IsDefault"], CultureInfo.InvariantCulture) == 1,
                IsEnabled = Convert.ToInt32(reader["IsEnabled"], CultureInfo.InvariantCulture) == 1,
                TimeoutSeconds = Convert.ToInt32(reader["TimeoutSeconds"], CultureInfo.InvariantCulture),
                MaxConcurrency = Convert.ToInt32(reader["MaxConcurrency"], CultureInfo.InvariantCulture),
                BatchSize = Convert.ToInt32(reader["BatchSize"], CultureInfo.InvariantCulture),
                ConfigurationVersion = reader["ConfigurationVersion"].ToString(),
                UpdatedAt = DateTime.Parse(reader["UpdatedAt"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            };
        }

        /// <summary>XMZADD 20260831 使用当前 Windows 用户的 DPAPI 保护 API 密钥，避免密钥以明文形式保存在本机 SQLite 文件。</summary>
        private static byte[] ProtectApiKey(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                return null;
            }

            return ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), ApiKeyEntropy, DataProtectionScope.CurrentUser);
        }

        /// <summary>XMZADD 20260831 在当前 Windows 用户身份下恢复 API 密钥，跨用户或损坏数据应由 DPAPI 显式阻止读取。</summary>
        private static string UnprotectApiKey(byte[] protectedApiKey)
        {
            if (protectedApiKey == null || protectedApiKey.Length == 0)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedApiKey, ApiKeyEntropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>XMZADD 20260831 统一人工字典键的空值表示，确保表级属性和字段级属性均可稳定定位。</summary>
        private static string NormalizeKey(string value)
        {
            return value ?? string.Empty;
        }

        /// <summary>XMZADD 20260831 为人工字典记录添加完整定位参数，保证重复保存只替换同一项人工结论。</summary>
        private static void AddOverrideKeyParameters(SQLiteCommand command, string scopeKey, string objectName, string fieldName, string propertyName)
        {
            command.Parameters.AddWithValue("@ScopeKey", NormalizeKey(scopeKey));
            command.Parameters.AddWithValue("@ObjectName", NormalizeKey(objectName));
            command.Parameters.AddWithValue("@FieldName", NormalizeKey(fieldName));
            command.Parameters.AddWithValue("@PropertyName", NormalizeKey(propertyName));
        }

        /// <summary>XMZADD 20260901 使用共享参数化语句保存人工覆盖，确保单项保存和批量事务采用同一数据规则。</summary>
        private static void SaveOverride(SQLiteCommand command, DictionaryOverride item)
        {
            DateTime updatedAt = item.UpdatedAt == DateTime.MinValue ? DateTime.UtcNow : item.UpdatedAt;
            command.CommandText = @"INSERT OR REPLACE INTO DictionaryOverrides
(ScopeKey, ObjectName, FieldName, PropertyName, ManualValue, OriginalAutomaticValue, IsLocked, Remark, UpdatedAt)
VALUES
(@ScopeKey, @ObjectName, @FieldName, @PropertyName, @ManualValue, @OriginalAutomaticValue, @IsLocked, @Remark, @UpdatedAt);";
            AddOverrideKeyParameters(command, item.ScopeKey, item.ObjectName, item.FieldName, item.PropertyName);
            command.Parameters.AddWithValue("@ManualValue", item.ManualValue ?? string.Empty);
            command.Parameters.AddWithValue("@OriginalAutomaticValue", item.OriginalAutomaticValue ?? string.Empty);
            command.Parameters.AddWithValue("@IsLocked", item.IsLocked ? 1 : 0);
            command.Parameters.AddWithValue("@Remark", item.Remark ?? string.Empty);
            command.Parameters.AddWithValue("@UpdatedAt", updatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        /// <summary>XMZADD 20260901 在打开事务前拒绝缺少稳定键的编辑批次，避免出现只有覆盖或只有队列的半成品。</summary>
        private static void ValidatePendingBatch(IList<DictionaryOverride> overrides, DictionaryChangeBatch batch)
        {
            Guid parsedId;
            if (overrides == null || overrides.Count == 0)
            {
                throw new ArgumentException("至少需要一项人工覆盖。", "overrides");
            }
            if (batch == null)
            {
                throw new ArgumentNullException("batch");
            }
            if (!Guid.TryParseExact(batch.BatchId, "N", out parsedId))
            {
                throw new ArgumentException("批次编号必须为 N 格式 GUID。", "batch");
            }
            if (batch.Operations == null || batch.Operations.Count == 0)
            {
                throw new ArgumentException("批次必须包含至少一项远程幂等操作。", "batch");
            }

            for (int i = 0; i < overrides.Count; i++)
            {
                DictionaryOverride item = overrides[i];
                if (item == null || string.IsNullOrWhiteSpace(item.ScopeKey) ||
                    string.IsNullOrWhiteSpace(item.ObjectName) || string.IsNullOrWhiteSpace(item.PropertyName))
                {
                    throw new ArgumentException("人工覆盖缺少必要定位键。", "overrides");
                }
            }
            for (int i = 0; i < batch.Operations.Count; i++)
            {
                DictionaryChangeOperation operation = batch.Operations[i];
                if (operation == null || !Guid.TryParseExact(operation.OperationId, "N", out parsedId) ||
                    string.IsNullOrWhiteSpace(operation.ObjectKey) || string.IsNullOrWhiteSpace(operation.PropertyName))
                {
                    throw new ArgumentException("远程操作缺少必要幂等键。", "batch");
                }
            }
        }

        /// <summary>XMZADD 20260901 读取已占用批次键的原始载荷以区分安全重试和冲突重用。</summary>
        private static string FindPendingPayload(SQLiteConnection connection, SQLiteTransaction transaction, string batchId)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT PayloadJson FROM PendingDictionaryOperations WHERE OperationId = @OperationId LIMIT 1;";
                command.Parameters.AddWithValue("@OperationId", batchId);
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : value.ToString();
            }
        }

        /// <summary>XMZADD 20260901 将 SQLite 中的必填往返时间恢复为明确 UTC，避免重试调度受本机时区影响。</summary>
        private static DateTime ReadUtcDateTime(object value)
        {
            return DateTime.Parse(
                value.ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        /// <summary>XMZADD 20260901 恢复可空 UTC 调度时间，空值表示当前没有延迟重试计划。</summary>
        private static DateTime? ReadNullableUtcDateTime(object value)
        {
            if (value == null || value == DBNull.Value || string.IsNullOrWhiteSpace(value.ToString()))
            {
                return null;
            }

            return ReadUtcDateTime(value);
        }

        /// <summary>XMZADD 20260901 以单一参数化语句更新队列生命周期，统一重试次数、问题编号和诊断规则。</summary>
        private void UpdateOperationStatus(
            string operationId,
            PendingOperationStatus status,
            int attemptIncrement,
            DateTime? nextAttemptAtUtc,
            int? issueNumber,
            bool replaceIssueNumber,
            string lastError)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE PendingDictionaryOperations
SET Status = @Status,
    AttemptCount = AttemptCount + @AttemptIncrement,
    NextAttemptAtUtc = @NextAttemptAtUtc,
    GitHubIssueNumber = CASE WHEN @ReplaceIssueNumber = 1 THEN @GitHubIssueNumber ELSE GitHubIssueNumber END,
    LastError = @LastError,
    UpdatedAtUtc = @UpdatedAtUtc
WHERE OperationId = @OperationId;";
                command.Parameters.AddWithValue("@Status", (int)status);
                command.Parameters.AddWithValue("@AttemptIncrement", attemptIncrement);
                command.Parameters.AddWithValue(
                    "@NextAttemptAtUtc",
                    nextAttemptAtUtc.HasValue
                        ? (object)nextAttemptAtUtc.Value.ToString("O", CultureInfo.InvariantCulture)
                        : DBNull.Value);
                command.Parameters.AddWithValue("@ReplaceIssueNumber", replaceIssueNumber ? 1 : 0);
                command.Parameters.AddWithValue(
                    "@GitHubIssueNumber",
                    issueNumber.HasValue ? (object)issueNumber.Value : DBNull.Value);
                command.Parameters.AddWithValue("@LastError", NormalizeOperationError(lastError));
                command.Parameters.AddWithValue("@UpdatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("@OperationId", NormalizeKey(operationId));
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260901 限制本地失败诊断体积，避免远程响应或异常堆栈无限扩大用户数据库。</summary>
        private static string NormalizeOperationError(string lastError)
        {
            string value = lastError ?? string.Empty;
            return value.Length <= 2000 ? value : value.Substring(0, 2000);
        }

        /// <summary>XMZADD 20260831 打开应用专用的本机 SQLite 文件，不建立也不使用任何 EOS 数据库连接。</summary>
        private SQLiteConnection OpenConnection()
        {
            var builder = new SQLiteConnectionStringBuilder();
            builder.DataSource = _databasePath;
            builder.Version = 3;
            // 等待短暂的本地读写事务释放锁，避免启动或刷新交叠时直接终止程序。
            builder.BusyTimeout = 30000;
            builder.DefaultTimeout = 30;
            var connection = new SQLiteConnection(builder.ConnectionString);
            connection.Open();
            return connection;
        }
    }
}
