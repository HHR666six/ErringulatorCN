// 抽样展示攻击元素错位前后的实际数据：
// 从 vanilla 与生成文件里挑几条「错位前后元素伤害确实变了」的行，
// 打印它们的 atkType / spAttribute / 四个元素槽 / 修正值 / spEffectId0，
// 用于解释游戏内表现。
// 另含武器攻击链探针：从 EquipParamWeapon 出发验证 atkDark 槽在圣属性
// 武器上的实际语义（示例武器：圣指纹拳套 9040000 / 黄金墓志铭 9160000）。
using System.IO;
using SoulsFormats;

string defDir = args.Length > 0 ? args[0] : @"D:\dowlond\ErringulatorCN\paramdefs";
string aPath = args.Length > 1 ? args[1] : @"D:\dowlond\ErringulatorCN\src\_test\vanilla\regulation.bin";
string bPath = args.Length > 2 ? args[2] : Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? ".", "er_root130", "regulation.bin");

var defs = new List<PARAMDEF>();
foreach (string f in Directory.GetFiles(defDir, "*.xml")) defs.Add(PARAMDEF.XmlDeserialize(f));

static Dictionary<string, PARAM> LoadAll(string path, List<PARAMDEF> defs)
{
    BND4 bnd = SFUtil.DecryptERRegulation(path);
    var map = new Dictionary<string, PARAM>(StringComparer.OrdinalIgnoreCase);
    foreach (BinderFile f in bnd.Files)
    {
        string n = Path.GetFileNameWithoutExtension(f.Name);
        PARAM p = PARAM.Read(f.Bytes);
        if (p.ApplyParamdefCarefully(defs)) map[n] = p;
    }
    return map;
}

static long V(PARAM.Row r, string f) { try { return Convert.ToInt64(r[f].Value); } catch { return long.MinValue; } }
static double D(PARAM.Row r, string f) { try { return Convert.ToDouble(r[f].Value); } catch { return double.NaN; } }

var A = LoadAll(aPath, defs);
var B = LoadAll(bPath, defs);

string[] dmg = { "atkMag", "atkFire", "atkThun", "atkDark" };
string[] corr = { "atkMagCorrection", "atkFireCorrection", "atkThunCorrection", "atkDarkCorrection" };
string[] labels = { "魔", "火", "雷", "暗" };

// atkType 常见值含义（paramdef 枚举）：0=普通斩击等, 1=?, 2/3=特殊；打印原始值供解读
foreach (string tbl in new[] { "AtkParam_Pc", "AtkParam_Npc" })
{
    PARAM pa = A[tbl], pb = B[tbl];
    var byB = pb.Rows.Where(r => r.ID > 0).ToDictionary(r => r.ID);

    Console.WriteLine($"\n===== {tbl}：元素槽确实改变的样本 =====");
    int shown = 0;
    foreach (PARAM.Row ra in pa.Rows)
    {
        if (ra.ID <= 0 || !byB.TryGetValue(ra.ID, out PARAM.Row rb)) continue;

        var da = dmg.Select(f => D(ra, f)).ToArray();
        var db = dmg.Select(f => D(rb, f)).ToArray();
        if (da.SequenceEqual(db)) continue;                       // 没变的不展示
        if (da.All(x => x == 0.0)) continue;                      // 池外行不应出现（防御性）

        Console.WriteLine($"\n  行 ID {ra.ID}  atkType={V(ra, "atkType")}  spAttribute={V(ra, "spAttribute")}  " +
                           $"atkPhys={D(ra, "atkPhys")}  spEffectId0={V(ra, "spEffectId0")}");
        Console.WriteLine("    元素槽     原版 → 错位后");
        string[] elemNames = { "Mag", "Fire", "Thun", "Dark" };
        for (int i = 0; i < 4; i++)
            Console.WriteLine($"      [{labels[i]}] atk{elemNames[i],-4}  {da[i],8} → {db[i],8}   (修正 {D(ra, corr[i]),6} → {D(rb, corr[i]),6})");
        // 校验多重集
        var sa = string.Join(",", da.OrderBy(x => x));
        var sb = string.Join(",", db.OrderBy(x => x));
        string verdict = sa == sb ? "YES" : "NO";
        Console.WriteLine($"      多重集守恒：{verdict}");

        if (++shown >= 5) break;
    }
    if (shown == 0) Console.WriteLine("  (无样本)");
}

