using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260910 验证公开构建通过本机环境读取 EOS 源码目录而不携带开发机路径。</summary>
    [TestClass]
    [DoNotParallelize]
    public sealed class AppPathServiceTests
    {
        /// <summary>XMZADD 20260910 验证未配置本机源码目录时保持空值并交由用户选择。</summary>
        [TestMethod]
        public void GetDefaultSourceRoot_EnvironmentMissing_ReturnsEmpty()
        {
            string previous = Environment.GetEnvironmentVariable("EOS_SOURCE_ROOT", EnvironmentVariableTarget.Process);
            try
            {
                Environment.SetEnvironmentVariable("EOS_SOURCE_ROOT", null, EnvironmentVariableTarget.Process);

                Assert.AreEqual(string.Empty, AppPathService.GetDefaultSourceRoot());
            }
            finally
            {
                Environment.SetEnvironmentVariable("EOS_SOURCE_ROOT", previous, EnvironmentVariableTarget.Process);
            }
        }

        /// <summary>XMZADD 20260910 验证本机显式配置的源码目录被规范化读取。</summary>
        [TestMethod]
        public void GetDefaultSourceRoot_EnvironmentConfigured_ReturnsTrimmedValue()
        {
            string previous = Environment.GetEnvironmentVariable("EOS_SOURCE_ROOT", EnvironmentVariableTarget.Process);
            try
            {
                Environment.SetEnvironmentVariable("EOS_SOURCE_ROOT", "  configured-source-root  ", EnvironmentVariableTarget.Process);

                Assert.AreEqual("configured-source-root", AppPathService.GetDefaultSourceRoot());
            }
            finally
            {
                Environment.SetEnvironmentVariable("EOS_SOURCE_ROOT", previous, EnvironmentVariableTarget.Process);
            }
        }
    }
}
