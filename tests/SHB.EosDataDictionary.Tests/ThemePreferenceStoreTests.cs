using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260828 验证浅深色主题偏好能够安全保存并在异常内容时回退深色。</summary>
    [TestClass]
    public sealed class ThemePreferenceStoreTests
    {
        /// <summary>XMZADD 20260828 验证首次运行没有偏好文件时默认使用深色主题。</summary>
        [TestMethod]
        public void Load_MissingFile_ReturnsDark()
        {
            string path = CreateTemporaryPath();
            ThemePreferenceStore store = new ThemePreferenceStore(path);

            Assert.AreEqual(AppTheme.Dark, store.Load());
        }

        /// <summary>XMZADD 20260828 验证保存浅色主题后可以从本地文件恢复。</summary>
        [TestMethod]
        public void Save_LightTheme_CanBeLoaded()
        {
            string path = CreateTemporaryPath();
            try
            {
                ThemePreferenceStore store = new ThemePreferenceStore(path);

                store.Save(AppTheme.Light);

                Assert.AreEqual(AppTheme.Light, store.Load());
            }
            finally
            {
                DeleteTemporaryDirectory(path);
            }
        }

        /// <summary>XMZADD 20260828 验证损坏的主题偏好不会阻止程序启动。</summary>
        [TestMethod]
        public void Load_InvalidValue_ReturnsDark()
        {
            string path = CreateTemporaryPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "无法识别的主题");
                ThemePreferenceStore store = new ThemePreferenceStore(path);

                Assert.AreEqual(AppTheme.Dark, store.Load());
            }
            finally
            {
                DeleteTemporaryDirectory(path);
            }
        }

        /// <summary>XMZADD 20260828 验证主题服务切换后同步保存用户选择。</summary>
        [TestMethod]
        public void Toggle_SwitchesThemeAndPersistsSelection()
        {
            string path = CreateTemporaryPath();
            try
            {
                ThemePreferenceStore store = new ThemePreferenceStore(path);
                ThemeService service = new ThemeService(store);

                Assert.AreEqual(AppTheme.Dark, service.LoadSavedTheme());
                Assert.AreEqual(AppTheme.Light, service.Toggle());
                Assert.AreEqual(AppTheme.Light, store.Load());
            }
            finally
            {
                DeleteTemporaryDirectory(path);
            }
        }

        /// <summary>XMZADD 20260828 验证主题路径不可读时不会阻止主程序启动。</summary>
        [TestMethod]
        public void Load_UnreadablePath_ReturnsDark()
        {
            string path = CreateTemporaryPath();
            try
            {
                Directory.CreateDirectory(path);
                ThemePreferenceStore store = new ThemePreferenceStore(path);

                Assert.AreEqual(AppTheme.Dark, store.Load());
            }
            finally
            {
                DeleteTemporaryDirectory(path);
            }
        }

        /// <summary>XMZADD 20260828 验证主题路径不可写时切换主题仍可继续使用。</summary>
        [TestMethod]
        public void Save_UnwritablePath_DoesNotThrow()
        {
            string path = CreateTemporaryPath();
            try
            {
                Directory.CreateDirectory(path);
                ThemePreferenceStore store = new ThemePreferenceStore(path);

                store.Save(AppTheme.Light);
            }
            finally
            {
                DeleteTemporaryDirectory(path);
            }
        }

        /// <summary>XMZADD 20260828 创建独立临时主题文件路径，避免测试污染用户配置。</summary>
        private static string CreateTemporaryPath()
        {
            return Path.Combine(Path.GetTempPath(), "SHB.EosDataDictionary.Tests", Guid.NewGuid().ToString("N"), "theme.txt");
        }

        /// <summary>XMZADD 20260828 删除单个测试创建的临时目录，保持测试环境可重复。</summary>
        private static void DeleteTemporaryDirectory(string filePath)
        {
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
