// 体检：对比 原版 / 游戏目录当前文件 的 TalkParam 状态（判断是否被旧版搬乱）。
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SoulsFormats;

string defDir = @"D:\dowlond\Erringulator 0.1.1\ErringulatorCN\paramdefs";
string vanillaPath = @"D:\dowlond\Erringulator 0.1.1\ErringulatorCN\src\_test\vanilla\regulation.bin";

var defs = new List<PARAMDEF>();
foreach (string f in Directory.GetFiles(defDir, "*.xml"))
    defs.Add(PARAMDEF.XmlDeserialize(f));

static long V(PARAM.Row r, string f) { try { return Convert.ToInt64(r[f].Value); } catch { return long.MinValue; } }
static bool B(PARAM.Row r, string f) { try { return Convert.ToBoolean(r[f].Value); } catch { return false; } }

PARAM LoadTalk(string path)
{
    BND4 bnd = SFUtil.DecryptERRegulation(path);
    foreach (BinderFile file in bnd.Files)
        if (Path.GetFileNameWithoutExtension(file.Name).Equals("TalkParam", StringComparison.OrdinalIgnoreCase))
        {
            PARAM p = PARAM.Read(file.Bytes);
            p.ApplyParamdefCarefully(defs);
            return p;
        }
    return null;
}

void Report(string label, string path)
{
    if (!File.Exists(path)) { Console.WriteLine($"\n===== {label}: 文件不存在 {path}"); return; }
    Console.WriteLine($"\n===== {label} =====\n{path}  ({new FileInfo(path).Length:N0} 字节)");
    PARAM t;
    try { t = LoadTalk(path); } catch (Exception ex) { Console.WriteLine("  读取失败：" + ex.Message); return; }

    var rows = t.Rows.Where(r => r.ID > 0).ToList();
    int animCount = rows.Count(r => V(r, "talkAnimationId") != -1);
    int mismatch = rows.Count(r => V(r, "msgId") > 0 && V(r, "msgId") != V(r, "voiceId"));
    int force = rows.Count(r => B(r, "isForceDisp"));
    Console.WriteLine($"  行数={rows.Count}  anim!=-1 的行={animCount}（原版=264）  msg!=voice 的行={mismatch}（原版=331）  isForceDisp={force}（原版=237）");

    // 说话动作指纹：所有 (id, anim) 对的哈希（原版固定）
    var animPairs = rows.Where(r => V(r, "talkAnimationId") != -1).OrderBy(r => r.ID)
                        .Select(r => $"{r.ID}:{V(r, "talkAnimationId")}");
    string fp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", animPairs))))[..16];
    Console.WriteLine($"  说话动作指纹 = {fp}");

    // 全部节奏字段的整表指纹（与 Core 的检测逻辑一致）
    string[] rhythm = { "talkAnimationId", "lipSyncStart", "lipSyncTime", "timeout", "motionId0", "motionId1",
                        "reactionId", "eventId", "returnPos", "spEffectId0", "spEffectId1" };
    var sb = new StringBuilder();
    foreach (PARAM.Row r in rows.OrderBy(r => r.ID))
    {
        sb.Append(r.ID).Append(':');
        foreach (string f in rhythm) sb.Append(V(r, f)).Append(':');
        sb.Append('|');
    }
    Console.WriteLine($"  节奏整表指纹 = {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16]}");

    // 90200（商人类动画）现在挂在哪些行上
    var owners = rows.Where(r => V(r, "talkAnimationId") == 90200).Select(r => r.ID).OrderBy(x => x).Take(10).ToList();
    Console.WriteLine($"  anim=90200 的行：{(owners.Count == 0 ? "无" : string.Join(",", owners))}{(((rows.Count(r => V(r, "talkAnimationId") == 90200)) > 10) ? " …" : "")}");

    // 检查几个典型行（原版 30xx万段 带 90200 的商人类对话）
    foreach (PARAM.Row r in rows.Where(r => V(r, "talkAnimationId") == 90200 || V(r, "talkAnimationId") == 90300).Take(6))
        Console.WriteLine($"    id={r.ID} anim={V(r, "talkAnimationId")} msg={V(r, "msgId")} voice={V(r, "voiceId")} force={B(r, "isForceDisp")}");
}

Report("原版（测试副本）", vanillaPath);
Report("游戏目录当前文件", @"D:\steam\steamapps\common\ELDEN RING\Game\regulation.bin");
Report("游戏目录备份 .bak", @"D:\steam\steamapps\common\ELDEN RING\Game\regulation.bin.bak");
