using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证 GitHub 个人凭据只以当前 Windows 用户可解密的密文落盘。</summary>
    [TestClass]
    public sealed class GitHubCredentialStoreTests
    {
        /// <summary>XMZADD 20260901 验证保存后的文件不包含明文 Token 且可由当前用户恢复。</summary>
        [TestMethod]
        public void SaveAndLoad_ProtectsTokenForCurrentWindowsUser()
        {
            string directory = CreateTemporaryDirectory();
            string path = Path.Combine(directory, "github-token.dat");
            const string token = "github_pat_secret-value-123";
            try
            {
                var store = new GitHubCredentialStore(path);

                store.Save(token);

                byte[] encrypted = File.ReadAllBytes(path);
                Assert.IsFalse(Encoding.UTF8.GetString(encrypted).Contains(token));
                Assert.IsFalse(Convert.ToBase64String(encrypted).Contains(token));
                Assert.AreEqual(token, store.Load());
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        /// <summary>XMZADD 20260901 验证清除凭据后文件和可读取值都消失。</summary>
        [TestMethod]
        public void Clear_RemovesProtectedCredential()
        {
            string directory = CreateTemporaryDirectory();
            string path = Path.Combine(directory, "github-token.dat");
            try
            {
                var store = new GitHubCredentialStore(path);
                store.Save("secret-token");

                store.Clear();

                Assert.IsFalse(File.Exists(path));
                Assert.IsNull(store.Load());
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        /// <summary>XMZADD 20260901 验证损坏或其他用户的密文只返回通用错误且不泄露凭据。</summary>
        [TestMethod]
        public void Load_CorruptedProtectedData_ThrowsGenericMessage()
        {
            string directory = CreateTemporaryDirectory();
            string path = Path.Combine(directory, "github-token.dat");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
                var store = new GitHubCredentialStore(path);

                InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                    delegate { store.Load(); });

                StringAssert.Contains(exception.Message, "GitHub 凭据无法读取");
                Assert.IsFalse(exception.ToString().Contains("secret-token"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        /// <summary>XMZADD 20260901 创建互不冲突的临时目录以隔离凭据文件测试。</summary>
        private static string CreateTemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "SHB.EosDataDictionary.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
