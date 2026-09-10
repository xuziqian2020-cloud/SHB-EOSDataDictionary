using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class ReadOnlySqlGuardTests
    {
        [TestMethod]
        public void Validate_AllowsRegisteredMetadataSelect()
        {
            ReadOnlySqlGuard.Validate("SELECT DB_NAME();");
        }

        /// <summary>XMZADD 20260901 验证只读保护允许从 SQL Server 分区统计读取近似行数。</summary>
        [TestMethod]
        public void Validate_AllowsPartitionStatisticsMetadataSelect()
        {
            const string sql = @"SELECT t.object_id, SUM(p.row_count)
FROM sys.tables t
LEFT JOIN sys.dm_db_partition_stats p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
GROUP BY t.object_id;";

            ReadOnlySqlGuard.Validate(sql);
            Assert.IsTrue(ReadOnlySqlGuard.IsReadOnly(sql));
        }

        [TestMethod]
        public void Validate_RejectsWriteStatement()
        {
            Assert.ThrowsException<System.InvalidOperationException>(
                () => ReadOnlySqlGuard.Validate("UPDATE dbo.UserTable SET Name = 'x';"));
        }

        /// <summary>XMZADD 20260828 验证 SELECT INTO 不能借结构查询入口创建数据库对象。</summary>
        [TestMethod]
        public void Validate_RejectsSelectIntoStatement()
        {
            Assert.ThrowsException<System.InvalidOperationException>(
                () => ReadOnlySqlGuard.Validate("SELECT * INTO dbo.CopyTable FROM sys.tables;"));
        }

        /// <summary>XMZADD 20260828 验证完整 EXECUTE 关键字不能绕过只读命令限制。</summary>
        [TestMethod]
        public void Validate_RejectsExecuteStatement()
        {
            Assert.ThrowsException<System.InvalidOperationException>(
                () => ReadOnlySqlGuard.Validate("EXECUTE dbo.WriteProcedure;"));
        }

        /// <summary>XMZADD 20260828 验证一次只允许执行一条结构查询，避免追加隐藏语句。</summary>
        [TestMethod]
        public void Validate_RejectsMultipleStatements()
        {
            Assert.ThrowsException<System.InvalidOperationException>(
                () => ReadOnlySqlGuard.Validate("SELECT DB_NAME(); SELECT SUSER_SNAME();"));
        }

        /// <summary>XMZADD 20260828 验证结构查询不能推进数据库序列值。</summary>
        [TestMethod]
        public void Validate_RejectsSequenceMutation()
        {
            Assert.ThrowsException<System.InvalidOperationException>(
                () => ReadOnlySqlGuard.Validate("SELECT NEXT VALUE FOR dbo.OrderSequence;"));
        }
    }
}
