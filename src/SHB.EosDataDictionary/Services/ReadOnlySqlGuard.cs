using System;
using System.Text.RegularExpressions;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 阻止数据字典执行会修改 EOS 数据库的 SQL。</summary>
    public static class ReadOnlySqlGuard
    {
        /// <summary>XMZADD 20260901 判断结构 SQL 是否满足只读约束，供发布前校验和自动测试复用。</summary>
        public static bool IsReadOnly(string sql)
        {
            try
            {
                Validate(sql);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>XMZADD 20260828 校验结构查询只能包含一条 SELECT 或 WITH 只读语句。</summary>
        public static void Validate(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new InvalidOperationException("结构查询不能为空。");
            }

            string withoutComments = Regex.Replace(sql, @"--[^\r\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            string statement = withoutComments.Trim();
            if (statement.EndsWith(";", StringComparison.Ordinal))
            {
                statement = statement.Substring(0, statement.Length - 1).TrimEnd();
            }
            if (statement.IndexOf(';') >= 0)
            {
                throw new InvalidOperationException("数据字典一次只允许执行一条结构查询。");
            }

            string normalized = statement.ToUpperInvariant();
            if (!normalized.StartsWith("SELECT", StringComparison.Ordinal) &&
                !normalized.StartsWith("WITH", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("数据字典只允许执行只读结构查询。");
            }

            if (Regex.IsMatch(normalized, @"\b(INSERT|UPDATE|DELETE|MERGE|EXEC(?:UTE)?|ALTER|DROP|TRUNCATE|CREATE|INTO|GRANT|REVOKE|DENY|BACKUP|RESTORE|DBCC|BULK|OPENROWSET|OPENQUERY)\b") ||
                Regex.IsMatch(normalized, @"\bNEXT\s+VALUE\s+FOR\b"))
            {
                throw new InvalidOperationException("检测到非只读 SQL，已阻止执行。");
            }
        }
    }
}
