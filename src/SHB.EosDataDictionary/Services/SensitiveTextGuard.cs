using System;
using System.Text.RegularExpressions;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 统一识别 AI 上下文与公开事件中的凭据赋值形态，避免各发布边界规则漂移。</summary>
    internal static class SensitiveTextGuard
    {
        private static readonly Regex SensitiveAssignmentRegex = new Regex(
            @"(?:password|pwd|token|access[-_\s]*token|client[-_\s]*secret|secret|api[-_\s]*key|authorization|server|database|connection[-_\s]*string|data[-_\s]*source|initial[-_\s]*catalog|user[-_\s]*id|uid)\s*[:=]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BearerTokenRegex = new Regex(
            @"bearer\s+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>XMZADD 20260901 保守判断文本是否包含常见密钥、认证头或数据库连接赋值，不回显敏感原文。</summary>
        internal static bool ContainsCredentialShape(string value)
        {
            string text = value ?? string.Empty;
            return SensitiveAssignmentRegex.IsMatch(text) || BearerTokenRegex.IsMatch(text);
        }
    }
}
