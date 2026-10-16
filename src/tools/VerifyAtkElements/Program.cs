// 攻击元素错位（装备效果子项）的正确性验证：
//   A = 原始 regulation，B = 生成 regulation
//   1) 行集合不变（不增不减）
//   2) 行内多重集守恒：每行的 {atkMag,atkFire,atkThun,atkDark} 与四个 Correction
//      在 A / B 之间作为多重集完全相等（只换槽位，不改总规模）
//   3) 池外行零改动：四槽皆 0 的行，8 个目标字段必须逐位相同
//   4) 非目标字段零改动：除目标字段外，任何字段都不得变化（含 atkPhys）
//   5) 1.3.1 起 spAttributeVariationValue（SFX 变体值）也是目标字段：
//      它在「带值的行」之间整体置换，因此按「带值行多重集守恒 +
//      零值行零改动」验证（单字段无法行内旋转）。
using System.IO;
using SoulsFormats;

string defDir = args.Length > 0 ? args[0] : @"D:\dowlond\ErringulatorCN\paramdefs";
string aPath = args.Length > 1 ? args[1] : @"D:\dowlond\ErringulatorCN\src\_test\ae_in\regulation.bin";
string bPath = args.Length > 2 ? args[2] : Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? ".", "er_ae_A", "regulation.bin");

var defs = new List<PARAMDEF>();
foreach (string f in Directory.GetFiles(defDir, "*.xml"))
    defs.Add(PARAMDEF.XmlDeserialize(f));

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

string[] dmg = { "atkMag", "atkFire", "atkThun", "atkDark" };
string[] corr = { "atkMagCorrection", "atkFireCorrection", "atkThunCorrection", "atkDarkCorrection" };
string variationField = "spAttributeVariationValue";
var targets = new HashSet<string>(dmg.Concat(corr).Append(variationField), StringComparer.Ordinal);

int problems = 0;

/// <summary>单元格值比较：dummy8 填充字段是 byte[]，必须比内容而不是比引用。</summary>
static bool CellEquals(object a, object b)
{
    if (a is byte[] ba && b is byte[] bb)
    {
        if (ba.Length != bb.Length) return false;
        for (int i = 0; i < ba.Length; i++) if (ba[i] != bb[i]) return false;
        return true;
    }
    return a.Equals(b);
}

/// <summary>安全读 double（字段缺失或类型异常时返回 NaN）。</summary>
static double SafeD(PARAM.Row r, string f)
{
    try { return Convert.ToDouble(r[f].Value); } catch { return double.NaN; }
}

