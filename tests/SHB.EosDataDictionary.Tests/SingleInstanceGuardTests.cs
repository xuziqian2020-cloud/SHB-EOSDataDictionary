using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证重复启动不会同时加载多份超大本地快照。</summary>
    [TestClass]
    public sealed class SingleInstanceGuardTests
    {
        /// <summary>XMZADD 20260831 验证相同实例名称在同一时刻只能有一个主实例。</summary>
        [TestMethod]
        public void Acquire_SameName_AllowsOnlyFirstInstance()
        {
            string name = "Local\\SHB.EosDataDictionary.Tests." + Guid.NewGuid().ToString("N");
            using (var first = new SingleInstanceGuard(name))
            using (var second = new SingleInstanceGuard(name))
            {
                Assert.IsTrue(first.IsPrimaryInstance);
                Assert.IsFalse(second.IsPrimaryInstance);
            }
        }
    }
}
