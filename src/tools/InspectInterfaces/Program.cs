using System.IO; using SoulsFormats;
string defDir = @"D:\dowlond\ErringulatorCN\paramdefs";
var defs = new List<PARAMDEF>();
foreach (string f in Directory.GetFiles(defDir, "*.xml")) defs.Add(PARAMDEF.XmlDeserialize(f));
BND4 bnd = SFUtil.DecryptERRegulation(@"D:\dowlond\ErringulatorCN\src\_test\vanilla\regulation.bin");
var byName = new Dictionary<string, PARAM>(StringComparer.OrdinalIgnoreCase);
foreach (BinderFile file in bnd.Files) { string n = Path.GetFileNameWithoutExtension(file.Name); PARAM p = PARAM.Read(file.Bytes); if (p.ApplyParamdefCarefully(defs)) byName[n] = p; }
static double D(PARAM.Row r, string f) { try { return Convert.ToDouble(r[f].Value); } catch { return double.NaN; } }
static long I(PARAM.Row r, string f) { try { return Convert.ToInt64(r[f].Value); } catch { return long.MinValue; } }
PARAM tp = byName["ToughnessParam"];
PARAM npc = byName["NpcParam"];
var usedIds = npc.Rows.Where(r => r.ID > 0).Select(r => I(r, "toughness")).Where(v => v != long.MinValue).Distinct().OrderBy(v => v).ToList();
Console.WriteLine("NpcParam.toughness 的不同取值（= 候选 ToughnessParam 行 ID）: " + string.Join(", ", usedIds));
Console.WriteLine("\n这些行的内容：");
foreach (long id in usedIds) {
  PARAM.Row row = tp.Rows.FirstOrDefault(r => r.ID == id);
  if (row == null) { Console.WriteLine($"  ID={id}: 【不存在】"); continue; }
  Console.WriteLine($"  ID={id,-4} correctionRate={D(row,"correctionRate"),-6} minToughness={I(row,"minToughness"),-6} proCorrectionRate={D(row,"proCorrectionRate"),-6} spEffectId={I(row,"spEffectId"),-6} isNonEff={I(row,"isNonEffectiveCorrectionForMin")}");
}
Console.WriteLine("\nToughnessParam 全部行的 correctionRate 取值: " + string.Join(", ", tp.Rows.Select(r => D(r,"correctionRate")).Distinct().OrderBy(v => v).Take(20)));
Console.WriteLine("各 toughness 取值的敌人数量：");
foreach (long id in usedIds) {
  int c = npc.Rows.Count(r => r.ID > 0 && I(r,"toughness") == id);
  int cb = npc.Rows.Count(r => r.ID > 0 && I(r,"toughness") == id && (I(r,"npcType")==1 || I(r,"isSoulGetByBoss")==1));
  Console.WriteLine($"  toughness={id,-4} 全体 {c,5}  其中Boss {cb}");
}
