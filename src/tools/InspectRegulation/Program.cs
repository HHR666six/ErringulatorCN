// 验证 1.16 regulation.bin 与 Summon Randomizer v1.0.8 算法所需的字段 / ID 兼容性。
using System.IO;
using SoulsFormats;

string regPath = args.Length > 0 ? args[0] : "regulation.bin";
string defDir = args.Length > 1 ? args[1] : "paramdefs";

Console.WriteLine($"regulation: {regPath}");
Console.WriteLine($"paramdefs : {defDir}");

var defs = new List<PARAMDEF>();
foreach (string f in Directory.GetFiles(defDir, "*.xml"))
    defs.Add(PARAMDEF.XmlDeserialize(f));

BND4 bnd = SFUtil.DecryptERRegulation(regPath);
var byName = new Dictionary<string, PARAM>(StringComparer.OrdinalIgnoreCase);
foreach (BinderFile file in bnd.Files)
{
    string name = Path.GetFileNameWithoutExtension(file.Name);
    PARAM p = PARAM.Read(file.Bytes);
    if (p.ApplyParamdefCarefully(defs)) byName[name] = p;
}

static long V(PARAM.Row r, string f) { try { return Convert.ToInt64(r[f].Value); } catch { return long.MinValue; } }

// ---- BuddyParam ----
PARAM buddy = byName["BuddyParam"];
Console.WriteLine($"\n== BuddyParam: {buddy.Rows.Count} rows, paramdef {buddy.AppliedParamdef.ParamType}");
foreach (string f in new[] { "npcParamId", "npcThinkParamId", "npcParamId_ridden", "npcThinkParamId_ridden",
             "npcPlayerInitParamId", "generateAnimId", "appearOnAroundSekihi", "pcFollowType",
             "x_offset", "z_offset", "dopingSpEffect_lv0", "dopingSpEffect_lv10", "triggerSpEffectId" })
    Console.WriteLine($"  field {f,-24} {(buddy.AppliedParamdef.Fields.Any(x => x.InternalName == f) ? "OK" : "MISSING")}");
var buddyRows = buddy.Rows.Where(r => r.ID > 0).ToArray();
Console.WriteLine($"  rows>0: {buddyRows.Length}; base(ID%100==0): {buddyRows.Count(r => r.ID % 100 == 0)}; non-base: {buddyRows.Count(r => r.ID % 100 != 0)}");
Console.WriteLine($"  ID range: {buddyRows.Min(r => r.ID)} .. {buddyRows.Max(r => r.ID)}");
foreach (int id in new[] { 20700000, 20000000, 22000000 })
{
    PARAM.Row r = buddy.Rows.FirstOrDefault(x => x.ID == id);
    Console.WriteLine(r == null
        ? $"  buddy {id}: (absent)"
        : $"  buddy {id}: npc={V(r, "npcParamId")} think={V(r, "npcThinkParamId")} ridden={V(r, "npcParamId_ridden")}/{V(r, "npcThinkParamId_ridden")} playerInit={V(r, "npcPlayerInitParamId")} genAnim={V(r, "generateAnimId")}");
}

// ---- NpcParam ----
PARAM npc = byName["NpcParam"];
Console.WriteLine($"\n== NpcParam: {npc.Rows.Count} rows");
foreach (string f in new[] { "teamType", "itemLotId_enemy", "itemLotId_map", "GameClearSpEffectID", "getSoul",
             "hitRadius", "hitHeight", "chrHitRadius", "spEffectID0", "spEffectID31", "behaviorVariationId" })
    Console.WriteLine($"  field {f,-24} {(npc.AppliedParamdef.Fields.Any(x => x.InternalName == f) ? "OK" : "MISSING")}");
