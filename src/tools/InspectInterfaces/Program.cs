// 第三层侦察：敌人元素抗性的最终载体。
// NpcParam def_* 恒 100（无效）；SpEffectParam 的 DamageCutRate 只覆盖少数剧情效果。
// 检查 NpcParam 是否还有另一个抗性入口：defFlickPower / guardCutRate 系列 / CalcCorrectGraph。
// 同时检查 428 随机化输出（若可用）实际会写哪张表——用 EnemyStandardInfo 与 Boss 判定交叉。
using System.IO;
using SoulsFormats;

string regPath = args.Length > 0 ? args[0] : @"D:\dowlond\ErringulatorCN\src\_test\vanilla\regulation.bin";
string defDir = args.Length > 1 ? args[1] : @"D:\dowlond\ErringulatorCN\paramdefs";

var defs = new List<PARAMDEF>();
foreach (string f in Directory.GetFiles(defDir, "*.xml")) defs.Add(PARAMDEF.XmlDeserialize(f));

BND4 bnd = SFUtil.DecryptERRegulation(regPath);
var byName = new Dictionary<string, PARAM>(StringComparer.OrdinalIgnoreCase);
foreach (BinderFile file in bnd.Files)
{
    string n = Path.GetFileNameWithoutExtension(file.Name);
    PARAM p = PARAM.Read(file.Bytes);
    if (p.ApplyParamdefCarefully(defs)) byName[n] = p;
}

static long I(PARAM.Row r, string f) { try { return Convert.ToInt64(r[f].Value); } catch { return long.MinValue; } }
static double D(PARAM.Row r, string f) { try { return Convert.ToDouble(r[f].Value); } catch { return double.NaN; } }

PARAM npc = byName["NpcParam"];

// NpcParam 全字段清单里找带 def / cut / resist / resist 的（排除 guard 系列）
Console.WriteLine("== NpcParam 抗性候选字段（def_*/GuardCutRate/GuardResist）==");
foreach (var fld in npc.AppliedParamdef.Fields)
{
    string n = fld.InternalName;
    if (n.StartsWith("def_") || n.EndsWith("GuardCutRate") || n.EndsWith("GuardResist"))
    {
        // 值分布
        var dist = new Dictionary<double, int>();
        int nz = 0, tot = 0;
        foreach (PARAM.Row r in npc.Rows)
        {
            if (r.ID <= 0) continue;
            tot++;
            double v = D(r, n);
            if (double.IsNaN(v)) continue;
            dist[v] = dist.TryGetValue(v, out int c) ? c + 1 : 1;
        }
        var top = dist.OrderByDescending(k => k.Value).Take(3)
            .Select(kv => $"{kv.Key}x{kv.Value}");
        Console.WriteLine($"  {n,-22} 主要值: {string.Join(", ", top)}  (共{tot}行)");
    }
}

// 物理防御 sub 类型：def_slash/blow/thrust 是百分比修正，检查其分布
Console.WriteLine("\n== def_slash / def_blow / def_thrust 分布（Boss vs 全体）==");
foreach (string f in new[] { "def_slash", "def_blow", "def_thrust" })
{
    var dAll = new Dictionary<double, int>();
    var dBoss = new Dictionary<double, int>();
    foreach (PARAM.Row r in npc.Rows)
    {
        if (r.ID <= 0) continue;
        double v = D(r, f);
        if (double.IsNaN(v)) continue;
        dAll[v] = dAll.TryGetValue(v, out int c) ? c + 1 : 1;
        if (I(r, "npcType") == 1) dBoss[v] = dBoss.TryGetValue(v, out int c2) ? c2 + 1 : 1;
    }
    Console.WriteLine($"  {f}: 全体前5 [{string.Join(",", dAll.OrderByDescending(k => k.Value).Take(5).Select(kv => $"{kv.Key}x{kv.Value}"))}]");
    Console.WriteLine($"       Boss前5 [{string.Join(",", dBoss.OrderByDescending(k => k.Value).Take(5).Select(kv => $"{kv.Key}x{kv.Value}"))}]");
}

Console.WriteLine("\nDONE");