// ==================== 武器攻击链探针：atkDark 的实际语义 ====================
Console.WriteLine("\n===== 武器 → AtkParam_Pc 链条（验证 atkDark 槽 = 圣属性伤害）=====");
PARAM wep = A["EquipParamWeapon"];
PARAM atkPc = A["AtkParam_Pc"];
// 列出武器表里实际的元素伤害字段名（先按 paramdef 动态找，避免猜名）
var wepFields = wep.AppliedParamdef.Fields.Select(f => f.InternalName)
    .Where(n => n.ToLowerInvariant().Contains("attack") || n.Contains("dark") || n.Contains("mag"))
    .ToList();
Console.WriteLine("  EquipParamWeapon 元素相关字段: " + string.Join(", ", wepFields.Take(16)));
// 找圣属性武器的通用方式：wep殊攻字段名各版本不同，改为扫描 atkBaseParamId>0 的行，
// 打印其对应 AtkParam_Pc 行的四槽 —— 展示 atkDark 有值的真实案例
int printed = 0;
// ER 武器表字段（1.16 paramdef 实测）：attackBasePhysics / Magic / Fire / Thunder。
// 注意：ER 武器表只有这 4 个基础元素列——圣属性在武器层写进 attackBaseDark
// 不存在时，用 AtkParam 侧的 atkDark 槽承载（圣武器的判定行暗槽有值）。
// 先统计武器表 4 列分布，再展示 atkDark 有值的判定行来自哪些武器。
var cnt2 = new Dictionary<string, int>();
foreach (string f2 in new[] { "attackBasePhysics", "attackBaseMagic", "attackBaseFire", "attackBaseThunder" })
    cnt2[f2] = 0;
foreach (PARAM.Row w in wep.Rows)
{
    if (w.ID <= 0) continue;
    foreach (string f2 in cnt2.Keys.ToList()) if (D(w, f2) > 0) cnt2[f2]++;
}
foreach (var kv in cnt2) Console.WriteLine($"  {kv.Key,-22} >0: {kv.Value} 行");
cnt2["attackBaseDark"] = 0;
foreach (PARAM.Row w in wep.Rows) { if (w.ID > 0 && D(w, "attackBaseDark") > 0) cnt2["attackBaseDark"]++; }
Console.WriteLine($"  attackBaseDark          >0: {cnt2["attackBaseDark"]} 行（圣属性武器）");
// 展示 5 个圣属性武器链（attackBaseDark>0 → 其 AtkParam 判定行的暗槽）
int holyPrinted = 0;
foreach (PARAM.Row w in wep.Rows)
{
    if (w.ID <= 0 || D(w, "attackBaseDark") <= 0) continue;
    long atkId;
    try { atkId = Convert.ToInt64(w["atkBaseParamId"].Value); } catch { continue; }
    PARAM.Row ar = atkPc.Rows.FirstOrDefault(x => x.ID == atkId);
    Console.WriteLine($"  圣武器 {w.ID}（attackBaseDark={D(w, "attackBaseDark")}） -> AtkParam_Pc {atkId}: " +
        (ar != null ? $"atkMag={D(ar, "atkMag")} atkFire={D(ar, "atkFire")} atkThun={D(ar, "atkThun")} atkDark={D(ar, "atkDark")}" : "(无判定行)"));
    if (++holyPrinted >= 5) break;
}
if (holyPrinted == 0) Console.WriteLine("  (圣武器的 atkBaseParamId 均无效——伤害槽由武器基础值直接结算)");
