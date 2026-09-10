using System;
using System.Threading;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260831 使用当前 Windows 会话命名互斥体阻止重复实例同时加载超大本地快照。</summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        /// <summary>XMZADD 20260831 获取指定程序实例名称的唯一所有权，并公开当前进程是否为主实例。</summary>
        public SingleInstanceGuard(string instanceName)
        {
            if (string.IsNullOrWhiteSpace(instanceName))
            {
                throw new ArgumentException("实例名称不能为空。", "instanceName");
            }
            bool createdNew;
            _mutex = new Mutex(true, instanceName, out createdNew);
            IsPrimaryInstance = createdNew;
        }

        public bool IsPrimaryInstance { get; private set; }

        /// <summary>XMZADD 20260831 释放主实例互斥体，使程序正常退出后可再次启动。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (IsPrimaryInstance)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // 进程退出阶段若所有权已由系统释放，无需影响正常关闭。
                }
            }
            _mutex.Dispose();
        }
    }
}
