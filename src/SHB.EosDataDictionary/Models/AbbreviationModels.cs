using System.Collections.Generic;

namespace SHB.EosDataDictionary.Models
{
    /// <summary>XMZADD 20260901 保存共享缩写的中文含义、适用范围和可信状态。</summary>
    public sealed class AbbreviationEntry
    {
        /// <summary>XMZADD 20260901 初始化可供知识库扫描流程直接追加证据的缩写条目。</summary>
        public AbbreviationEntry()
        {
            Evidence = new List<AbbreviationEvidence>();
        }

        public string Abbreviation { get; set; }
        public string ChineseMeaning { get; set; }
        public string ModuleScope { get; set; }
        public string TableScope { get; set; }
        public int ConfidenceScore { get; set; }
        public ConfidenceStatus Status { get; set; }
        public IList<AbbreviationEvidence> Evidence { get; set; }
    }

    /// <summary>XMZADD 20260901 保存缩写结论的相对来源位置和可审阅短摘要。</summary>
    public sealed class AbbreviationEvidence
    {
        public string SourceType { get; set; }
        public string RelativePath { get; set; }
        public int LineNumber { get; set; }
        public string Summary { get; set; }
    }
}
