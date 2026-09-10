using System;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class SqlMetadataReaderTests
    {
        [TestMethod]
        public void FormatType_UsesSqlServerLengthAndPrecision()
        {
            Assert.AreEqual("nvarchar(50)", SqlTypeFormatter.Format("nvarchar", 100, 0, 0));
            Assert.AreEqual("nvarchar(MAX)", SqlTypeFormatter.Format("nvarchar", -1, 0, 0));
            Assert.AreEqual("decimal(18,2)", SqlTypeFormatter.Format("decimal", 0, 18, 2));
        }

        /// <summary>XMZADD 20260831 确保对象和字段元数据只读取标准描述属性，避免设计器属性展开重复行。</summary>
        [TestMethod]
        public void MetadataQueries_OnlyReadSqlServerDescriptionProperty()
        {
            string objectSql = GetPrivateQuery("ObjectSql");
            string columnSql = GetPrivateQuery("ColumnSql");

            StringAssert.Contains(objectSql, "AND ep.name = 'MS_Description'");
            StringAssert.Contains(columnSql, "AND ep.name = 'MS_Description'");
        }

        /// <summary>XMZADD 20260901 验证对象扫描只读取用户表并通过分区统计取得近似行数。</summary>
        [TestMethod]
        public void ObjectQuery_ReadsOnlyUserTablesAndApproximateRowCount()
        {
            string objectSql = GetPrivateQuery("ObjectSql");

            StringAssert.Contains(objectSql, "FROM sys.tables");
            StringAssert.Contains(objectSql, "INNER JOIN sys.schemas");
            StringAssert.Contains(objectSql, "sys.dm_db_partition_stats");
            StringAssert.Contains(objectSql, "index_id IN (0, 1)");
            StringAssert.Contains(objectSql, "ApproximateRowCount");
            Assert.IsFalse(objectSql.Contains("sys.objects"));
            Assert.IsFalse(objectSql.Contains("type IN ('U', 'V')"));
            Assert.IsTrue(ReadOnlySqlGuard.IsReadOnly(objectSql));
        }

        /// <summary>XMZADD 20260901 验证读取器注册的全部结构 SQL 都保持只读。</summary>
        [TestMethod]
        public void MetadataQueries_AllPassReadOnlyGuard()
        {
            string[] fieldNames = { "ObjectSql", "ColumnSql", "ForeignKeySql", "UniqueColumnSql" };

            for (int index = 0; index < fieldNames.Length; index++)
            {
                string sql = GetPrivateQuery(fieldNames[index]);
                Assert.IsTrue(ReadOnlySqlGuard.IsReadOnly(sql), fieldNames[index] + " 必须保持只读。");
            }
        }

        /// <summary>XMZADD 20260828 验证预取消会在建立数据库连接前终止结构读取。</summary>
        [TestMethod]
        public void Read_PreCancelled_DoesNotOpenSqlConnection()
        {
            var profile = new ConnectionProfile
            {
                Server = "invalid.example.local",
                Database = "EOS_INVALID"
            };
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsException<OperationCanceledException>(
                () => new SqlServerMetadataReader().Read(profile, cancellation.Token));
        }

        /// <summary>XMZADD 20260901 验证真实读取器把 SQL Server 扩展说明标记为无连接信息的数据库证据。</summary>
        [TestMethod]
        public void CreateDatabaseValue_AddsSafeDatabaseEvidenceMarker()
        {
            MethodInfo method = typeof(SqlServerMetadataReader).GetMethod(
                "CreateDatabaseValue", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var value = (MetadataValue)method.Invoke(null, new object[] { "订单名称" });

            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, value.Status);
            Assert.AreEqual(1, value.Evidence.Count);
            Assert.AreEqual("数据库说明", value.Evidence[0].SourceType);
            Assert.AreEqual(string.Empty, value.Evidence[0].SourcePath);
            Assert.AreEqual("SqlExtendedDescription", value.Evidence[0].RuleName);
            Assert.IsNull(value.Evidence[0].RawValue);
            Assert.IsNull(value.Evidence[0].OriginalText);
        }

        /// <summary>XMZADD 20260831 读取数据库读取器中的私有查询文本供只读 SQL 约束回归测试使用。</summary>
        private static string GetPrivateQuery(string fieldName)
        {
            FieldInfo field = typeof(SqlServerMetadataReader).GetField(
                fieldName,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field);
            return field.GetValue(null) as string;
        }
    }
}
