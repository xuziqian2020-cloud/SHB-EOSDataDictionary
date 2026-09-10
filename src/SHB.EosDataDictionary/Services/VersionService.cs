using System;
using System.Reflection;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 生成主窗口固定显示的程序版本文本。</summary>
    public static class VersionService
    {
        /// <summary>XMZADD 20260828 返回公司级数据字典的版本和版权信息。</summary>
        public static string GetDisplayText()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            string versionText = version == null || version.Major == 0 ? "V1.0.0" : "V" + version.Major + "." + version.Minor + "." + version.Build;
            return "Version " + versionText + " © 2026 XMZ";
        }
    }
}
