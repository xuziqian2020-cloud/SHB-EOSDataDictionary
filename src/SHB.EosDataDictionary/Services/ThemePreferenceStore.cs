using System;
using System.IO;
using System.Security;
using System.Text;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 在当前 Windows 用户目录保存并恢复界面主题偏好。</summary>
    public sealed class ThemePreferenceStore
    {
        private readonly string _filePath;

        /// <summary>XMZADD 20260828 使用指定文件路径创建可测试的主题偏好存储。</summary>
        public ThemePreferenceStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("主题偏好文件路径不能为空。", "filePath");
            }
            _filePath = filePath;
        }

        /// <summary>XMZADD 20260828 读取上次主题选择，文件缺失或损坏时安全回退到深色主题。</summary>
        public AppTheme Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return AppTheme.Dark;
                }

                string value = File.ReadAllText(_filePath).Trim();
                AppTheme theme;
                if (Enum.TryParse(value, true, out theme))
                {
                    return theme;
                }
            }
            catch (IOException)
            {
                // 主题偏好不是业务数据，读取失败时必须优先保证数据字典可以正常启动。
            }
            catch (UnauthorizedAccessException)
            {
                // 主题偏好不是业务数据，权限不足时使用默认深色而不是中断主程序。
            }
            catch (SecurityException)
            {
                // Windows 安全策略禁止访问时不影响数据库字典核心功能。
            }
            return AppTheme.Dark;
        }

        /// <summary>XMZADD 20260828 保存用户主动选择的主题，确保下次启动保持一致视觉环境。</summary>
        public void Save(AppTheme theme)
        {
            try
            {
                string directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(_filePath, theme.ToString(), new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // 主题保存失败不能影响用户继续查询 EOS 数据字典。
            }
            catch (UnauthorizedAccessException)
            {
                // 只读用户目录仍允许本次会话切换主题，不强制持久化。
            }
            catch (SecurityException)
            {
                // Windows 安全策略限制写入时保留当前会话主题即可。
            }
        }
    }
}
