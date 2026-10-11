// 对话/武器动作随机化的置换正确性验证：
//   A = 原始 regulation，B = 生成 regulation
//   1) 整组搬家：B 中每一行的"字段组"必须能在 A 的组集合中找到（多重集相等）
//   2) 池外行不动：TalkParam 的 msgId<=0 行、EquipParamWeapon 的 wepmotionCategory==0 行必须原样
using System.IO;
using SoulsFormats;

// 用法：InspectRound2 <原始 regulation.bin> <生成 regulation.bin> [paramdefs 目录]
string a = args.Length > 0 ? args[0] : "regulation_a.bin";
string b = args.Length > 1 ? args[1] : "regulation_b.bin";
string defDir = args.Length > 2 ? args[2] : "paramdefs";

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

var A = LoadAll(a, defs);
var B = LoadAll(b, defs);
int problems = 0;

static long V(PARAM.Row r, string f) { try { return Convert.ToInt64(r[f].Value); } catch { return long.MinValue; } }

void CheckGroup(string table, string[] fields, Func<PARAM.Row, bool> pool)
{
    PARAM pa = A[table], pb = B[table];
    var byA = pa.Rows.Where(r => r.ID > 0).ToDictionary(r => r.ID);
    var byB = pb.Rows.Where(r => r.ID > 0).ToDictionary(r => r.ID);

    // A 的组多重集
    var poolA = new Dictionary<string, int>();
    foreach (PARAM.Row r in byA.Values)
    {
        if (!pool(r)) continue;
        string key = string.Join("|", fields.Select(f => V(r, f)));
        poolA[key] = poolA.TryGetValue(key, out int c) ? c + 1 : 1;
    }

    int checkedRows = 0, changed = 0, movedOk = 0, movedBad = 0, fixedPoint = 0;
    var usedA = new HashSet<int>();   // 已消费的 A 行（验证是双射）
    foreach (PARAM.Row rb in byB.Values)
    {
        if (!byA.TryGetValue(rb.ID, out PARAM.Row ra)) { Console.WriteLine($"  ✗ {table} {rb.ID} 在 A 中缺失"); problems++; continue; }
        if (!pool(ra))
        {
            // 池外行必须完全不动
            foreach (string f in fields)
                if (V(ra, f) != V(rb, f)) { problems++; Console.WriteLine($"  ✗ {table} {rb.ID} 池外行被改动（{f}）"); }
            continue;
        }
        checkedRows++;
        bool same = fields.All(f => V(ra, f) == V(rb, f));
        if (same) { fixedPoint++; continue; }
        changed++;
        // B 的组必须来自 A 的某个池内行
        string key = string.Join("|", fields.Select(f => V(rb, f)));
        if (poolA.ContainsKey(key)) movedOk++; else { movedBad++; }
    }

    Console.WriteLine($"== {table} ==  校验 {checkedRows} 行：改动 {changed}，保持不变（随机过程中恰好抽中自己） {fixedPoint}");
    Console.WriteLine($"   整组搬家命中 A 的行库：{movedOk}/{changed}" + (movedBad > 0 ? $"（✗ {movedBad} 个不匹配！）" : " ✓"));
    if (movedBad > 0) problems += movedBad;

    // 多重集一致性（B 的组集合 == A 的池内组集合）
    var poolB = new Dictionary<string, int>();
    foreach (PARAM.Row rb in byB.Values)
    {
        if (!pool(rb)) continue;
        string key = string.Join("|", fields.Select(f => V(rb, f)));
        poolB[key] = poolB.TryGetValue(key, out int c) ? c + 1 : 1;
    }
    bool multisetEqual = poolA.Count == poolB.Count && poolA.All(kv => poolB.TryGetValue(kv.Key, out int c) && c == kv.Value);
    Console.WriteLine($"   多重集一致（不增不减）：{(multisetEqual ? "✓" : "✗")}");
    if (!multisetEqual) problems++;
}

Console.WriteLine($"A（原始）：{a}");
Console.WriteLine($"B（生成）：{b}");
Console.WriteLine();

// 1.2.6 起对话随机回退为「初始版」：15 个字段成套搬移、所有有台词的行参与。
string[] talkFields = { "msgId", "voiceId", "msgId_female", "voiceId_female", "spEffectId0", "motionId0",
    "spEffectId1", "motionId1", "returnPos", "reactionId", "eventId",
    "lipSyncStart", "lipSyncTime", "timeout", "talkAnimationId" };
CheckGroup("TalkParam", talkFields, r => V(r, "msgId") > 0);

CheckGroup("EquipParamWeapon", new[] { "wepmotionCategory", "wepmotionOneHandId", "wepmotionBothHandId" },
    r => V(r, "wepmotionCategory") > 0);

Console.WriteLine();
Console.WriteLine(problems == 0 ? "结论：对话 / 武器动作置换验证全部通过。" : $"结论：发现 {problems} 个问题！");
