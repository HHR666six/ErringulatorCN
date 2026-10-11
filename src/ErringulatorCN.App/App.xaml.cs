using System;
using System.Threading;
using System.Windows;

namespace ErringulatorCN.App
{
    public partial class App : Application
    {
        private Mutex _instanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单实例：两个实例同时对同一输出目录写入会互相覆盖，直接阻止。
            _instanceMutex = new Mutex(true, @"Local\ErringulatorCN_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "ErringulatorCN 已经在运行了。\n\n请切换到已打开的窗口；" +
                    "同时开两个实例去写同一个输出目录会互相覆盖。",
                    "程序已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { /* 未持有则忽略 */ }
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
