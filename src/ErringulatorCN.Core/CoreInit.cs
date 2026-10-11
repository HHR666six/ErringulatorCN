using System.Runtime.CompilerServices;
using System.Text;

namespace ErringulatorCN.Core
{
    /// <summary>
    /// 程序集加载时的初始化：注册 CodePages 编码提供程序，
    /// 否则 SoulsFormats 的 shift-jis 编码在 .NET Core 上不可用。
    /// </summary>
    internal static class CoreInit
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch
            {
                // 忽略：部分精简运行时不支持 CodePages
            }
        }
    }
}
