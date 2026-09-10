using System;
using System.IO;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 统一管理 EOS 数据字典的默认源码和本机数据路径。</summary>
    public static class AppPathService
    {
        private const string SourceRootEnvironmentVariable = "EOS_SOURCE_ROOT";
        private static string _localDataDirectoryOverrideForTesting;

        /// <summary>XMZADD 20260910 从当前进程配置读取 EOS 源码根目录，避免公开构建绑定开发机路径。</summary>
        public static string GetDefaultSourceRoot()
        {
            string path = Environment.GetEnvironmentVariable(
                SourceRootEnvironmentVariable,
                EnvironmentVariableTarget.Process);
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
        }

        /// <summary>XMZADD 20260908 为程序集内部界面测试切换独立数据目录，防止测试触碰用户真实字典。</summary>
        internal static void SetLocalDataDirectoryOverrideForTesting(string path)
        {
            _localDataDirectoryOverrideForTesting = string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetFullPath(path);
        }

        /// <summary>XMZADD 20260828 获取当前 Windows 用户保存连接和快照的目录。</summary>
        public static string GetLocalDataDirectory()
        {
            string path = string.IsNullOrWhiteSpace(_localDataDirectoryOverrideForTesting)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SHB", "EosDataDictionary")
                : _localDataDirectoryOverrideForTesting;
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>XMZADD 20260828 获取本机连接配置和元数据快照数据库路径。</summary>
        public static string GetLocalDatabasePath()
        {
            return Path.Combine(GetLocalDataDirectory(), "dictionary.db");
        }

        /// <summary>XMZADD 20260828 获取当前用户保存浅深色主题偏好的文件路径。</summary>
        public static string GetThemePreferencePath()
        {
            return Path.Combine(GetLocalDataDirectory(), "theme.txt");
        }

        /// <summary>XMZADD 20260831 获取本机异常诊断日志路径，便于程序异常后保留排查依据而不依赖 EOS 数据库。</summary>
        public static string GetDiagnosticLogPath()
        {
            try
            {
                return Path.Combine(GetLocalDataDirectory(), "diagnostic.log");
            }
            catch (IOException)
            {
                // 用户目录暂不可写时仍需保留最小诊断能力，避免日志路径本身造成启动失败。
                return Path.Combine(Path.GetTempPath(), "SHB", "EosDataDictionary", "diagnostic.log");
            }
            catch (UnauthorizedAccessException)
            {
                // 受限桌面环境优先让主程序启动，异常日志可退回到当前用户可写的临时目录。
                return Path.Combine(Path.GetTempPath(), "SHB", "EosDataDictionary", "diagnostic.log");
            }
        }
    }
}
