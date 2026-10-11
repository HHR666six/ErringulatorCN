using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ErringulatorCN.Core;
using SoulsFormats;

namespace ErringulatorCN.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                if (args.Length == 0) { PrintHelp(); return 1; }
                string cmd = args[0].ToLowerInvariant();
                var opt = ParseOptions(args.Skip(1).ToArray());

                return cmd switch
                {
                    "fields" => CmdFields(opt),
                    "dump" => CmdDump(opt),
                    "csvexport" => CmdCsvExport(opt),
                    "randomize" => CmdRandomize(opt),
                    "verify" => CmdVerify(opt),
                    "summons" => CmdSummons(opt),
                    "inspect" => CmdInspect(opt),
                    "safetycheck" => CmdSafetyCheck(opt),
                    "compare" => CmdCompare(opt),
                    "selftest" => CmdSelfTest(opt),
                    _ => Fail($"未知命令：{cmd}"),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"ErringulatorCN 命令行（效果随机化器）

用法：
  ErringulatorCN.Cli fields   [--defs <paramdef目录>] [--param <表名>]
  ErringulatorCN.Cli dump     --input <游戏目录> [--param <表名>] [--rows N]
  ErringulatorCN.Cli randomize --input <游戏目录> [--output <输出目录>] [--seed <种子>]
                              [--mode shuffle|shift|reverse] [--shift N]
                              [--no-magic] [--no-ash] [--no-goods] [--no-equip]
                              [--weapon-ash] [--npc-spells] [--defs <paramdef目录>]
  召唤物随机化（完整 = Summon Randomizer 式，默认开启）：
    --no-summons            关闭召唤物随机化
    --summon-mild           改用温和模式（仅成对错位）
    --summon-preset <名称>  套用预设：fun|reasonable|chaos|family|playground|balanced
    --summon-settings <串>  套用原版 Summon Randomizer 的设置串
    --summon-initial N      初始多召几率 %（默认 50）
    --summon-additional N   追加多召几率 %（默认 50）
    --summon-max N          多召上限 1-9（默认 5）
    --summon-dupe N         多召重复几率 %（默认 80）
    --summon-variant N      变体复用几率 %（默认 20）
    --buddy-reuse           允许骨灰重复
    --allow-large-multi     允许大体积多召
    --summon-hp X           血量倍率（默认 2.0，加法落在 SpEffect 290000）
    --summon-dmg X          伤害倍率（默认 3.0）
    --summon-fp X           FP 倍率（默认 1.0）
    --no-summon-mult        不应用倍率
    --no-summon-costs       不重掷骨灰召唤消耗（FP/HP）
    --talks                 启用 NPC 对话随机（说别人的台词）
    --weapon-motion         启用武器动作模组随机（用别的武器动画）
    --summon-fplo N / --summon-fphi N    骨灰 FP 消耗范围（默认 30..60）
    --summon-hpchance N     骨灰改用 HP 消耗的几率 %（默认 10）
    --summon-hplo N / --summon-hphi N    骨灰 HP 消耗范围（默认 300..600）
  ErringulatorCN.Cli verify   --input <原始regulation> --output <生成后的regulation>
  ErringulatorCN.Cli summons  --input <原始regulation> --output <生成后的regulation>
                              召唤物专项校验（克隆有效性 / 多召数量 / 倍率 / 消耗）
");
        }

        private static int Fail(string msg)
        {
            Console.Error.WriteLine(msg);
            return 1;
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--")) continue;
                string key = a.Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    dict[key] = args[++i];
                }
                else
                {
                    dict[key] = "true";
                }
            }
            return dict;
        }

        private static bool Flag(Dictionary<string, string> o, string key) =>
            o.TryGetValue(key, out string v) && v != "false" && v != "0";

        private static string Val(Dictionary<string, string> o, string key, string def = null) =>
            o.TryGetValue(key, out string v) ? v : def;

        // ---------------- fields：列出参数表定义 ----------------

        private static int CmdFields(Dictionary<string, string> o)
        {
            string defs = EffectRandomizer.ResolveParamdefDir(Val(o, "defs"));
            Console.WriteLine($"paramdef 目录：{defs}");
            string filter = Val(o, "param");

            foreach (string file in Directory.GetFiles(defs, "*.xml").OrderBy(x => x))
            {
                PARAMDEF def;
                try { def = PARAMDEF.XmlDeserialize(file); } catch { continue; }
                if (!string.IsNullOrEmpty(filter) &&
                    !def.ParamType.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileNameWithoutExtension(file).Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                Console.WriteLine();
                Console.WriteLine($"===== {Path.GetFileNameWithoutExtension(file)}  type={def.ParamType}  rowSize={def.GetRowSize()}  fields={def.Fields.Count} =====");
                for (int i = 0; i < def.Fields.Count; i++)
                    Console.WriteLine($"{i,4}  {def.Fields[i].DisplayType,-10} {def.Fields[i].InternalName}");
            }
            return 0;
        }

        // ---------------- dump：读取真实表 ----------------

        private static int CmdDump(Dictionary<string, string> o)
        {
            string input = Val(o, "input") ?? @"D:\steam\steamapps\common\ELDEN RING\Game";
            int rowLimit = int.TryParse(Val(o, "rows", "5"), out int rl) ? rl : 5;
            string filter = Val(o, "param");

            string reg = Path.Combine(input, "regulation.bin");
            Console.WriteLine($"读取：{reg}");
            BND4 bnd = SFUtil.DecryptERRegulation(reg);
            Console.WriteLine($"参数文件 {bnd.Files.Count} 个，压缩方式 {bnd.Compression}");

            var defs = EffectRandomizer.ResolveParamdefDir(Val(o, "defs"));
            var paramdefs = Directory.GetFiles(defs, "*.xml")
                .Select(p => { try { return PARAMDEF.XmlDeserialize(p); } catch { return null; } })
                .Where(d => d != null).ToList();

            foreach (BinderFile f in bnd.Files)
            {
                string name = Path.GetFileNameWithoutExtension(f.Name);
                if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                PARAM p = PARAM.Read(f.Bytes);
                bool applied = p.ApplyParamdefCarefully(paramdefs);
                Console.WriteLine();
                Console.WriteLine($"===== {name}  type={p.ParamType}  rows={p.Rows.Count}  rowSize={p.DetectedSize}  paramdef={(applied ? "已匹配" : "未匹配")} =====");

                if (!applied || rowLimit <= 0) continue;
                foreach (PARAM.Row row in p.Rows.Take(rowLimit))
                {
                    Console.WriteLine($"  -- ID={row.ID} name={row.Name}");
                    int shown = 0;
                    for (int i = 0; i < Math.Min(p.AppliedParamdef.Fields.Count, 260); i++)
                    {
                        PARAMDEF.Field fd = p.AppliedParamdef.Fields[i];
                        if (fd.DisplayType == PARAMDEF.DefType.dummy8) continue;
                        Console.Write($"     {fd.InternalName}={row.Cells[i].Value}  ");
                        if (++shown % 3 == 0) Console.WriteLine();
                    }
                    Console.WriteLine();
                }
            }
            return 0;
        }

        // ---------------- csvexport：导出参数表为 CSV（分析用） ----------------

        private static int CmdCsvExport(Dictionary<string, string> o)
        {
            string input = Val(o, "input") ?? @"D:\steam\steamapps\common\ELDEN RING\Game";
            string paramName = Val(o, "param") ?? "EquipParamGoods";
            string outPath = Val(o, "out") ?? Path.Combine(Directory.GetCurrentDirectory(), paramName + ".csv");
            int idMin = int.TryParse(Val(o, "idmin", int.MinValue.ToString()), out int lo) ? lo : int.MinValue;
            int idMax = int.TryParse(Val(o, "idmax", int.MaxValue.ToString()), out int hi) ? hi : int.MaxValue;

            string reg = Path.Combine(input, "regulation.bin");
            BND4 bnd = SFUtil.DecryptERRegulation(reg);
            BinderFile file = bnd.Files.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f.Name).Equals(paramName, StringComparison.OrdinalIgnoreCase));
            if (file == null) return Fail($"找不到参数表：{paramName}");

            string defs = EffectRandomizer.ResolveParamdefDir(Val(o, "defs"));
            var paramdefs = Directory.GetFiles(defs, "*.xml")
                .Select(p => { try { return PARAMDEF.XmlDeserialize(p); } catch { return null; } })
                .Where(d => d != null).ToList();

            PARAM param = PARAM.Read(file.Bytes);
            if (!param.ApplyParamdefCarefully(paramdefs)) return Fail("paramdef 未匹配");

            PARAMDEF def = param.AppliedParamdef;
            var wanted = Val(o, "attrs")?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            var cols = def.Fields.Where(f => f.DisplayType != PARAMDEF.DefType.dummy8)
                                 .Select(f => f.InternalName)
                                 .Where(n => wanted == null || wanted.Contains(n)).ToArray();

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", new[] { "ID" }.Concat(cols)));
            int written = 0;
            foreach (PARAM.Row row in param.Rows)
            {
                if (row.ID < idMin || row.ID > idMax) continue;
                var vals = new List<string> { row.ID.ToString() };
                for (int i = 0; i < def.Fields.Count; i++)
                {
                    if (def.Fields[i].DisplayType == PARAMDEF.DefType.dummy8) continue;
                    if (wanted != null && !wanted.Contains(def.Fields[i].InternalName)) continue;
                    vals.Add(Escape(row.Cells[i].Value?.ToString() ?? ""));
                }
                sb.AppendLine(string.Join(",", vals));
                written++;
            }

            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
            Console.WriteLine($"已导出 {written} 行到：{outPath}");
            Console.WriteLine($"列：{string.Join(", ", cols)}");
            return 0;
        }

        private static string Escape(string s) =>
            s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        // ---------------- randomize ----------------

        private static int CmdRandomize(Dictionary<string, string> o)
        {
            // ---- 召唤物设置：先默认（完整引擎 + 欢乐预设），再按命令行覆盖 ----
            var summon = new SummonSettings();
            if (!Flag(o, "summon-mild"))
            {
                summon.ApplyPreset(Val(o, "summon-preset") ?? "fun");
            }
            else
            {
                summon.Engine = SummonEngineMode.Mild;
            }
            string settingsString = Val(o, "summon-settings");
            if (!string.IsNullOrWhiteSpace(settingsString))
            {
                if (!summon.TryParseSettingsString(settingsString, out string parseErr))
                    return Fail("--summon-settings 解析失败：" + parseErr);
                Console.WriteLine("已套用原版 Summon Randomizer 设置串。");
            }
            if (int.TryParse(Val(o, "summon-initial"), out int si)) summon.MultiBuddyInitialChance = si;
            if (int.TryParse(Val(o, "summon-additional"), out int sa)) summon.MultiBuddyAdditionalChance = sa;
            if (int.TryParse(Val(o, "summon-max"), out int sm)) summon.MultiBuddyMax = sm;
            if (int.TryParse(Val(o, "summon-dupe"), out int sd)) summon.MultiBuddyDupeChance = sd;
            if (int.TryParse(Val(o, "summon-variant"), out int sv)) summon.VariantReuseChance = sv;
            if (Flag(o, "buddy-reuse")) summon.BuddyReuse = true;
            if (Flag(o, "allow-large-multi")) summon.ForbidLargeMultiBuddies = false;
            if (double.TryParse(Val(o, "summon-hp"), out double shp)) summon.HpMultiplier = shp;
            if (double.TryParse(Val(o, "summon-dmg"), out double sdmg)) summon.DamageMultiplier = sdmg;
            if (double.TryParse(Val(o, "summon-fp"), out double sfp)) summon.FpMultiplier = sfp;
            if (Flag(o, "no-summon-mult")) summon.ApplyMultipliers = false;
            if (Flag(o, "no-summon-costs")) summon.RandomizeAshCosts = false;
            if (int.TryParse(Val(o, "summon-fplo"), out int sfl)) summon.FpCostMin = sfl;
            if (int.TryParse(Val(o, "summon-fphi"), out int sfh)) summon.FpCostMax = sfh;
            if (int.TryParse(Val(o, "summon-hpchance"), out int shc)) summon.HpCostChance = shc;
            if (int.TryParse(Val(o, "summon-hplo"), out int shl)) summon.HpCostMin = shl;
            if (int.TryParse(Val(o, "summon-hphi"), out int shh)) summon.HpCostMax = shh;
            summon.Pool = string.Equals(Val(o, "summon-pool"), "same", StringComparison.OrdinalIgnoreCase)
                          ? SummonPoolScope.SameSegment : SummonPoolScope.All;
            if (Flag(o, "no-summon-ridden")) summon.RandomizeRidden = false;
            summon.Clamp();

            var settings = new RandomizerSettings
            {
                InputDir = Val(o, "input") ?? @"D:\steam\steamapps\common\ELDEN RING\Game",
                OutputDir = Val(o, "output"),   // 留空 = 就地覆盖游戏目录
                ParamdefDir = Val(o, "defs"),
                Seed = Val(o, "seed"),
                Mode = ParseMode(Val(o, "mode", "shuffle")),
                ShiftAmount = int.TryParse(Val(o, "shift", "1"), out int s) ? s : 1,
                RandomizeMagic = !Flag(o, "no-magic"),
                RandomizeAshOfWar = !Flag(o, "no-ash"),
                RandomizeGoods = !Flag(o, "no-goods"),
                RandomizeEquipment = !Flag(o, "no-equip"),
                NoOp = Flag(o, "noop"),
                RandomizeWeaponAshLink = Flag(o, "weapon-ash"),
                UnlockAshOfWarForAllWeapons = !Flag(o, "no-unlock-ash"),
                RandomizeSummons = !Flag(o, "no-summons"),
                Summons = summon,
                RandomizeTalks = Flag(o, "talks"),
                RandomizeWeaponMoveset = Flag(o, "weapon-motion"),
                IncludeNpcSpells = Flag(o, "npc-spells"),
                RandomizeCost = !Flag(o, "no-cost"),
                GoodsConservativeMode = !Flag(o, "goods-all"),
                BackupExisting = !Flag(o, "no-backup"),
                LoadBackup = Flag(o, "load-backup"),
            };

            if (settings.InputDir.Equals(settings.OutputDir, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("注意：输出目录与输入目录相同，将就地覆盖（已自动备份原文件）。");

            var sw = Stopwatch.StartNew();
            var randomizer = new EffectRandomizer(settings, m => Console.WriteLine("  " + m));
            RandomizeReport report = randomizer.Run();

            Console.WriteLine();
            Console.WriteLine("========== 随机化结果 ==========");
            Console.WriteLine($"种子        ：{report.Seed}");
            Console.WriteLine($"错位方式    ：{report.Mode}");
            Console.WriteLine($"输入        ：{report.InputPath}");
            Console.WriteLine($"输出        ：{report.OutputPath}");
            Console.WriteLine($"备份        ：{report.BackupPath ?? "（无）"}");
            Console.WriteLine($"用时        ：{report.Elapsed.TotalSeconds:F1} 秒");
            Console.WriteLine();
            Console.WriteLine("参数表明细：");
            foreach (ParamReport pr in report.Params)
                Console.WriteLine($"  {pr.ParamName,-22} {pr.ParamType,-28} 行 {pr.AffectedRows,6}/{pr.TotalRows,-6} 字段 {pr.AffectedFields,3}");
            if (report.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("警告：");
                foreach (string w in report.Warnings) Console.WriteLine("  ! " + w);
            }

            if (report.Safety != null)
            {
                Console.WriteLine();
                Console.WriteLine("---------- 关键道具保护自检 ----------");
                if (report.Safety.Errors.Count > 0)
                {
                    foreach (string e in report.Safety.Errors) Console.WriteLine("  ? " + e);
                }
                else
                {
                    foreach (SafetyCheckItem it in report.Safety.Items)
                        Console.WriteLine($"  {(it.Changed ? "✗" : "✓")} {it.Name,-18}（ID {it.ID,-6}）" +
                                          (it.Changed ? $"被改动 {it.ChangedFields} 个字段：{string.Join("；", it.Details)}" : "未被改动"));
                    Console.WriteLine(report.Safety.Passed
                        ? $"结论：{report.Safety.TotalChecked} 项受保护道具全部未被改动。"
                        : $"结论：异常！{report.Safety.ChangedCount} 项受保护道具被改动。");
                }
            }
            Console.WriteLine($"总耗时 {sw.Elapsed.TotalSeconds:F1} 秒");
            return 0;
        }

        private static ErringMode ParseMode(string s) => s?.ToLowerInvariant() switch
        {
            "shift" => ErringMode.Shift,
            "reverse" => ErringMode.Reverse,
            _ => ErringMode.Shuffle,
        };

        // ---------------- safetycheck：关键道具保护自检 ----------------

        private static int CmdSafetyCheck(Dictionary<string, string> o)
        {
            string a = Val(o, "input");
            string b = Val(o, "output");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return Fail("safetycheck 需要 --input <原始文件/目录> --output <待检查文件/目录>");

            if (Directory.Exists(a)) a = Path.Combine(a, "regulation.bin");
            if (Directory.Exists(b)) b = Path.Combine(b, "regulation.bin");

            Console.WriteLine($"原始：{a}");
            Console.WriteLine($"待检：{b}");
            Console.WriteLine();

            SafetyCheckReport r = SafetyAuditor.Check(a, b, Val(o, "defs"));
            if (r.Errors.Count > 0)
            {
                foreach (string e in r.Errors) Console.WriteLine("  ? " + e);
                return 1;
            }

            foreach (SafetyCheckItem it in r.Items)
                Console.WriteLine($"  {(it.Changed ? "✗" : "✓")} {it.Name,-18}（ID {it.ID,-6}）" +
                                  (it.Changed ? $"被改动 {it.ChangedFields} 个字段：{string.Join("；", it.Details)}" : "未被改动"));
            Console.WriteLine();
            Console.WriteLine(r.Passed
                ? $"结论：通过，{r.TotalChecked} 项受保护道具全部未被改动。"
                : $"结论：不通过！{r.ChangedCount}/{r.TotalChecked} 项被改动——这份文件是用旧规则生成的，请用最新版重新生成。");
            return r.Passed ? 0 : 2;
        }

        // ---------------- inspect：逐字段对比指定物品 ----------------

        private static int CmdInspect(Dictionary<string, string> o)
        {
            string a = Val(o, "input");
            string b = Val(o, "output");
            string idList = Val(o, "ids") ?? "130,2040";
            string paramName = Val(o, "param") ?? "EquipParamGoods";

            string defs = EffectRandomizer.ResolveParamdefDir(Val(o, "defs"));
            var paramdefs = Directory.GetFiles(defs, "*.xml")
                .Select(p => { try { return PARAMDEF.XmlDeserialize(p); } catch { return null; } })
                .Where(d => d != null).ToList();

            var ids = idList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(int.Parse).ToArray();

            Dictionary<int, PARAM.Row> Load(string path)
            {
                BND4 bnd = SFUtil.DecryptERRegulation(path);
                BinderFile f = bnd.Files.FirstOrDefault(x => Path.GetFileNameWithoutExtension(x.Name).Equals(paramName, StringComparison.OrdinalIgnoreCase));
                if (f == null) return new Dictionary<int, PARAM.Row>();
                PARAM p = PARAM.Read(f.Bytes);
                p.ApplyParamdefCarefully(paramdefs);
                return p.Rows.ToDictionary(r => r.ID);
            }

            var rowsA = Load(a);
            var rowsB = Load(b);
            Console.WriteLine($"A（原始）：{a}  行数 {rowsA.Count}");
            Console.WriteLine($"B（生成）：{b}  行数 {rowsB.Count}");
            Console.WriteLine();

            string[] fields =
            {
                "refId_default", "behaviorId", "spEffectCategory", "goodsUseAnim", "refCategory",
                "refId_1", "refVirtualWepId", "sfxVariationId", "castSfxId", "fireSfxId", "effectSfxId",
                "consumeMP", "consumeHP", "useEnableSpEffectType", "goodsType", "isConsume",
            };

            foreach (int id in ids)
            {
                bool hasA = rowsA.TryGetValue(id, out PARAM.Row ra);
                bool hasB = rowsB.TryGetValue(id, out PARAM.Row rb);
                Console.WriteLine($"=== ID={id} （A存在={hasA} B存在={hasB}）===");
                if (!hasA || !hasB) { Console.WriteLine("   缺行，跳过"); continue; }

                int diff = 0;
                foreach (string k in fields)
                {
                    object va, vb;
                    try { va = ra[k].Value; } catch { continue; }
                    try { vb = rb[k].Value; } catch { continue; }
                    bool changed = !Equals(va, vb);
                    if (changed) diff++;
                    Console.WriteLine($"   {k,-22} A={va,-14} B={vb,-14} {(changed ? "<== 被改动" : "")}");
                }
                Console.WriteLine($"   >>> 该物品被改动的字段数 = {diff}");
                Console.WriteLine();
            }

            return 0;
        }

        // ---------------- compare：比较两份生成文件的内容差异 ----------------

        private static int CmdCompare(Dictionary<string, string> o)
        {
            string a = Val(o, "input");
            string b = Val(o, "output");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return Fail("compare 需要 --input <文件A> --output <文件B>");

            string defs = EffectRandomizer.ResolveParamdefDir(Val(o, "defs"));
            var paramdefs = Directory.GetFiles(defs, "*.xml")
                .Select(p => { try { return PARAMDEF.XmlDeserialize(p); } catch { return null; } })
                .Where(d => d != null).ToList();

            BND4 ba = SFUtil.DecryptERRegulation(a);
            BND4 bb = SFUtil.DecryptERRegulation(b);

            Console.WriteLine($"A：{a}");
            Console.WriteLine($"B：{b}");
            Console.WriteLine($"文件数：A {ba.Files.Count}，B {bb.Files.Count}");
            Console.WriteLine($"文件 SHA256 相同：{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(a))) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(b)))}");
            Console.WriteLine();

            var mapB = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (BinderFile f in bb.Files)
                mapB[Path.GetFileNameWithoutExtension(f.Name)] = f.Bytes;

            int differTables = 0;
            foreach (BinderFile fa in ba.Files)
            {
                string name = Path.GetFileNameWithoutExtension(fa.Name);
                if (!mapB.TryGetValue(name, out byte[] bbBytes)) { Console.WriteLine($"{name}: B 中缺失"); differTables++; continue; }

                if (!fa.Bytes.AsSpan().SequenceEqual(bbBytes))
                {
                    var s1 = System.Security.Cryptography.SHA256.HashData(fa.Bytes);
                    var s2 = System.Security.Cryptography.SHA256.HashData(bbBytes);
                    Console.WriteLine($"{name}: 原始字节不同（{fa.Bytes.Length} vs {bbBytes.Length}）" +
                                      $" SHA={Convert.ToHexString(s1.AsSpan(0, 6))}/{Convert.ToHexString(s2.AsSpan(0, 6))}");
                }

                PARAM pa = PARAM.Read(fa.Bytes);
                PARAM pb = PARAM.Read(bbBytes);

                if (pa.Rows.Count != pb.Rows.Count)
                {
                    Console.WriteLine($"{name}: 行数不同 {pa.Rows.Count} vs {pb.Rows.Count}");
                    differTables++;
                    continue;
                }

                if (!pa.ApplyParamdefCarefully(paramdefs) || !pb.ApplyParamdefCarefully(paramdefs))
                {
                    if (!fa.Bytes.AsSpan().SequenceEqual(bbBytes)) { Console.WriteLine($"{name}: 字节不同（无 paramdef）"); differTables++; }
                    continue;
                }

                int diffCells = 0;
                var fields = new Dictionary<string, int>(StringComparer.Ordinal);
                // 行数与原始字节都一致时按位置逐行比较（同一处理流程输出必然同序）；
                // 否则退回按 ID 匹配。
                bool positional = fa.Bytes.AsSpan().SequenceEqual(bbBytes);
                var rowsB = new Dictionary<int, PARAM.Row>();
                if (!positional)
                    foreach (PARAM.Row r in pb.Rows) if (!rowsB.ContainsKey(r.ID)) rowsB[r.ID] = r;

                for (int ri = 0; ri < pa.Rows.Count; ri++)
                {
                    PARAM.Row ra = pa.Rows[ri];
                    PARAM.Row rb = positional ? pb.Rows[ri] : (rowsB.TryGetValue(ra.ID, out PARAM.Row found) ? found : null);
                    if (rb == null) { diffCells++; continue; }
                    if (positional && ra.ID != rb.ID) { diffCells++; continue; }
                    for (int i = 0; i < pa.AppliedParamdef.Fields.Count && i < ra.Cells.Count; i++)
                    {
                        if (pa.AppliedParamdef.Fields[i].DisplayType == PARAMDEF.DefType.dummy8) continue;
                        if (!Equals(ra.Cells[i].Value, rb.Cells[i].Value))
                        {
                            diffCells++;
                            string fn = pa.AppliedParamdef.Fields[i].InternalName;
                            fields[fn] = fields.TryGetValue(fn, out int c) ? c + 1 : 1;
                        }
                    }
                }

                if (diffCells > 0)
                {
                    differTables++;
                    Console.WriteLine($"{name}: 字段值不同 {diffCells} 处 -> {string.Join(", ", fields.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}×{kv.Value}"))}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"字段内容有差异的参数表：{differTables} 个");
            return 0;
        }

        // ---------------- selftest：PARAM 读写是否幂等 ----------------

        private static int CmdSelfTest(Dictionary<string, string> o)
        {
            string input = Val(o, "input") ?? @"D:\steam\steamapps\common\ELDEN RING\Game";
            string reg = Path.Combine(input, "regulation.bin");
            Console.WriteLine($"读取：{reg}");
            BND4 bnd = SFUtil.DecryptERRegulation(reg);

            string defs = EffectRandomizer.ResolveParamdefDir(Val(o, "defs"));
            var paramdefs = Directory.GetFiles(defs, "*.xml")
                .Select(p => { try { return PARAMDEF.XmlDeserialize(p); } catch { return null; } })
                .Where(d => d != null).ToList();

            int identical = 0, changed = 0, noDef = 0;
            foreach (BinderFile f in bnd.Files)
            {
                byte[] before = f.Bytes;
                PARAM p = PARAM.Read(before);
                if (!p.ApplyParamdefCarefully(paramdefs)) { noDef++; continue; }
                byte[] after = p.Write();

                if (before.AsSpan().SequenceEqual(after))
                {
                    identical++;
                }
                else
                {
                    changed++;
                    long diff = 0;
                    int n = Math.Min(before.Length, after.Length);
                    for (int i = 0; i < n; i++) if (before[i] != after[i]) diff++;
                    Console.WriteLine($"  重写后不同的表：{Path.GetFileName(f.Name),-40} 原长 {before.Length,8} 新长 {after.Length,8} 差异起点字节 {diff}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"PARAM 读→写 幂等：{identical} 个表字节完全一致，{changed} 个表被重写规范化，{noDef} 个表无 paramdef。");
            return 0;
        }

        // ---------------- summons：召唤物数据专项校验 ----------------

        private static int CmdSummons(Dictionary<string, string> o)
        {
            string a = Val(o, "input");
            string b = Val(o, "output");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return Fail("summons 需要 --input <原始文件> --output <生成文件>");

            string defs = EffectRandomizer.ResolveParamdefDir(Val(o, "defs"));
            var paramdefs = Directory.GetFiles(defs, "*.xml")
                .Select(p => { try { return PARAMDEF.XmlDeserialize(p); } catch { return null; } })
                .Where(d => d != null).ToList();

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

            var A = LoadAll(a, paramdefs);
            var B = LoadAll(b, paramdefs);

            static long V(PARAM.Row r, string f) { try { return Convert.ToInt64(r[f].Value); } catch { return long.MinValue; } }
            static double F(PARAM.Row r, string f) { try { return Convert.ToDouble(r[f].Value); } catch { return double.NaN; } }

            Console.WriteLine($"原始：{a}");
            Console.WriteLine($"生成：{b}");
            Console.WriteLine();

            int problems = 0;
            void Problem(string msg) { problems++; Console.WriteLine("  ✗ " + msg); }

            // ---- 1) BuddyParam 结构 ----
            PARAM buddyB = B["BuddyParam"];
            PARAM npcB = B["NpcParam"];
            PARAM thinkB = B["NpcThinkParam"];
            PARAM spB = B["SpEffectParam"];
            PARAM goodsB = B["EquipParamGoods"];
            PARAM spA = A["SpEffectParam"];

            var npcIds = npcB.Rows.Select(r => r.ID).ToHashSet();
            var thinkIds = thinkB.Rows.Select(r => r.ID).ToHashSet();
            var npcById = npcB.Rows.ToDictionary(r => r.ID);

            var baseRows = buddyB.Rows.Where(r => r.ID > 0 && r.ID % 100 == 0).ToList();
            var childRows = buddyB.Rows.Where(r => r.ID > 0 && r.ID % 100 != 0).ToList();
            Console.WriteLine($"== BuddyParam ==  基行 {baseRows.Count}，多召子行 {childRows.Count}");

            int invalid = 0, mimicBase = 0, mimicChild = 0, tamedOk = 0, aiOk = 0, cloneNamed = 0;
            foreach (PARAM.Row r in buddyB.Rows)
            {
                if (r.ID <= 0) continue;
                int baseId = r.ID - r.ID % 100;
                long npc = V(r, "npcParamId"), think = V(r, "npcThinkParamId");

                if (baseId == 20700000)
                {
                    if (r.ID == baseId) { if (npc == 133201000 && think == 133200000) mimicBase++; else Problem($"仿身泪滴本体 {r.ID} 配置异常：{npc}/{think}"); }
                    else { if (npc == 100000010 && think == 100000010 && V(r, "npcPlayerInitParamId") == 26050) mimicChild++; else Problem($"仿身泪滴多召 {r.ID} 配置异常：{npc}/{think}/{V(r, "npcPlayerInitParamId")}"); }
                    continue;
                }

                if (npc <= 0 || think <= 0) { invalid++; Problem($"骨灰 {r.ID} 的敌人/AI 未分配：{npc}/{think}"); continue; }
                if (!npcIds.Contains((int)npc)) { invalid++; Problem($"骨灰 {r.ID} 指向不存在的 NPC {npc}"); continue; }
                if (!thinkIds.Contains((int)think)) { invalid++; Problem($"骨灰 {r.ID} 指向不存在的 AI {think}"); continue; }

                PARAM.Row nr = npcById[(int)npc];
                PARAM.Row tr = thinkB.Rows.First(x => x.ID == (int)think);
                bool named = (nr.Name ?? "").StartsWith("buddyRando");
                if (named) cloneNamed++;
                if (V(nr, "teamType") == 47) tamedOk++; else Problem($"克隆 {npc} teamType={V(nr, "teamType")}（应为 47）");
                if (Convert.ToBoolean(tr["isBuddyAI"].Value)) aiOk++; else Problem($"AI 克隆 {think} isBuddyAI 未置真");
            }
            Console.WriteLine($"  指向有效 NPC/AI：全部通过（未分配 {invalid} 个）");
            Console.WriteLine($"  仿身泪滴：本体 {mimicBase}，多召玩家复制体 {mimicChild}");
            Console.WriteLine($"  驯服克隆：teamType=47 {tamedOk} 个；AI isBuddyAI {aiOk} 个；带 buddyRando 标记 {cloneNamed} 个");

            // ---- 2) 克隆行存在性与排序 ----
            int marked = npcB.Rows.Count(r => (r.Name ?? "").StartsWith("buddyRando")) +
                         thinkB.Rows.Count(r => (r.Name ?? "").StartsWith("buddyRando"));
            Console.WriteLine($"== 克隆行 ==  NpcParam {npcB.Rows.Count} 行（新克隆 {marked} 个带标记），NpcThinkParam {thinkB.Rows.Count} 行");
            foreach ((string tn, PARAM tp) in new[] { ("BuddyParam", buddyB), ("NpcParam", npcB), ("NpcThinkParam", thinkB) })
            {
                bool sorted = true;
                for (int i = 1; i < tp.Rows.Count; i++) if (tp.Rows[i].ID < tp.Rows[i - 1].ID) { sorted = false; break; }
                if (!sorted) Problem($"{tn} 行未按 ID 排序");
            }

            // ---- 3) 倍率 ----
            Console.WriteLine("== SpEffect 290000-290010 倍率 ==");
            for (int i = 0; i <= 10; i++)
            {
                PARAM.Row ra = spA.Rows.FirstOrDefault(r => r.ID == 290000 + i);
                PARAM.Row rb = spB.Rows.FirstOrDefault(r => r.ID == 290000 + i);
                if (ra == null || rb == null) { Problem($"SpEffect {290000 + i} 缺失"); continue; }
                if (i == 0 || i == 10)
                    Console.WriteLine($"  {290000 + i}: maxHpRate {F(ra, "maxHpRate")} → {F(rb, "maxHpRate")}，" +
                                      $"物理攻击 {F(ra, "physicsAttackPowerRate")} → {F(rb, "physicsAttackPowerRate")}");
            }

            // ---- 4) 骨灰消耗 ----
            var ashA = goodsB.Rows.Where(r => r.ID > 0 && V(r, "goodsUseAnim") == 34).ToList();
            int zeroed = ashA.Count(r => V(r, "consumeMP") == 0 && V(r, "consumeHP") == 0);
            int mpRange = ashA.Count(r => r.ID % 100 == 0 && V(r, "consumeMP") > 0);
            int hpSet = ashA.Count(r => r.ID % 100 == 0 && V(r, "consumeHP") > 0);
            Console.WriteLine($"== 骨灰消耗 ==  共 {ashA.Count} 行；清零 {zeroed}；基行 FP>0 的 {mpRange} 个，HP>0 的 {hpSet} 个");

            // ---- 5) 受保护道具仍未被改动（快速抽查）----
            PARAM goodsA = A["EquipParamGoods"];
            int[] protect = { 130, 181, 2040, 2070, 250, 251, 1000, 1050, 115 };
            var aById = goodsA.Rows.ToDictionary(r => r.ID);
            var bById = goodsB.Rows.ToDictionary(r => r.ID);
            foreach (int id in protect)
            {
                if (!bById.TryGetValue(id, out PARAM.Row rb)) { Problem($"受保护道具 {id} 丢失"); continue; }
                if (!aById.TryGetValue(id, out PARAM.Row ra)) continue;
                if (V(ra, "goodsUseAnim") != V(rb, "goodsUseAnim") || V(ra, "refId_default") != V(rb, "refId_default"))
                    Problem($"受保护道具 {id} 被改动：anim {V(ra, "goodsUseAnim")}→{V(rb, "goodsUseAnim")}，refId {V(ra, "refId_default")}→{V(rb, "refId_default")}");
            }
            Console.WriteLine("== 受保护道具抽查 ==  完成");

            Console.WriteLine();
            Console.WriteLine(problems == 0 ? "结论：召唤物数据校验全部通过。" : $"结论：发现 {problems} 个问题！");
            return problems == 0 ? 0 : 2;
        }

        // ---------------- verify：逐表比对改动范围 ----------------

        private static int CmdVerify(Dictionary<string, string> o)
        {
            string a = Val(o, "input");
            string b = Val(o, "output");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return Fail("verify 需要 --input <原始文件> --output <生成文件>");

            string defs = EffectRandomizer.ResolveParamdefDir(Val(o, "defs"));
            var paramdefs = Directory.GetFiles(defs, "*.xml")
                .Select(p => { try { return PARAMDEF.XmlDeserialize(p); } catch { return null; } })
                .Where(d => d != null).ToList();

            Console.WriteLine($"原始：{a}");
            Console.WriteLine($"生成：{b}");
            BND4 bndA = SFUtil.DecryptERRegulation(a);
            BND4 bndB = SFUtil.DecryptERRegulation(b);
            Console.WriteLine($"参数文件数：原始 {bndA.Files.Count}，生成 {bndB.Files.Count}");

            var mapB = bndB.Files.ToDictionary(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase);

            int totalDiffTables = 0;
            long totalDiffBytes = 0;
            int totalDiffRows = 0;
            long unexpectedTotal = 0;

            Console.WriteLine();
            Console.WriteLine($"{"参数表",-32}{"行数",8}{"差异行",8}{"差异单元格",12}  说明");

            foreach (BinderFile fa in bndA.Files)
            {
                string name = Path.GetFileNameWithoutExtension(fa.Name);
                if (!mapB.TryGetValue(name, out BinderFile fb)) { Console.WriteLine($"{name,-32}  生成文件里缺失！"); continue; }

                PARAM pa = PARAM.Read(fa.Bytes);
                PARAM pb = PARAM.Read(fb.Bytes);

                if (pa.Rows.Count != pb.Rows.Count)
                {
                    totalDiffTables++;
                    totalDiffRows += Math.Abs(pa.Rows.Count - pb.Rows.Count);
                    Console.WriteLine($"{name,-32}{pa.Rows.Count,8}{"变化",8}{"-",12}  行数变化!");
                    continue;
                }

                bool applied = pa.ApplyParamdefCarefully(paramdefs) & pb.ApplyParamdefCarefully(paramdefs);
                if (!applied)
                {
                    bool sameLen = fa.Bytes.Length == fb.Bytes.Length;
                    Console.WriteLine($"{name,-32}{pa.Rows.Count,8}{(sameLen ? 0 : -1),8}{"-",12}  无 paramdef（字节长度{(sameLen ? "一致" : "不一致")}）");
                    continue;
                }

                var rowsB = new Dictionary<int, PARAM.Row>();
                foreach (PARAM.Row r in pb.Rows) rowsB[r.ID] = r;

                int diffRows = 0, diffCells = 0;
                var fieldHits = new Dictionary<string, int>(StringComparer.Ordinal);
                var targetFields = new HashSet<string>(StringComparer.Ordinal)
                {
                    "behaviorId","ezStateBehaviorType","spEffectCategory","refType","hasSpEffectType",
                    "replaceMagicId","replaceCategory","createLimitGroupId",
                    "swordArtsType","swordArtsTypeNew","artsSpeedType","refStatus",
                    "refId_default","refId_1","refVirtualWepId","goodsUseAnim","useEnableSpEffectType",
                    "refId","refCategory","residentSpEffectId1","residentSpEffectId2",
                    "residentSpEffectId3","residentSpEffectId4",
                    "residentSpEffectId","spEffectBehaviorId0","spEffectBehaviorId1","spEffectBehaviorId2",
                    "behaviorVariationId","absorpParamId","attackElementCorrectId",
                    "spAttribute","spAtkcategory","spAttributeVariationValue","swordArtsParamId",
                    "mp","stamina","mp_charge","stamina_charge","consumeSA",
                    "requirementIntellect","requirementFaith","requirementLuck","slotLength",
                    "usePoint_L1","usePoint_L2","usePoint_R1","usePoint_R2",
                    "useMagicPoint_L1","useMagicPoint_L2","useMagicPoint_R1","useMagicPoint_R2",
                    "consumeMP","consumeHP","consumeHeroPoint","useLimitSpEffectType",
                    "sfxVariationId","castSfxId","fireSfxId","effectSfxId",
                    "ReplacementStatusType","ReplacementStatus1","ReplacementStatus2",
                    "ReplacementStatus3","ReplacementStatus4",
                    "ReplacementMagic1","ReplacementMagic2","ReplacementMagic3","ReplacementMagic4",
                    // 召唤物随机化
                    "npcParamId","npcThinkParamId","npcParamId_ridden","npcThinkParamId_ridden",
                    "npcPlayerInitParamId","generateAnimId","appearOnAroundSekihi","pcFollowType",
                    "x_offset","z_offset","teamType","itemLotId_enemy","itemLotId_map",
                    "GameClearSpEffectID","getSoul","hitRadius","hitHeight","chrHitRadius","isBuddyAI",
                    "maxHpRate","physicsAttackPowerRate","magicAttackPowerRate","fireAttackPowerRate",
                    "thunderAttackPowerRate","darkAttackPowerRate","behaviorVariationId","mp",
                    // 对话 / 武器动作模组
                    "msgId","voiceId","msgId_female","voiceId_female","spEffectId0","motionId0",
                    "spEffectId1","motionId1","returnPos","reactionId","eventId",
                    "lipSyncStart","lipSyncTime","timeout","talkAnimationId",
                    "wepmotionCategory","wepmotionOneHandId","wepmotionBothHandId",
                };
                for (int i = 0; i <= 31; i++) targetFields.Add($"spEffectID{i}");
                for (int i = 0; i <= 10; i++) targetFields.Add($"dopingSpEffect_lv{i}");
                for (int i = 1; i <= 10; i++)
                {
                    targetFields.Add($"refCategory{i}");
                    targetFields.Add($"refId{i}");
                    targetFields.Add($"consumeType{i}");
                }

                var unexpected = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (PARAM.Row ra in pa.Rows)
                {
                    if (!rowsB.TryGetValue(ra.ID, out PARAM.Row rb)) { diffRows++; continue; }
                    bool rowDiff = false;
                    for (int i = 0; i < pa.AppliedParamdef.Fields.Count && i < ra.Cells.Count; i++)
                    {
                        PARAMDEF.Field fd = pa.AppliedParamdef.Fields[i];
                        if (fd.DisplayType == PARAMDEF.DefType.dummy8) continue;
                        object va = ra.Cells[i].Value;
                        object vb = rb.Cells[i].Value;
                        if (Equals(va, vb)) continue;

                        diffCells++;
                        rowDiff = true;
                        string fn = fd.InternalName;
                        fieldHits[fn] = fieldHits.TryGetValue(fn, out int c) ? c + 1 : 1;
                        if (!targetFields.Contains(fn))
                            unexpected[fn] = unexpected.TryGetValue(fn, out int u) ? u + 1 : 1;
                    }
                    if (rowDiff) diffRows++;
                }

                if (diffRows > 0 || diffCells > 0)
                {
                    totalDiffTables++;
                    totalDiffRows += diffRows;
                    totalDiffBytes += diffCells;

                    string top = string.Join(", ", fieldHits.OrderByDescending(kv => kv.Value).Take(8)
                        .Select(kv => $"{kv.Key}×{kv.Value}"));
                    Console.WriteLine($"{name,-32}{pa.Rows.Count,8}{diffRows,8}{diffCells,12}  {top}");

                    if (unexpected.Count > 0)
                    {
                        Console.WriteLine($"    !! 非目标字段也被改动：{string.Join(", ", unexpected.Select(kv => $"{kv.Key}×{kv.Value}"))}");
                        unexpectedTotal += unexpected.Values.Sum();
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"汇总：有差异的参数表 {totalDiffTables} 个，改动行 {totalDiffRows}，改动单元格 {totalDiffBytes}");
            Console.WriteLine($"非目标字段改动：{unexpectedTotal} 处{(unexpectedTotal == 0 ? "（全部改动都在预期字段内）" : "（需要排查！）")}");
            Console.WriteLine($"结构：文件数 {(bndA.Files.Count == bndB.Files.Count ? "一致" : "不一致!")}");
            return 0;
        }
    }
}
