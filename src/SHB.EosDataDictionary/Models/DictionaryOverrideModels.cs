using System;

namespace SHB.EosDataDictionary.Models
{
    /// <summary>XMZADD 20260831 保存用户对字典解释的本地维护内容，确保任何自动刷新都不覆盖人工确认结果。</summary>
    public sealed class DictionaryOverride
    {
        public string ScopeKey { get; set; }
        public string ObjectName { get; set; }
        public string FieldName { get; set; }
        public string ObjectKey { get; set; }
        public string FieldKey { get; set; }
        public string PropertyName { get; set; }
        public string ManualValue { get; set; }
        public string OriginalAutomaticValue { get; set; }
        public bool IsLocked { get; set; }
        public bool IsOrphaned { get; set; }
        public string Remark { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