foreach (int id in new[] { 133201000, 100000010 })
{
    PARAM.Row r = npc.Rows.FirstOrDefault(x => x.ID == id);
    Console.WriteLine(r == null ? $"  npc {id}: (absent)" : $"  npc {id}: OK teamType={V(r, "teamType")} nameHit={r.Name}");
}
int poolCount = npc.Rows.Count(r => r.ID > 0 && r.ID < 89000000 && V(r, "behaviorVariationId") > 10000);
Console.WriteLine($"  pool candidates (behaviorVariationId>10000, ID<89000000): {poolCount}");
int named = npc.Rows.Count(r => r.ID > 0 && (r.Name?.StartsWith("buddyRando") ?? false));
Console.WriteLine($"  pre-existing buddyRando clones: {named}");

// ---- NpcThinkParam ----
PARAM think = byName["NpcThinkParam"];
Console.WriteLine($"\n== NpcThinkParam: {think.Rows.Count} rows");
Console.WriteLine($"  field isBuddyAI: {(think.AppliedParamdef.Fields.Any(x => x.InternalName == "isBuddyAI") ? "OK" : "MISSING")}");
foreach (int id in new[] { 133200000, 100000010 })
    Console.WriteLine($"  think {id}: {(think.Rows.Any(x => x.ID == id) ? "OK" : "(absent)")}");
// digit-truncation matching coverage
var thinkIds = think.Rows.Select(r => (long)r.ID).ToHashSet();
int matched = 0;
foreach (PARAM.Row r in npc.Rows)
{
    long id = r.ID;
    if (id <= 0 || id >= 89000000 || V(r, "behaviorVariationId") <= 10000) continue;
    if (thinkIds.Contains(id) || thinkIds.Contains(id - id % 10) || thinkIds.Contains(id - id % 100) ||
        thinkIds.Contains(id - id % 1000) || thinkIds.Contains(id - id % 10000) || thinkIds.Contains(id - id % 100000))
        matched++;
}
Console.WriteLine($"  think-matched pool entries: {matched}/{poolCount}");

// ---- SpEffectParam ----
PARAM sp = byName["SpEffectParam"];
Console.WriteLine($"\n== SpEffectParam: {sp.Rows.Count} rows");
foreach (string f in new[] { "maxHpRate", "physicsAttackPowerRate", "magicAttackPowerRate", "fireAttackPowerRate",
             "thunderAttackPowerRate", "darkAttackPowerRate" })
    Console.WriteLine($"  field {f,-28} {(sp.AppliedParamdef.Fields.Any(x => x.InternalName == f) ? "OK" : "MISSING")}");
for (int i = 0; i <= 10; i++)
{
    PARAM.Row r = sp.Rows.FirstOrDefault(x => x.ID == 290000 + i);
    if (r == null) { Console.WriteLine($"  spEffect {290000 + i}: (absent)"); continue; }
    if (i == 0)
        Console.WriteLine($"  spEffect 290000..: maxHpRate={V(r, "maxHpRate")} physAtk={V(r, "physicsAttackPowerRate")}");
}
foreach (int id in new[] { 295000, 296000, 297000 })
    Console.WriteLine($"  spEffect {id}: {(sp.Rows.Any(x => x.ID == id) ? "OK" : "(absent)")}");

// ---- EquipParamGoods ----
PARAM goods = byName["EquipParamGoods"];
Console.WriteLine($"\n== EquipParamGoods: {goods.Rows.Count} rows");
int ashes = goods.Rows.Count(r => r.ID > 0 && V(r, "goodsUseAnim") == 34);
Console.WriteLine($"  spirit-ash goods (goodsUseAnim==34): {ashes}");

// ---- CharaInitParam ----
if (byName.TryGetValue("CharaInitParam", out PARAM ci))
    Console.WriteLine($"\n== CharaInitParam: 26050 {(ci.Rows.Any(x => x.ID == 26050) ? "OK" : "(absent)")}");
else
    Console.WriteLine("\n== CharaInitParam: table missing");

// 检查 NpcParam/NpcThinkParam 的 ID 空洞情况（克隆空间）
long npcMax = npc.Rows.Max(r => r.ID);
long thinkMax = think.Rows.Max(r => r.ID);
Console.WriteLine($"\nNpcParam max ID: {npcMax}; NpcThinkParam max ID: {thinkMax} (clone space above max)");
Console.WriteLine("\nDONE");
