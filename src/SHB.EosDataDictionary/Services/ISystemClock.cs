using System;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 抽象同步调度所需 UTC 时间，使离线退避规则可确定性验证。</summary>
    public interface ISystemClock
    {
        DateTime UtcNow { get; }
    }

    /// <summary>XMZADD 20260901 提供正式同步流程使用的系统 UTC 时间。</summary>
    public sealed class SystemClock : ISystemClock
    {
        public DateTime UtcNow
        {
            get { return DateTime.UtcNow; }
        }
    }
}
