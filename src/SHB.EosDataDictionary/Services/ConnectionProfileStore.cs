using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 保存本机 EOS 连接配置并保护 SQL Server 密码。</summary>
    public sealed class ConnectionProfileStore
    {
        public const string DefaultEosProductionProfileName = "EOS 正式库";
        private readonly string _databasePath;

        public ConnectionProfileStore(string databasePath)
        {
            _databasePath = databasePath;
            EnsureSchema();
        }

        /// <summary>XMZADD 20260828 保存连接配置并让密码只以当前用户 DPAPI 密文落盘。</summary>
        public void Save(ConnectionProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT OR REPLACE INTO ConnectionProfiles
(Id, Name, ServerName, Port, DatabaseName, AuthenticationMode, UserName,
 EncryptedPassword, TrustServerCertificate, IsReadOnly)
VALUES (@Id, @Name, @ServerName, @Port, @DatabaseName, @AuthenticationMode,
        @UserName, @EncryptedPassword, @TrustServerCertificate, @IsReadOnly);";
                command.Parameters.AddWithValue("@Id", profile.Id);
                command.Parameters.AddWithValue("@Name", profile.Name ?? string.Empty);
                command.Parameters.AddWithValue("@ServerName", profile.Server ?? string.Empty);
                command.Parameters.AddWithValue("@Port", profile.Port);
                command.Parameters.AddWithValue("@DatabaseName", profile.Database ?? string.Empty);
                command.Parameters.AddWithValue("@AuthenticationMode", (int)profile.AuthenticationMode);
                command.Parameters.AddWithValue("@UserName", profile.UserName ?? string.Empty);
                command.Parameters.AddWithValue("@EncryptedPassword", Protect(profile.Password));
                command.Parameters.AddWithValue("@TrustServerCertificate", profile.TrustServerCertificate ? 1 : 0);
                command.Parameters.AddWithValue("@IsReadOnly", profile.IsReadOnly ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260901 保存或迁移本机唯一的 EOS 正式库只读配置，并在复用作用域时保留原有 DPAPI 密文。</summary>
        public ConnectionProfile SaveDefaultEosProductionProfile(ConnectionProfile candidate)
        {
            if (candidate == null)
            {
                throw new ArgumentNullException("candidate");
            }

            IList<ConnectionProfile> profiles = LoadAll();
            ConnectionProfile existing = FindByName(profiles, DefaultEosProductionProfileName);
            if (existing == null)
            {
                existing = FindSameScope(profiles, candidate);
            }

            if (existing != null)
            {
                // 同一正式库作用域只迁移名称和只读标记，避免无意覆盖其他电脑或旧版本保存的凭据。
                UpdateDefaultProfileIdentity(existing.Id);
                return LoadById(existing.Id);
            }

            var profile = new ConnectionProfile
            {
                Id = candidate.Id,
                Name = DefaultEosProductionProfileName,
                Server = candidate.Server,
                Port = candidate.Port,
                Database = candidate.Database,
                AuthenticationMode = candidate.AuthenticationMode,
                UserName = candidate.UserName,
                Password = candidate.Password,
                TrustServerCertificate = candidate.TrustServerCertificate,
                IsReadOnly = true
            };
            Save(profile);
            return LoadById(profile.Id);
        }

        /// <summary>XMZADD 20260901 读取本机唯一的 EOS 正式库配置供结构维护扫描使用。</summary>
        public bool TryLoadDefaultEosProductionProfile(out ConnectionProfile profile)
        {
            profile = FindByName(LoadAll(), DefaultEosProductionProfileName);
            return profile != null;
        }

        /// <summary>XMZADD 20260901 保存结构维护使用的源码和知识库目录，本机路径不进入 GitHub 共享数据。</summary>
        public void SaveStructurePaths(string sourceRoot, string knowledgeBaseRoot)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT OR REPLACE INTO StructureMaintenanceSettings
(Id, SourceRoot, KnowledgeBaseRoot) VALUES (1, @SourceRoot, @KnowledgeBaseRoot);";
                command.Parameters.AddWithValue("@SourceRoot", sourceRoot ?? string.Empty);
                command.Parameters.AddWithValue("@KnowledgeBaseRoot", knowledgeBaseRoot ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260901 读取本机结构维护目录，缺失配置时返回空路径供发布者首次选择。</summary>
        public void LoadStructurePaths(out string sourceRoot, out string knowledgeBaseRoot)
        {
            sourceRoot = string.Empty;
            knowledgeBaseRoot = string.Empty;
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT SourceRoot, KnowledgeBaseRoot FROM StructureMaintenanceSettings WHERE Id = 1;";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        sourceRoot = reader["SourceRoot"].ToString();
                        knowledgeBaseRoot = reader["KnowledgeBaseRoot"].ToString();
                    }
                }
            }
        }

        /// <summary>XMZADD 20260828 读取本机保存的连接配置并在内存中恢复密码。</summary>
        public IList<ConnectionProfile> LoadAll()
        {
            var result = new List<ConnectionProfile>();
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT Id, Name, ServerName, Port, DatabaseName,
AuthenticationMode, UserName, EncryptedPassword, TrustServerCertificate, IsReadOnly
FROM ConnectionProfiles ORDER BY Name, Id;";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var profile = new ConnectionProfile
                        {
                            Id = reader["Id"].ToString(),
                            Name = reader["Name"].ToString(),
                            Server = reader["ServerName"].ToString(),
                            Port = Convert.ToInt32(reader["Port"]),
                            Database = reader["DatabaseName"].ToString(),
                            AuthenticationMode = (AuthenticationMode)Convert.ToInt32(reader["AuthenticationMode"]),
                            UserName = reader["UserName"].ToString(),
                            Password = Unprotect(reader["EncryptedPassword"] as byte[]),
                            TrustServerCertificate = Convert.ToInt32(reader["TrustServerCertificate"]) == 1,
                            IsReadOnly = Convert.ToInt32(reader["IsReadOnly"]) == 1
                        };
                        result.Add(profile);
                    }
                }
            }

            return result;
        }

        /// <summary>XMZADD 20260828 删除本机连接配置，不删除 EOS 数据库对象和数据。</summary>
        public void Delete(string profileId)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM ConnectionProfiles WHERE Id = @Id;";
                command.Parameters.AddWithValue("@Id", profileId ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260901 按配置名称查找本机连接配置。</summary>
        private static ConnectionProfile FindByName(IList<ConnectionProfile> profiles, string name)
        {
            for (int index = 0; index < profiles.Count; index++)
            {
                ConnectionProfile profile = profiles[index];
                if (string.Equals(profile.Name, name, StringComparison.Ordinal))
                {
                    return profile;
                }
            }

            return null;
        }

        /// <summary>XMZADD 20260901 优先按持久配置标识识别旧作用域，并兼容随机新标识下的同服务器数据库认证配置。</summary>
        private static ConnectionProfile FindSameScope(IList<ConnectionProfile> profiles, ConnectionProfile candidate)
        {
            for (int index = 0; index < profiles.Count; index++)
            {
                ConnectionProfile profile = profiles[index];
                bool samePersistentScope = string.Equals(profile.Id, candidate.Id, StringComparison.Ordinal) &&
                    string.Equals(profile.Database, candidate.Database, StringComparison.OrdinalIgnoreCase);
                bool sameConnectionIdentity = string.Equals(
                        (profile.Server ?? string.Empty).Trim(),
                        (candidate.Server ?? string.Empty).Trim(),
                        StringComparison.OrdinalIgnoreCase) &&
                    profile.Port == candidate.Port &&
                    string.Equals(
                        (profile.Database ?? string.Empty).Trim(),
                        (candidate.Database ?? string.Empty).Trim(),
                        StringComparison.OrdinalIgnoreCase) &&
                    profile.AuthenticationMode == candidate.AuthenticationMode;
                if (samePersistentScope || sameConnectionIdentity)
                {
                    return profile;
                }
            }

            return null;
        }

        /// <summary>XMZADD 20260901 仅更新正式库配置身份，保证已有连接内容和密码密文保持不变。</summary>
        private void UpdateDefaultProfileIdentity(string profileId)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE ConnectionProfiles
SET Name = @Name, IsReadOnly = 1
WHERE Id = @Id;";
                command.Parameters.AddWithValue("@Name", DefaultEosProductionProfileName);
                command.Parameters.AddWithValue("@Id", profileId ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260901 重新读取指定连接配置以返回数据库中最终保存的内容。</summary>
        private ConnectionProfile LoadById(string profileId)
        {
            IList<ConnectionProfile> profiles = LoadAll();
            for (int index = 0; index < profiles.Count; index++)
            {
                if (string.Equals(profiles[index].Id, profileId, StringComparison.Ordinal))
                {
                    return profiles[index];
                }
            }

            throw new InvalidOperationException("连接配置保存后无法重新读取。");
        }

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
CREATE TABLE IF NOT EXISTS ConnectionProfiles (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    ServerName TEXT NOT NULL,
    Port INTEGER NOT NULL,
    DatabaseName TEXT NOT NULL,
    AuthenticationMode INTEGER NOT NULL,
    UserName TEXT,
    EncryptedPassword BLOB,
    TrustServerCertificate INTEGER NOT NULL,
    IsReadOnly INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS StructureMaintenanceSettings (
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    SourceRoot TEXT NOT NULL,
    KnowledgeBaseRoot TEXT NOT NULL
);";
                command.ExecuteNonQuery();
            }
        }

        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection("Data Source=" + _databasePath + ";Version=3;");
            connection.Open();
            return connection;
        }

        private static byte[] Protect(string value)
        {
            byte[] clearBytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            return ProtectedData.Protect(clearBytes, null, DataProtectionScope.CurrentUser);
        }

        private static string Unprotect(byte[] protectedBytes)
        {
            if (protectedBytes == null || protectedBytes.Length == 0)
            {
                return string.Empty;
            }

            byte[] clearBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
    }
}
