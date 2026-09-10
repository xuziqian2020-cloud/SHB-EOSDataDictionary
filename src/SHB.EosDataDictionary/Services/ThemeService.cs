using System;
using System.Windows;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 切换 WPF 颜色资源并持久化当前用户的浅深色主题选择。</summary>
    public sealed class ThemeService
    {
        private const string DarkThemeSource = "Themes/DarkTheme.xaml";
        private const string LightThemeSource = "Themes/LightTheme.xaml";
        private readonly ThemePreferenceStore _preferenceStore;

        /// <summary>XMZADD 20260828 使用指定偏好存储创建主题切换服务，便于测试和本机隔离。</summary>
        public ThemeService(ThemePreferenceStore preferenceStore)
        {
            if (preferenceStore == null)
            {
                throw new ArgumentNullException("preferenceStore");
            }
            _preferenceStore = preferenceStore;
            CurrentTheme = AppTheme.Dark;
        }

        public AppTheme CurrentTheme { get; private set; }

        /// <summary>XMZADD 20260828 读取并应用上次保存的主题，确保程序启动后视觉保持一致。</summary>
        public AppTheme LoadSavedTheme()
        {
            AppTheme theme = _preferenceStore.Load();
            Apply(theme);
            return theme;
        }

        /// <summary>XMZADD 20260828 在浅色和深色主题之间切换并保存用户选择。</summary>
        public AppTheme Toggle()
        {
            AppTheme theme = CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
            Apply(theme);
            _preferenceStore.Save(theme);
            return theme;
        }

        /// <summary>XMZADD 20260828 将指定主题颜色字典替换到当前 WPF 应用资源中。</summary>
        public void Apply(AppTheme theme)
        {
            CurrentTheme = theme;
            Application application = Application.Current;
            if (application == null)
            {
                return;
            }

            ResourceDictionary replacement = new ResourceDictionary();
            replacement.Source = new Uri(GetThemeSource(theme), UriKind.Relative);
            int themeIndex = FindThemeDictionaryIndex(application.Resources);
            if (themeIndex >= 0)
            {
                application.Resources.MergedDictionaries[themeIndex] = replacement;
            }
            else
            {
                // 主题颜色必须优先于控件样式合并，保证所有 DynamicResource 能解析到当前色板。
                application.Resources.MergedDictionaries.Insert(0, replacement);
            }
        }

        /// <summary>XMZADD 20260828 定位当前资源集合中的主题色板，避免替换共享控件样式。</summary>
        private static int FindThemeDictionaryIndex(ResourceDictionary resources)
        {
            for (int i = 0; i < resources.MergedDictionaries.Count; i++)
            {
                Uri source = resources.MergedDictionaries[i].Source;
                if (source == null)
                {
                    continue;
                }
                string value = source.OriginalString;
                if (value.IndexOf("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    value.IndexOf("LightTheme.xaml", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>XMZADD 20260828 将主题枚举映射为项目内颜色资源字典路径。</summary>
        private static string GetThemeSource(AppTheme theme)
        {
            return theme == AppTheme.Light ? LightThemeSource : DarkThemeSource;
        }
    }
}
