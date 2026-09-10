using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证应用诊断日志在异常场景下保留排查依据而不泄露连接与访问凭据。</summary>
    [TestClass]
    public sealed class AppDiagnosticLogTests
    {
        /// <summary>XMZADD 20260831 验证诊断内容会移除 API Key、连接密码、Authorization、Bearer 与完整连接字符串。</summary>
        [TestMethod]
        public void Sanitize_SensitiveValues_AreNeverReturned()
        {
            string detail = "ApiKey=api-key-secret; Password=database-password; Authorization: Bearer authorization-secret\r\nConnectionString=Data Source=server01;Initial Catalog=EOS;User Id=sa;Password=connection-password";

            string sanitized = AppDiagnosticLog.Sanitize(detail);

            Assert.IsFalse(sanitized.IndexOf("api-key-secret", StringComparison.Ordinal) >= 0);
            Assert.IsFalse(sanitized.IndexOf("database-password", StringComparison.Ordinal) >= 0);
            Assert.IsFalse(sanitized.IndexOf("authorization-secret", StringComparison.Ordinal) >= 0);
            Assert.IsFalse(sanitized.IndexOf("connection-password", StringComparison.Ordinal) >= 0);
            Assert.IsFalse(sanitized.IndexOf("server01", StringComparison.Ordinal) >= 0);
            StringAssert.Contains(sanitized, "[已脱敏]");
        }

        /// <summary>XMZADD 20260831 验证诊断日志会创建目录、追加记录并受单文件容量约束。</summary>
        [TestMethod]
        public void WriteUnhandledException_CreatesDirectoryAndAppendsRedactedEntries()
        {
            string logPath = CreateTemporaryLogPath();
            try
            {
                var log = new AppDiagnosticLog(logPath, 4096, 512);

                log.WriteUnhandledException("第一处异常", new InvalidOperationException("Password=write-password; ApiKey=write-api-key"));
                log.WriteUnhandledException("第二处异常", new InvalidOperationException("Authorization: Bearer write-bearer"));

                Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(logPath)));
                Assert.IsTrue(File.Exists(logPath));
                Assert.IsTrue(new FileInfo(logPath).Length <= 4096);
                string content = File.ReadAllText(logPath);
                StringAssert.Contains(content, "[异常时间]");
                StringAssert.Contains(content, "第一处异常");
                StringAssert.Contains(content, "第二处异常");
                Assert.IsFalse(content.IndexOf("write-password", StringComparison.Ordinal) >= 0);
                Assert.IsFalse(content.IndexOf("write-api-key", StringComparison.Ordinal) >= 0);
                Assert.IsFalse(content.IndexOf("write-bearer", StringComparison.Ordinal) >= 0);
            }
            finally
            {
                DeleteTemporaryDirectory(logPath);
            }
        }

        /// <summary>XMZADD 20260831 验证主题加载失败时仍会尝试应用默认主题并写入本机诊断日志。</summary>
        [TestMethod]
        public void LoadThemeWithFallback_WhenThemeLoadingThrows_AppliesDefaultThemeAndContinues()
        {
            string logPath = CreateTemporaryLogPath();
            try
            {
                bool defaultThemeApplied = false;
                var log = new AppDiagnosticLog(logPath);
                MethodInfo method = typeof(App).GetMethod("LoadThemeWithFallback", BindingFlags.Static | BindingFlags.NonPublic);

                Assert.IsNotNull(method);
                method.Invoke(null, new object[]
                {
                    new Action(ThrowThemeLoadException),
                    new Action(delegate { defaultThemeApplied = true; }),
                    log
                });

                Assert.IsTrue(defaultThemeApplied);
                string content = File.ReadAllText(logPath);
                StringAssert.Contains(content, "启动主题加载");
                Assert.IsFalse(content.IndexOf("theme-api-key", StringComparison.Ordinal) >= 0);
            }
            finally
            {
                DeleteTemporaryDirectory(logPath);
            }
        }

        /// <summary>XMZADD 20260831 构造独立临时日志路径，避免专项测试写入当前用户的真实诊断目录。</summary>
        private static string CreateTemporaryLogPath()
        {
            return Path.Combine(Path.GetTempPath(), "SHB.EosDataDictionary.Tests", Guid.NewGuid().ToString("N"), "diagnostic.log");
        }

        /// <summary>XMZADD 20260831 模拟损坏主题资源在加载时抛出异常，以验证启动降级流程不会中断。</summary>
        private static void ThrowThemeLoadException()
        {
            throw new InvalidOperationException("主题资源损坏，ApiKey=theme-api-key");
        }

        /// <summary>XMZADD 20260831 删除单个测试创建的临时目录，确保后续测试不受历史诊断内容影响。</summary>
        private static void DeleteTemporaryDirectory(string logPath)
        {
            string directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
