// 敌人韧性随机的正确性验证：
//   A = 原始 regulation，B = 生成 regulation
//   1) NpcParam 行集合不变
//   2) 每个池（Boss / 非Boss）内 (toughness, toughnessRecoverCorrection) 成对多重集守恒
//      —— 档位分布不变，只换持有者；索引仍取自本池已有值，引用安全
//   3) 池隔离：Boss 行的档位不得来自非 Boss 池（反之亦然）
//   4) 仅非Boss 模式下，Boss 行的 toughness 必须逐行不变
//   5) 非目标字段零改动（抽样 nameId / npcType / isSoulGetByBoss / def_mag / spEffectID0）
// 退出码：0 = 全部通过；2 = 有问题。
using System.IO;
using SoulsFormats;

string defDir = args.Length > 0 ? args[0] : @"D:\dowlond\ErringulatorCN\paramdefs";
string aPath = args.Length > 1 ? args[1] : @"D:\dowlond\ErringulatorCN\src\_test\vanilla\regulation.bin";
string bPath = args.Length > 2 ? args[2] : Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? ".", "er_tgh", "regulation.bin");
bool bossMode = args.Length > 3 && args[3] == "boss";

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
PARAM pa = A["NpcParam"], pb = B["NpcParam"];
// 召唤物随机化会克隆 NPC 行（名称前缀 buddyRando），这些新增行不属于韧性随机的
// 验证范围，必须排除，否则行数比较与多重集都会被克隆行污染。
static bool IsClone(PARAM.Row r) => (r.Name ?? "").StartsWith("buddyRando", StringComparison.Ordinal);
var byA = pa.Rows.Where(r => r.ID > 0 && !IsClone(r)).ToDictionary(r => r.ID);
var byB = pb.Rows.Where(r => r.ID > 0 && !IsClone(r)).ToDictionary(r => r.ID);

static long I(PARAM.Row r, string f) { try { return Convert.ToInt64(r[f].Value); } catch { return long.MinValue; } }
static double D(PARAM.Row r, string f) { try { return Convert.ToDouble(r[f].Value, System.Globalization.CultureInfo.InvariantCulture); } catch { return double.NaN; } }
static bool IsBoss(PARAM.Row r) => I(r, "npcType") == 1 || I(r, "isSoulGetByBoss") == 1;
static bool CellEquals(object a, object b)
{
    if (a is byte[] ba && b is byte[] bb)
    {
        if (ba.Length != bb.Length) return false;
        for (int i = 0; i < ba.Length; i++) if (ba[i] != bb[i]) return false;
        return true;
    }
    return Equals(a, b);
}

int problems = 0;
Console.WriteLine($"== NpcParam(排除召唤克隆): A {byA.Count} 行 / B {byB.Count} 行");
if (byA.Count != byB.Count) { Console.WriteLine("  BAD 行数不一致"); problems++; }

// ---- 2) 分池成对多重集守恒 ----
var pairA_Boss = new List<(long, double)>(); var pairB_Boss = new List<(long, double)>();
var pairA_Nrm = new List<(long, double)>(); var pairB_Nrm = new List<(long, double)>();
int missed = 0;
foreach (var kv in byA)
{
    if (!byB.TryGetValue(kv.Key, out PARAM.Row rb)) { missed++; continue; }
    var pairA = (I(kv.Value, "toughness"), Math.Round(D(kv.Value, "toughnessRecoverCorrection"), 6));
    var pairB = (I(rb, "toughness"), Math.Round(D(rb, "toughnessRecoverCorrection"), 6));
    if (IsBoss(kv.Value)) { pairA_Boss.Add(pairA); pairB_Boss.Add(pairB); }
    else { pairA_Nrm.Add(pairA); pairB_Nrm.Add(pairB); }
}
if (missed > 0) { Console.WriteLine($"  BAD B 中缺失 {missed} 行"); problems++; }

bool okBoss = pairA_Boss.OrderBy(x => x).SequenceEqual(pairB_Boss.OrderBy(x => x));
bool okNrm = pairA_Nrm.OrderBy(x => x).SequenceEqual(pairB_Nrm.OrderBy(x => x));
Console.WriteLine($"  Boss 池 成对多重集守恒: {okBoss}  (A/B 各 {pairA_Boss.Count} 条)");
Console.WriteLine($"  非Boss池 成对多重集守恒: {okNrm}  (A/B 各 {pairA_Nrm.Count} 条)");
if (!okBoss || !okNrm) problems++;

// ---- 3) 池隔离：B 中 Boss 行的档位必须仍属于 A 的 Boss 池档位集合 ----
var bossIdxA = pairA_Boss.Select(x => x.Item1).Distinct().OrderBy(v => v).ToList();
var nrmIdxA = pairA_Nrm.Select(x => x.Item1).Distinct().OrderBy(v => v).ToList();
int crossPool = 0;
foreach (var kv in byA)
{
    if (!byB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
    long got = I(rb, "toughness");
    bool boss = IsBoss(kv.Value);
    bool inOwnPool = boss ? bossIdxA.Contains(got) : nrmIdxA.Contains(got);
    if (!inOwnPool) crossPool++;
}
Console.WriteLine($"  跨池污染行数: {crossPool}（应 0）  Boss 档位集 [{string.Join(",", bossIdxA)}] / 非Boss档位集 [{string.Join(",", nrmIdxA)}]");
if (crossPool > 0) problems++;

// ---- 4) 仅非Boss 模式：Boss 行必须原样 ----
if (!bossMode)
{
    int bossChanged = 0;
    foreach (var kv in byA)
    {
        if (!byB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
        if (!IsBoss(kv.Value)) continue;
        if (I(kv.Value, "toughness") != I(rb, "toughness") ||
            Math.Abs(D(kv.Value, "toughnessRecoverCorrection") - D(rb, "toughnessRecoverCorrection")) > 1e-6) bossChanged++;
    }
    Console.WriteLine($"  断言（仅非Boss 模式）：Boss 行韧性被改动 = {bossChanged}（应 0）");
    if (bossChanged > 0) problems++;
}

// ---- 5) 非目标字段抽样零改动 ----
foreach (string f in new[] { "nameId", "npcType", "isSoulGetByBoss", "def_mag", "spEffectID0", "teamType" })
{
    int changed = 0;
    foreach (var kv in byA)
    {
        if (!byB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
        if (!CellEquals(kv.Value[f].Value, rb[f].Value)) changed++;
    }
    Console.WriteLine($"  非目标字段 {f,-16} 改动行数: {changed}（应 0）");
    if (changed > 0) problems++;
}

// ---- 变动统计 ----
int moved = byA.Count(kv => byB.TryGetValue(kv.Key, out PARAM.Row rb) && I(kv.Value, "toughness") != I(rb, "toughness"));
Console.WriteLine($"  档位实际发生变化的行数: {moved}");

Console.WriteLine(problems == 0
    ? "RESULT: PASS - all enemy-toughness invariants hold"
    : $"RESULT: FAIL - {problems} problem class(es)");
return problems == 0 ? 0 : 2;
