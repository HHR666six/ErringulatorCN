// 极简 minidump 解析器：读取 x64 崩溃转储的异常流与模块表，
// 输出异常代码、出错地址、RIP 与对应模块。
// 附加：<dump> <exe> 时对比 RIP 处「转储内内存字节」与「磁盘 exe 原始字节」，
// 判断该处代码是否被运行时改写（被改 = 某个注入层在运行期 patch 了代码）。
// 用法：DumpProbe <dump> [dump...]；DumpProbe <dump> -exe <eldenring.exe>
using System.Text;

if (args.Length >= 3 && args[1] == "-exe")
{
    CompareWithExe(args[0], args[2]);
    return 0;
}

foreach (string path in args)
{
    Console.WriteLine("==================================================");
    Console.WriteLine("DUMP: " + path);
    try
    {
        Probe(path);
    }
    catch (Exception ex)
    {
        Console.WriteLine("  ERROR: " + ex.Message);
    }
}
return 0;

static void CompareWithExe(string dumpPath, string exePath)
{
    byte[] d = File.ReadAllBytes(dumpPath);
    byte[] exe = File.ReadAllBytes(exePath);
    int nStreams = BitConverter.ToInt32(d, 8);
    int dirRva = BitConverter.ToInt32(d, 12);

    uint excCode = 0; ulong excAddr = 0; ulong rspForScan = 0;
    var modules = new List<(ulong Base, uint Size, string Name)>();
    var memRanges = new List<(ulong Start, ulong Size, int Rva)>();

    for (int i = 0; i < nStreams; i++)
    {
        int e = dirRva + i * 12;
        uint type = BitConverter.ToUInt32(d, e);
        uint rva = BitConverter.ToUInt32(d, e + 8);
        if (type == 4)
        {
            int n = BitConverter.ToInt32(d, (int)rva);
            for (int m = 0; m < n; m++)
            {
                int mo = (int)rva + 4 + m * 108;
                uint nameRva = BitConverter.ToUInt32(d, mo + 20);
                string name = "";
                if (nameRva != 0)
                {
                    int len = BitConverter.ToInt32(d, (int)nameRva);
                    name = Encoding.Unicode.GetString(d, (int)nameRva + 4, len);
                }
                modules.Add((BitConverter.ToUInt64(d, mo), BitConverter.ToUInt32(d, mo + 8), name));
            }
        }
        else if (type == 6)
        {
            excCode = BitConverter.ToUInt32(d, (int)rva + 8);
            excAddr = BitConverter.ToUInt64(d, (int)rva + 8 + 16);
            // thread context -> RSP (x64: 0x98) for stack scan
            int ctxDesc = (int)rva + 8 + 32 + 15 * 8;
            uint ctxSize = BitConverter.ToUInt32(d, ctxDesc);
            int ctxOff = BitConverter.ToInt32(d, ctxDesc + 4);
            if (ctxOff != 0 && ctxSize >= 0x100)
                rspForScan = BitConverter.ToUInt64(d, ctxOff + 0x98);
        }
        else if (type == 5) // MemoryListStream (32-bit RVAs)
        {
            int n = BitConverter.ToInt32(d, (int)rva);
            for (int k = 0; k < n; k++)
            {
                int o = (int)rva + 4 + k * 16;
                memRanges.Add((BitConverter.ToUInt64(d, o), BitConverter.ToUInt32(d, o + 8), BitConverter.ToInt32(d, o + 12)));
            }
        }
        else if (type == 9) // Memory64ListStream
        {
            ulong n = BitConverter.ToUInt64(d, (int)rva);
            ulong baseRva = BitConverter.ToUInt64(d, (int)rva + 8);
            int p = (int)rva + 16;
            for (ulong k = 0; k < n; k++)
            {
                ulong start = BitConverter.ToUInt64(d, p);
                ulong size = BitConverter.ToUInt64(d, p + 8);
                memRanges.Add((start, size, (int)baseRva));
                baseRva += size;
                p += 16;
            }
        }
    }

    Console.WriteLine($"dump: {dumpPath}");
    Console.WriteLine($"exception 0x{excCode:X8} at {excAddr:X16}");

    // find memory range containing excAddr
    foreach (var mr in memRanges)
    {
        if (excAddr >= mr.Start && excAddr < mr.Start + mr.Size)
        {
            int off = mr.Rva + (int)(excAddr - mr.Start);
            var sb = new StringBuilder();
            for (int k = 0; k < 32 && off + k < d.Length; k++) sb.Append(d[off + k].ToString("X2")).Append(' ');
            Console.WriteLine("dump bytes @RIP : " + sb);

            // RVA within the module that contains the exception address
            var main = modules.FirstOrDefault(m => excAddr >= m.Base && excAddr < m.Base + m.Size);
            if (main.Size > 0)
            {
                long rvaInExe = (long)excAddr - (long)main.Base;
                long fileOff = RvaToOffset(exe, rvaInExe);
                if (fileOff >= 0)
                {
                    var sb2 = new StringBuilder();
                    for (int k = 0; k < 32 && fileOff + k < exe.Length; k++) sb2.Append(exe[fileOff + k].ToString("X2")).Append(' ');
                    Console.WriteLine($"exe bytes  @RVA {rvaInExe:X} : " + sb2);
                    bool same = true;
                    for (int k = 0; k < 32; k++) if (d[off + k] != exe[fileOff + k]) { same = false; break; }
                    Console.WriteLine(same ? "=> code NOT modified at crash site" : "=> !!! code MODIFIED at runtime (patched by an injection) !!!");
                }
            }

            // ---- 简易栈回溯：扫描崩溃线程栈上的返回地址，标注所属模块 ----
            if (rspForScan != 0)
            {
                var sr = memRanges.FirstOrDefault(m => rspForScan >= m.Start && rspForScan < m.Start + m.Size);
                if (sr.Size > 0)
                {
                    Console.WriteLine("---- stack scan (return-address candidates, first 40) ----");
                    int shown = 0;
                    int stackOff = sr.Rva + (int)(rspForScan - sr.Start);
                    for (int k = 0; k < 0x4000 && shown < 40; k += 8)
                    {
                        if (stackOff + k + 8 > d.Length) break;
                        ulong v = BitConverter.ToUInt64(d, stackOff + k);
                        if (v == 0) continue;
                        foreach (var m in modules)
                        {
                            if (v > m.Base && v < m.Base + m.Size)
                            {
                                Console.WriteLine($"  [rsp+0x{k:X3}] {v:X16}  {Path.GetFileName(m.Name)}+0x{v - m.Base:X}");
                                shown++;
                                break;
                            }
                        }
                    }
                }
                else Console.WriteLine("(stack memory not present in dump)");
            }
            return;
        }
    }
    Console.WriteLine("RIP memory not present in dump");
}

