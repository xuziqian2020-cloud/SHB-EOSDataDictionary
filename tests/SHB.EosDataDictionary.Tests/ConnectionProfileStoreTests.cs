using System.IO;
using System.Data.SQLite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class ConnectionProfileStoreTests
    {
        [TestMethod]
        public void SaveAndLoad_RoundTripsPasswordAndScope()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-connection-" + System.Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new ConnectionProfileStore(path);
                store.Save(new ConnectionProfile
                {
                    Id = "profile-1",
                    Name = "EOS 测试库",
                    Server = "127.0.0.1",
                    Port = 1433,
                    Database = "EOS_TEST",
                    AuthenticationMode = AuthenticationMode.SqlServer,
                    UserName = "sa",
                    Password = "密-码-123",
                    IsReadOnly = true
                });

                var loaded = store.LoadAll();

                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual("密-码-123", loaded[0].Password);
                Assert.AreEqual("profile-1|EOS_TEST", loaded[0].ScopeKey);
                Assert.IsFalse(File.ReadAllText(path).Contains("密-码-123"));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证首次保存结构连接时固定为EOS正式库只读配置且密码仍只以DPAPI密文落盘。</summary>
        [TestMethod]
        public void SaveDefaultEosProductionProfile_NewProfile_IsNamedAndReadOnly()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-connection-" + System.Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new ConnectionProfileStore(path);
                ConnectionProfile saved = store.SaveDefaultEosProductionProfile(new ConnectionProfile
                {
                    Id = "production-scope",
                    Name = "临时名称",
                    Server = "127.0.0.1",
                    Database = "EOS_TEST",
                    AuthenticationMode = AuthenticationMode.SqlServer,
                    UserName = "test-user",
                    Password = "fake-password",
                    IsReadOnly = false
                });

                ConnectionProfile loaded;
                Assert.IsTrue(store.TryLoadDefaultEosProductionProfile(out loaded));
                Assert.AreEqual(ConnectionProfileStore.DefaultEosProductionProfileName, saved.Name);
                Assert.AreEqual(ConnectionProfileStore.DefaultEosProductionProfileName, loaded.Name);
                Assert.IsTrue(loaded.IsReadOnly);
                Assert.AreEqual("fake-password", loaded.Password);
                Assert.IsFalse(File.ReadAllText(path).Contains("fake-password"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>XMZADD 20260901 验证同scope旧配置迁移只改默认名称和只读标记，不覆盖原DPAPI密文或连接内容。</summary>
        [TestMethod]
        public void SaveDefaultEosProductionProfile_ExistingScope_ReusesWithoutOverwritingCiphertext()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-connection-" + System.Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new ConnectionProfileStore(path);
                store.Save(new ConnectionProfile
                {
                    Id = "production-scope",
                    Name = "旧正式连接",
                    Server = "127.0.0.1",
                    Database = "EOS_TEST",
                    AuthenticationMode = AuthenticationMode.SqlServer,
                    UserName = "existing-user",
                    Password = "original-fake-password",
                    IsReadOnly = false
                });
                byte[] beforeCiphertext = ReadCiphertext(path, "production-scope");

                ConnectionProfile migrated = store.SaveDefaultEosProductionProfile(new ConnectionProfile
                {
                    Id = "production-scope",
                    Name = "新输入",
                    Server = "192.0.2.10",
                    Database = "EOS_TEST",
                    AuthenticationMode = AuthenticationMode.SqlServer,
                    UserName = "replacement-user",
                    Password = "replacement-fake-password",
                    IsReadOnly = true
                });
                byte[] afterCiphertext = ReadCiphertext(path, "production-scope");

                Assert.AreEqual(ConnectionProfileStore.DefaultEosProductionProfileName, migrated.Name);
                Assert.IsTrue(migrated.IsReadOnly);
                Assert.AreEqual("127.0.0.1", migrated.Server);
                Assert.AreEqual("existing-user", migrated.UserName);
                Assert.AreEqual("original-fake-password", migrated.Password);
                CollectionAssert.AreEqual(beforeCiphertext, afterCiphertext);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>XMZADD 20260901 验证首次窗口生成随机新ID时仍按服务器、数据库和认证迁移旧连接且保留原凭据密文。</summary>
        [TestMethod]
        public void SaveDefaultEosProductionProfile_RandomCandidateId_MatchesExistingConnectionIdentity()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-connection-" + System.Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new ConnectionProfileStore(path);
                store.Save(new ConnectionProfile
                {
                    Id = "persisted-old-id",
                    Name = "旧版本连接",
                    Server = "db.example.local",
                    Port = 1433,
                    Database = "EOS_SAMPLE",
                    AuthenticationMode = AuthenticationMode.SqlServer,
                    UserName = "existing-user",
                    Password = "original-fake-password",
                    IsReadOnly = false
                });
                byte[] beforeCiphertext = ReadCiphertext(path, "persisted-old-id");

                ConnectionProfile migrated = store.SaveDefaultEosProductionProfile(new ConnectionProfile
                {
                    Id = System.Guid.NewGuid().ToString("N"),
                    Name = "窗口临时配置",
                    Server = " DB.EXAMPLE.LOCAL ",
                    Port = 1433,
                    Database = "eos_sample",
                    AuthenticationMode = AuthenticationMode.SqlServer,
                    UserName = "replacement-user",
                    Password = "replacement-fake-password",
                    IsReadOnly = true
                });
                byte[] afterCiphertext = ReadCiphertext(path, "persisted-old-id");

                Assert.AreEqual("persisted-old-id", migrated.Id);
                Assert.AreEqual(ConnectionProfileStore.DefaultEosProductionProfileName, migrated.Name);
                Assert.AreEqual("existing-user", migrated.UserName);
                Assert.AreEqual("original-fake-password", migrated.Password);
                Assert.AreEqual(1, store.LoadAll().Count);
                CollectionAssert.AreEqual(beforeCiphertext, afterCiphertext);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>XMZADD 20260901 验证源码和知识库目录仅保存在本机 SQLite，供下次结构维护继续使用。</summary>
        [TestMethod]
        public void StructurePaths_SaveAndLoad_RoundTripsLocalConfiguration()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-connection-" + System.Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new ConnectionProfileStore(path);

                store.SaveStructurePaths("D:\\EOS-Source", "D:\\EOS-Knowledge");

                string sourceRoot;
                string knowledgeBaseRoot;
                store.LoadStructurePaths(out sourceRoot, out knowledgeBaseRoot);
                Assert.AreEqual("D:\\EOS-Source", sourceRoot);
                Assert.AreEqual("D:\\EOS-Knowledge", knowledgeBaseRoot);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>XMZADD 20260901 从临时SQLite读取DPAPI密文字节以验证迁移没有重新加密或覆盖密码。</summary>
        private static byte[] ReadCiphertext(string path, string profileId)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT EncryptedPassword FROM ConnectionProfiles WHERE Id=@Id;";
                    command.Parameters.AddWithValue("@Id", profileId);
                    return (byte[])command.ExecuteScalar();
                }
            }
        }
    }
}
