using System;
using System.Data.SqlClient;
using System.Threading.Tasks;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 使用最小只读查询验证 EOS SQL Server 连接并返回实际数据库名。</summary>
    public sealed class SqlServerConnectionProbe
    {
        private const string ProbeSql = "SELECT DB_NAME();";

        /// <summary>XMZADD 20260828 验证连接配置可访问目标数据库，但不读取业务表或源码。</summary>
        public async Task<string> TestAsync(ConnectionProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            // 连接验证也必须通过只读守卫，避免短连接被误改成写库语句。
            ReadOnlySqlGuard.Validate(ProbeSql);
            using (var connection = new SqlConnection(profile.BuildConnectionString()))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (var command = new SqlCommand(ProbeSql, connection))
                {
                    object result = await command.ExecuteScalarAsync().ConfigureAwait(false);
                    string databaseName = Convert.ToString(result);
                    if (string.IsNullOrWhiteSpace(databaseName))
                    {
                        throw new InvalidOperationException("连接成功，但 SQL Server 未返回当前数据库名。");
                    }
                    return databaseName;
                }
            }
        }
    }
}