static long RvaToOffset(byte[] pe, long rva)
{
    int peOff = BitConverter.ToInt32(pe, 0x3C);
    int numSec = BitConverter.ToUInt16(pe, peOff + 6);
    int optSize = BitConverter.ToUInt16(pe, peOff + 20);
    int secOff = peOff + 24 + optSize;
    for (int i = 0; i < numSec; i++)
    {
        int s = secOff + 40 * i;
        uint vsize = BitConverter.ToUInt32(pe, s + 8);
        uint vaddr = BitConverter.ToUInt32(pe, s + 12);
        uint rawSize = BitConverter.ToUInt32(pe, s + 16);
        uint rawPtr = BitConverter.ToUInt32(pe, s + 20);
        if (rva >= vaddr && rva < vaddr + Math.Max(vsize, rawSize))
            return rawPtr + (rva - vaddr);
    }
    return -1;
}

static void Probe(string path)
{
    byte[] d = File.ReadAllBytes(path);
    if (BitConverter.ToUInt32(d, 0) != 0x504D444D) { Console.WriteLine("  not a minidump"); return; }

    uint crashTime = BitConverter.ToUInt32(d, 20);
    Console.WriteLine($"  dump TimeDateStamp: {DateTimeOffset.FromUnixTimeSeconds(crashTime).ToLocalTime():yyyy-MM-dd HH:mm:ss} (local)");

    int nStreams = BitConverter.ToInt32(d, 8);
    int dirRva = BitConverter.ToInt32(d, 12);

    // module list
    var modules = new List<(ulong Base, uint Size, string Name)>();
    // exception
    uint excCode = 0, nParams = 0; ulong excAddr = 0;
    ulong[] excInfo = new ulong[15];
    bool hasExc = false;
    uint threadId = 0, procId = 0, procCreate = 0; int miscFlags = 0;

    for (int i = 0; i < nStreams; i++)
    {
        int e = dirRva + i * 12;
        uint type = BitConverter.ToUInt32(d, e);
        uint rva = BitConverter.ToUInt32(d, e + 8);
        if (type == 4) // ModuleList
        {
            int n = BitConverter.ToInt32(d, (int)rva);
            int off = (int)rva + 4;
            for (int m = 0; m < n; m++)
            {
                int mo = off + m * 108;
                ulong baseAddr = BitConverter.ToUInt64(d, mo);
                uint size = BitConverter.ToUInt32(d, mo + 8);
                uint nameRva = BitConverter.ToUInt32(d, mo + 20);
                int len = BitConverter.ToInt32(d, (int)nameRva);
                string name = Encoding.Unicode.GetString(d, (int)nameRva + 4, len);
                modules.Add((baseAddr, size, name));
            }
        }
        else if (type == 6) // ExceptionStream
        {
            threadId = BitConverter.ToUInt32(d, (int)rva);
            int ex = (int)rva + 8;
            excCode = BitConverter.ToUInt32(d, ex);
            excAddr = BitConverter.ToUInt64(d, ex + 16);
            nParams = BitConverter.ToUInt32(d, ex + 24);
            for (int k = 0; k < 15; k++) excInfo[k] = BitConverter.ToUInt64(d, ex + 32 + k * 8);
            // ThreadContext
            uint ctxSize = BitConverter.ToUInt32(d, ex + 32 + 15 * 8);
            uint ctxRva = BitConverter.ToUInt32(d, ex + 36 + 15 * 8);
            if (ctxRva != 0 && ctxSize >= 0x100)
            {
                ulong rip = BitConverter.ToUInt64(d, (int)ctxRva + 0xF8);
                ulong rsp = BitConverter.ToUInt64(d, (int)ctxRva + 0x98);
                Console.WriteLine($"  thread {threadId}  RIP={rip:X16}  RSP={rsp:X16}  RIP module: {Resolve(modules, rip)}");
            }
            hasExc = true;
        }
        else if (type == 15) // MiscInfo
        {
            miscFlags = BitConverter.ToInt32(d, (int)rva + 4);
            if ((miscFlags & 1) != 0) procId = BitConverter.ToUInt32(d, (int)rva + 8);
            procCreate = BitConverter.ToUInt32(d, (int)rva + 12);
        }
    }

    if (procId != 0) Console.WriteLine($"  PID={procId}  process start: {DateTimeOffset.FromUnixTimeSeconds(procCreate).ToLocalTime():yyyy-MM-dd HH:mm:ss} (local)");

    if (hasExc)
    {
        Console.WriteLine($"  EXCEPTION code=0x{excCode:X8} ({Name(excCode)})  address={excAddr:X16}");
        Console.WriteLine($"    address module: {Resolve(modules, excAddr)}");
        if (excCode == 0xC0000005 && nParams >= 2)
        {
            string acc = excInfo[0] switch { 0 => "READ", 1 => "WRITE", 8 => "EXECUTE", _ => excInfo[0].ToString() };
            Console.WriteLine($"    access violation: {acc} at {excInfo[1]:X16}  (module: {Resolve(modules, excInfo[1])})");
        }
    }
    else Console.WriteLine("  no exception stream");

    Console.WriteLine($"  modules: {modules.Count}");
    // 与 mod 相关的模块（非 Windows 系统目录里的）
    foreach (var m in modules)
    {
        string lower = m.Name.ToLowerInvariant();
        if (lower.Contains("eldenring") || lower.Contains("randomizer") || lower.Contains("modengine") ||
            lower.Contains("winhttp") || lower.Contains("reshade") || lower.Contains("opti") ||
            lower.Contains("dxgi") || lower.Contains("d3d12") || lower.Contains("nvngx") ||
            lower.Contains("amd_ags") || lower.Contains("steam_api") || lower.Contains("oo2core") ||
            lower.Contains("bink") || lower.Contains("easyanti") || lower.Contains("lua") ||
            lower.Contains("crashfix") || lower.Contains("er_buddy") || lower.Contains("dinput"))
            Console.WriteLine($"    {m.Base:X16} +{m.Size:X8}  {m.Name}");
    }
}

static string Resolve(List<(ulong Base, uint Size, string Name)> mods, ulong addr)
{
    foreach (var m in mods)
        if (addr >= m.Base && addr < m.Base + m.Size)
            return $"{Path.GetFileName(m.Name)}+0x{addr - m.Base:X} ({m.Name})";
    return "(unknown)";
}

static string Name(uint code) => code switch
{
    0xC0000005 => "ACCESS_VIOLATION",
    0xC000001D => "ILLEGAL_INSTRUCTION",
    0xC0000094 => "INT_DIVIDE_BY_ZERO",
    0xC00000FD => "STACK_OVERFLOW",
    0xC0000409 => "STACK_BUFFER_OVERRUN / FAIL_FAST",
    0xC0000374 => "HEAP_CORRUPTION",
    0xC0000017 => "NO_MEMORY",
    0x80000003 => "BREAKPOINT",
    0x80000004 => "SINGLE_STEP",
    0xE06D7363 => "CPP_EXCEPTION",
    0x40000015 => "FATAL_APP_EXIT",
    _ => "?",
};
