using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using System.Data.SqlClient;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class ConnectionProfileTests
    {
        [TestMethod]
        public void BuildConnectionString_SupportsWindowsAuthentication()
        {
            var profile = new ConnectionProfile
            {
                Server = "192.0.2.23",
                Port = 1433,
                Database = "EOS_TEST",
                AuthenticationMode = AuthenticationMode.Windows
            };

            string connectionString = profile.BuildConnectionString();

            Assert.IsTrue(connectionString.Contains("Integrated Security=True"));
            Assert.IsTrue(connectionString.Contains("192.0.2.23,1433"));
            Assert.IsTrue(connectionString.Contains("Initial Catalog=EOS_TEST"));
        }

        /// <summary>XMZADD 20260828 验证只读连接明确向 SQL Server 声明只读访问意图。</summary>
        [TestMethod]
        public void BuildConnectionString_AlwaysUsesReadOnlyApplicationIntent()
        {
            var profile = new ConnectionProfile
            {
                Server = "192.0.2.23",
                Database = "EOS_TEST",
                IsReadOnly = false
            };

            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString());

            Assert.AreEqual(ApplicationIntent.ReadOnly, builder.ApplicationIntent);
        }
    }
}
