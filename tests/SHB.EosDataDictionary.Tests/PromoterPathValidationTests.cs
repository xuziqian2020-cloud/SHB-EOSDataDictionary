using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Promoter;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class PromoterPathValidationTests
    {
        /// <summary>XMZADD 20260917 验证快照发布器接受 Git worktree 使用文件形式保存的 .git 元数据入口。</summary>
        [TestMethod]
        public void ValidatePaths_GitWorktreeMetadataFile_IsAccepted()
        {
            string root = Path.Combine(Path.GetTempPath(), "eos-promoter-worktree-" + Guid.NewGuid().ToString("N"));
            string sourceDatabasePath = Path.Combine(root, "candidate.db");
            string outputDatabasePath = Path.Combine(root, "promoted.db");
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "snapshot"));
                File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../.git/worktrees/test");
                File.WriteAllText(Path.Combine(root, "snapshot", "manifest.json"), "{}");
                File.WriteAllBytes(sourceDatabasePath, new byte[] { 1 });

                Program.ValidatePaths(sourceDatabasePath, outputDatabasePath, root);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }
    }
}
