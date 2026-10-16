// 敌人抗性随机 + SFX 变体换位的正确性验证：
//   A = 原始 regulation，B = 生成 regulation
//   1) NpcParam 抗性字段：每个字段在指定池（Boss/非Boss）内多重集守恒；
//      池外行（该字段的 0 值行）零改动；非目标字段零改动。
//   2) AtkParam spAttributeVariationValue：带值行内多重集守恒；0 值行零改动。
//   3) 攻击元素四槽：沿用 1.3.0 的行内多重集守恒。
// 退出码：0 = 全部通过；2 = 有问题。
using System.IO;
using SoulsFormats;

string defDir = args.Length > 0 ? args[0] : @"D:\dowlond\ErringulatorCN\paramdefs";
string aPath = args.Length > 1 ? args[1] : @"D:\dowlond\ErringulatorCN\src\_test\vanilla\regulation.bin";
string bPath = args.Length > 2 ? args[2] : Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? ".", "er_res_B", "regulation.bin");
bool checkBoss = args.Length > 3 && args[3] == "boss";

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

var A = LoadAll(aPath, defs);
var B = LoadAll(bPath, defs);

static double D(PARAM.Row r, string f)
{
    try { return Convert.ToDouble(r[f].Value, System.Globalization.CultureInfo.InvariantCulture); }
    catch { return double.NaN; }
}
static long I(PARAM.Row r, string f) { try { return Convert.ToInt64(r[f].Value); } catch { return long.MinValue; } }

int problems = 0;

string[] resistFields =
{
    "poisonGuardResist", "diseaseGuardResist", "bloodGuardResist",
    "curseGuardResist", "sleepGuardResist", "madnessGuardResist", "freezeGuardResist",
    "magGuardCutRate", "fireGuardCutRate", "thunGuardCutRate", "darkGuardCutRate",
};

PARAM pa = A["NpcParam"], pb = B["NpcParam"];
var byA = pa.Rows.Where(r => r.ID > 0).ToDictionary(r => r.ID);
var byB = pb.Rows.Where(r => r.ID > 0).ToDictionary(r => r.ID);

