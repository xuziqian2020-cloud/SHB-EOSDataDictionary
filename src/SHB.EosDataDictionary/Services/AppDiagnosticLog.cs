using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260831 将桌面程序异常写入本机脱敏日志，支持排查闪退且不泄露连接和 AI 凭据。</summary>
    public sealed class AppDiagnosticLog
    {
        private const int DefaultMaximumFileBytes = 1024 * 1024;
        private const int DefaultMaximumEntryCharacters = 12000;
        private readonly string _logPath;
        private readonly int _maximumFileBytes;
        private readonly int _maximumEntryCharacters;

        /// <summary>XMZADD 20260831 使用默认容量创建诊断日志，避免异常信息无限增长占用用户本机磁盘。</summary>
        public AppDiagnosticLog(string logPath)
            : this(logPath, DefaultMaximumFileBytes, DefaultMaximumEntryCharacters)
        {
        }

        /// <summary>XMZADD 20260831 使用指定容量创建诊断日志，支持隔离测试验证日志截断和追加规则。</summary>
        public AppDiagnosticLog(string logPath, int maximumFileBytes, int maximumEntryCharacters)
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                throw new ArgumentException("诊断日志路径不能为空。", "logPath");
            }

            _logPath = logPath;
            _maximumFileBytes = maximumFileBytes > 0 ? maximumFileBytes : DefaultMaximumFileBytes;
            _maximumEntryCharacters = maximumEntryCharacters > 0 ? maximumEntryCharacters : DefaultMaximumEntryCharacters;
        }

        /// <summary>XMZADD 20260831 记录未处理异常并始终脱敏，防止排查闪退时把数据库密码或 AI 密钥写入本机明文日志。</summary>
        public void WriteUnhandledException(string source, Exception exception)
        {
            string detail = exception == null ? string.Empty : exception.ToString();
            Write(source, detail);
        }

        /// <summary>XMZADD 20260831 写入已脱敏的诊断条目，日志写入失败也不允许二次异常阻断主程序退出或恢复。</summary>
        public void Write(string source, string detail)
        {
            try
            {
                string directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string entry = BuildEntry(source, detail);
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length + Encoding.UTF8.GetByteCount(entry) > _maximumFileBytes)
                {
                    // 最新异常对定位本次闪退更有价值，因此容量达到上限时从空文件重新记录。
                    File.WriteAllText(_logPath, string.Empty, new UTF8Encoding(false));
                }

                File.AppendAllText(_logPath, entry, new UTF8Encoding(false));
            }
            catch
            {
                // 诊断日志是辅助能力，文件权限或磁盘异常不能再引发新的应用异常。
            }
        }

        /// <summary>XMZADD 20260831 对异常信息中的密钥、密码、认证头和连接字符串进行脱敏，保证诊断文件不成为敏感信息副本。</summary>
        public static string Sanitize(string value)
        {
            string result = value ?? string.Empty;
            result = Regex.Replace(result, "(?im)(connection\\s*string)\\s*[:=]\\s*[^\\r\\n]*", "$1=[已脱敏]");
            result = Regex.Replace(result, "(?im)(authorization)\\s*[:=]\\s*(bearer\\s+)?[^;\\r\\n\\s]+", "$1=[已脱敏]");
            result = Regex.Replace(result, "(?im)(bearer)\\s+[^;\\r\\n\\s]+", "$1 [已脱敏]");
            result = Regex.Replace(result, "(?im)(api[\\s_-]*key|password|pwd)\\s*[:=]\\s*[^;\\r\\n\\s]+", "$1=[已脱敏]");
            return result;
        }

        /// <summary>XMZADD 20260831 组合受容量限制的本机诊断条目，使异常时间、来源和堆栈可供人工定位。</summary>
        private string BuildEntry(string source, string detail)
        {
            string safeDetail = Sanitize(detail);
            if (safeDetail.Length > _maximumEntryCharacters)
            {
                safeDetail = safeDetail.Substring(0, _maximumEntryCharacters) + Environment.NewLine + "[异常内容已截断]";
            }

            var builder = new StringBuilder();
            builder.AppendLine("[异常时间] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("[来源] " + Sanitize(source));
            builder.AppendLine("[详情]");
            builder.AppendLine(safeDetail);
            builder.AppendLine(new string('-', 72));
            return builder.ToString();
        }
    }
}
