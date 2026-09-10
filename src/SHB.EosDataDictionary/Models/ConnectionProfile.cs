using System;
using System.Data.SqlClient;

namespace SHB.EosDataDictionary.Models
{
    /// <summary>XMZADD 20260828 表示 SQL Server 连接的认证方式。</summary>
    public enum AuthenticationMode
    {
        Windows,
        SqlServer
    }

    /// <summary>XMZADD 20260828 保存 EOS 数据库连接配置并生成作用域标识。</summary>
    public sealed class ConnectionProfile
    {
        public ConnectionProfile()
        {
            Id = Guid.NewGuid().ToString("N");
            Port = 1433;
            AuthenticationMode = AuthenticationMode.Windows;
            IsReadOnly = true;
            TrustServerCertificate = true;
        }

        public string Id { get; set; }
        public string Name { get; set; }
        public string Server { get; set; }
        public int Port { get; set; }
        public string Database { get; set; }
        public AuthenticationMode AuthenticationMode { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
        public bool TrustServerCertificate { get; set; }
        public bool IsReadOnly { get; set; }

        public string ScopeKey
        {
            get { return (Id ?? string.Empty) + "|" + (Database ?? string.Empty); }
        }

        /// <summary>XMZADD 20260828 生成供结构读取使用的 SQL Server 连接字符串。</summary>
        public string BuildConnectionString()
        {
            if (string.IsNullOrWhiteSpace(Server) || string.IsNullOrWhiteSpace(Database))
            {
                throw new InvalidOperationException("服务器和数据库不能为空。");
            }

            string dataSource = Server.Trim();
            if (Port > 0 && dataSource.IndexOf(",", StringComparison.Ordinal) < 0 &&
                dataSource.IndexOf("\\", StringComparison.Ordinal) < 0)
            {
                dataSource = dataSource + "," + Port;
            }

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = dataSource,
                InitialCatalog = Database.Trim(),
                ConnectTimeout = 8,
                ApplicationName = "SHB EOS Data Dictionary",
                // EOS 数据字典的连接用途固定为结构读取，历史连接配置也不能降级成读写意图。
                ApplicationIntent = ApplicationIntent.ReadOnly,
                TrustServerCertificate = TrustServerCertificate,
                PersistSecurityInfo = false
            };

            if (AuthenticationMode == AuthenticationMode.Windows)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.UserID = UserName ?? string.Empty;
                builder.Password = Password ?? string.Empty;
            }

            return builder.ConnectionString;
        }
    }
}