// 显式断言（boss 模式关闭时）：Boss 行的每个抗性字段必须与原版逐行相同。
if (!checkBoss)
{
    int bossChanged = 0;
    foreach (var kv in byA)
    {
        if (!byB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
        bool isBoss = I(kv.Value, "npcType") == 1 || I(kv.Value, "isSoulGetByBoss") == 1;
        if (!isBoss) continue;
        foreach (string f in resistFields)
        {
            if (!Equals(kv.Value[f].Value, rb[f].Value)) { bossChanged++; break; }
        }
    }
    Console.WriteLine($"== 断言（仅非Boss 模式）：Boss 行抗性被改动的行数 = {bossChanged}（应 0）");
    if (bossChanged > 0) problems++;
}

// ==================== 1) NpcParam 抗性 ====================
var targets = new HashSet<string>(resistFields, StringComparer.Ordinal);

Console.WriteLine($"== NpcParam: A {pa.Rows.Count} 行 / B {pb.Rows.Count} 行");
if (pa.Rows.Count != pb.Rows.Count) { Console.WriteLine("  BAD row_count"); problems++; }

foreach (string f in resistFields)
{
    var multA_Boss = new List<double>(); var multB_Boss = new List<double>();
    var multA_Nrm = new List<double>(); var multB_Nrm = new List<double>();
    int poolViolated = 0, zeroChanged = 0;

    foreach (var kv in byA)
    {
        if (!byB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
        PARAM.Row ra = kv.Value;
        bool isBoss = I(ra, "npcType") == 1 || I(ra, "isSoulGetByBoss") == 1;

        double va = D(ra, f), vb = D(rb, f);
        if (double.IsNaN(va) || double.IsNaN(vb)) continue;
        if (va == 0)
        {
            if (vb != 0) zeroChanged++;
            continue;
        }
        // 非 0 值必须在同一个池里（Boss 行的新值应来自 Boss 池）——
        // 用多重集合差验证：每个池各自守恒即可，行归属不允许跨池。
        if (isBoss) { multA_Boss.Add(va); multB_Boss.Add(vb); }
        else { multA_Nrm.Add(va); multB_Nrm.Add(vb); }
    }

    bool okBoss = multA_Boss.OrderBy(x => x).SequenceEqual(multB_Boss.OrderBy(x => x));
    bool okNrm = multA_Nrm.OrderBy(x => x).SequenceEqual(multB_Nrm.OrderBy(x => x));
    if (!okBoss || !okNrm) poolViolated++;
    if (zeroChanged > 0) poolViolated++;

    Console.WriteLine($"  {f,-22} bossPool(A/B)={multA_Boss.Count}/{multB_Boss.Count} ok={okBoss}" +
        $"  normPool={multA_Nrm.Count}/{multB_Nrm.Count} ok={okNrm}" +
        $"  zeroChanged={zeroChanged}" + (poolViolated > 0 ? "  BAD" : ""));
    if (poolViolated > 0) problems++;
}

// 非目标字段抽样：nameId / teamType / hitRadius 必须零改动
foreach (string f in new[] { "nameId", "teamType", "hitRadius", "spEffectID0", "def_mag" })
{
    int changed = 0;
    foreach (var kv in byA)
    {
        if (!byB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
        if (!Equals(kv.Value[f].Value, rb[f].Value)) changed++;
    }
    Console.WriteLine($"  非目标字段 {f,-14} 改动行数: {changed}（应 0）");
    if (changed > 0) problems++;
}

// ==================== 2) AtkParam SFX 变体 ====================
foreach (string tbl in new[] { "AtkParam_Pc", "AtkParam_Npc" })
{
    PARAM ta = A[tbl], tb = B[tbl];
    var vA = new List<double>(); var vB = new List<double>();
    int zeroChanged = 0, otherChanged = 0;
    var byTA = ta.Rows.Where(r => r.ID > 0).ToDictionary(r => r.ID);
    var byTB = tb.Rows.Where(r => r.ID > 0).ToDictionary(r => r.ID);

    foreach (var kv in byTA)
    {
        if (!byTB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
        PARAM.Row ra = kv.Value;
        double va = D(ra, "spAttributeVariationValue"), vb = D(rb, "spAttributeVariationValue");
        if (double.IsNaN(va) || double.IsNaN(vb)) continue;
        if (va == 0) { if (vb != 0) zeroChanged++; continue; }
        vA.Add(va); vB.Add(vb);
        // atkType（SFX 属性）不允许变
        if (!Equals(ra["atkType"].Value, rb["atkType"].Value)) otherChanged++;
    }
    bool okVar = vA.OrderBy(x => x).SequenceEqual(vB.OrderBy(x => x));
    Console.WriteLine($"\n== {tbl} SFX变体: 带值行 {vA.Count} 守恒={okVar}  零值行被改={zeroChanged}  atkType被改={otherChanged}");
    if (!okVar || zeroChanged > 0 || otherChanged > 0) problems++;

    // 攻击元素四槽行内守恒（1.3.0 既有断言）
    string[] dmg = { "atkMag", "atkFire", "atkThun", "atkDark" };
    int msBad = 0, physChanged = 0;
    foreach (var kv in byTA)
    {
        if (!byTB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
        var da = dmg.Select(f => D(kv.Value, f)).OrderBy(x => x).ToList();
        var db = dmg.Select(f => D(rb, f)).OrderBy(x => x).ToList();
        if (!da.SequenceEqual(db)) msBad++;
        if (!Equals(kv.Value["atkPhys"].Value, rb["atkPhys"].Value)) physChanged++;
    }
    Console.WriteLine($"   元素槽多重集违例: {msBad}  atkPhys被改: {physChanged}（均应 0）");
    if (msBad > 0 || physChanged > 0) problems++;
}

Console.WriteLine(problems == 0
    ? "RESULT: PASS - all enemy-resist & sfx-variation invariants hold"
    : $"RESULT: FAIL - {problems} problem class(es)");
return problems == 0 ? 0 : 2;
