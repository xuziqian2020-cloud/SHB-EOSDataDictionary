using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证用户调整的数据字典列宽会持久化到本机。</summary>
    [TestClass]
    public sealed class ColumnLayoutStoreTests
    {
        /// <summary>XMZADD 20260831 验证同一列表的多列宽度可在重新打开程序后恢复。</summary>
        [TestMethod]
        public void SaveAndLoad_RestoresColumnWidths()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-column-layout-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new ColumnLayoutStore(path);
                store.Save("TablesGrid", "ChineseName", 240D);
                store.Save("TablesGrid", "ObjectName", 300D);

                Assert.AreEqual(240D, store.Load("TablesGrid")["ChineseName"], 0.01D);
                Assert.AreEqual(300D, new ColumnLayoutStore(path).Load("TablesGrid")["ObjectName"], 0.01D);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