foreach (string tbl in new[] { "AtkParam_Pc", "AtkParam_Npc" })
{
    if (!A.TryGetValue(tbl, out PARAM pa) || !B.TryGetValue(tbl, out PARAM pb))
    {
        Console.WriteLine($"✗ {tbl} 缺失（A={A.ContainsKey(tbl)} B={B.ContainsKey(tbl)}）");
        problems++;
        continue;
    }

    Console.WriteLine($"\n== {tbl}: A {pa.Rows.Count} 行 / B {pb.Rows.Count} 行");
    if (pa.Rows.Count != pb.Rows.Count) { Console.WriteLine("  ✗ 行数不一致"); problems++; }

    var byA = pa.Rows.Where(r => r.ID > 0).ToDictionary(r => r.ID);
    var byB = pb.Rows.Where(r => r.ID > 0).ToDictionary(r => r.ID);
    if (byA.Count != byB.Count) { Console.WriteLine("  ✗ 有效行集合大小不一致"); problems++; }

    var fieldNames = pa.AppliedParamdef.Fields.Select(f => f.InternalName).ToArray();

    int touched = 0, untouched = 0, movedOnly = 0, badMultiset = 0, badOther = 0, badOutOfPool = 0;

    foreach (var kv in byA)
    {
        if (!byB.TryGetValue(kv.Key, out PARAM.Row rb))
        {
            Console.WriteLine($"  ✗ 行 {kv.Key} 在 B 中缺失");
            problems++;
            continue;
        }
        PARAM.Row ra = kv.Value;

        // 非目标字段必须完全一致
        for (int i = 0; i < fieldNames.Length && i < ra.Cells.Count && i < rb.Cells.Count; i++)
        {
            string fn = fieldNames[i];
            if (targets.Contains(fn)) continue;
            object va = ra.Cells[i].Value, vb = rb.Cells[i].Value;
            bool same = va == null && vb == null || (va != null && vb != null && CellEquals(va, vb));
            if (!same)
            {
                if (badOther < 5) Console.WriteLine($"  ✗ 行 {ra.ID} 非目标字段 {fn} 被改动：{va} → {vb}");
                badOther++;
            }
        }

        // 目标字段：多重集守恒
        static List<double> Grab(PARAM.Row r, string[] fs)
        {
            var l = new List<double>();
            foreach (string f in fs)
            {
                try { l.Add(Convert.ToDouble(r[f].Value)); } catch { l.Add(double.NaN); }
            }
            return l;
        }
        var da = Grab(ra, dmg); var db = Grab(rb, dmg);
        var ca = Grab(ra, corr); var cb = Grab(rb, corr);
        bool okD = da.OrderBy(x => x).SequenceEqual(db.OrderBy(x => x));
        bool okC = ca.OrderBy(x => x).SequenceEqual(cb.OrderBy(x => x));
        if (!okD || !okC)
        {
            if (badMultiset < 5)
                Console.WriteLine($"  ✗ 行 {ra.ID} 多重集不守恒：dmg[{string.Join(",", da)}] → [{string.Join(",", db)}]");
            badMultiset++;
        }

        bool aAllZero = da.All(x => x == 0.0);
        bool changed = !da.SequenceEqual(db) || !ca.SequenceEqual(cb);

        if (changed)
        {
            touched++;
            if (aAllZero) { badOutOfPool++; if (badOutOfPool < 5) Console.WriteLine($"  ✗ 行 {ra.ID} 池外行（四槽皆0）被改动"); }
            else movedOnly++;
        }
        else untouched++;
    }

    Console.WriteLine($"  行分类：变动 {touched}（其中池内 {movedOnly}）+ 未变动 {untouched}");
    Console.WriteLine($"  多重集不守恒：{badMultiset}，池外行被改动：{badOutOfPool}，非目标字段被改动：{badOther}");
    if (badMultiset > 0 || badOutOfPool > 0 || badOther > 0) problems++;

    // 1.3.1：SFX 变体值在「带值行」之间整体置换 —— 带值行多重集守恒 + 零值行零改动
    var vA = new List<double>(); var vB = new List<double>();
    int varZeroChanged = 0, varRowsMoved = 0;
    foreach (var kv in byA)
    {
        if (!byB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
        double a = SafeD(kv.Value, variationField), b = SafeD(rb, variationField);
        if (double.IsNaN(a) || double.IsNaN(b)) continue;
        if (a == 0.0)
        {
            if (b != 0.0) varZeroChanged++;
            continue;
        }
        if (a != b) varRowsMoved++;
        vA.Add(a); vB.Add(b);
    }
    bool varOk = vA.OrderBy(x => x).SequenceEqual(vB.OrderBy(x => x));
    Console.WriteLine($"  SFX 变体 {variationField}：带值行 {vA.Count}，多重集守恒={varOk}，"
        + $"置换行数={varRowsMoved}，零值行被改动={varZeroChanged}");
    if (!varOk || varZeroChanged > 0) problems++;

    // atkPhys 全表零改动（额外强调）
    int physChanged = 0;
    foreach (var kv in byA)
    {
        if (!byB.TryGetValue(kv.Key, out PARAM.Row rb)) continue;
        if (!Equals(kv.Value["atkPhys"].Value, rb["atkPhys"].Value)) physChanged++;
    }
    Console.WriteLine($"  行数/多重集/池外/非目标字段：rows {pa.Rows.Count}=?{pb.Rows.Count}, "
        + $"multiset_bad={badMultiset}, out_of_pool_bad={badOutOfPool}, non_target_bad={badOther}, "
        + $"variation_bad={(varOk && varZeroChanged == 0 ? 0 : 1)}, "
        + $"phys_changed={physChanged}, in_pool_changed={movedOnly}");

    if (badMultiset > 0 || badOutOfPool > 0 || badOther > 0 || physChanged > 0) problems++;
}

Console.WriteLine(problems == 0
    ? "RESULT: PASS - all attack-element invariants hold"
    : $"RESULT: FAIL - {problems} problem class(es)");
return problems == 0 ? 0 : 2;
