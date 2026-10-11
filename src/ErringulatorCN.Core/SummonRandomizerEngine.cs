using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SoulsFormats;

namespace ErringulatorCN.Core
{
    /// <summary>完整召唤物随机化的运行结果。</summary>
    public sealed class SummonRunResult
    {
        public List<ParamReport> Reports { get; } = new List<ParamReport>();
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>本次生成的驯服克隆 NPC ID（供 FP 倍率等后续步骤使用）。</summary>
        public List<int> CloneNpcIds { get; } = new List<int>();

        public int PoolSize { get; set; }
        public int BuddyBases { get; set; }
        public int BuddyChildren { get; set; }
        public int ClonesCreated { get; set; }
        public int CostRowsTouched { get; set; }
        public int CleanedCloneRows { get; set; }

        /// <summary>因为没找到 3 个空效果槽而未能补满友方特效的克隆数量。</summary>
        public int ShortEffectSlots { get; set; }

        /// <summary>true 表示因缺少必要参数表/有效池而整体跳过（文件未被修改）。</summary>
        public bool Skipped { get; set; }
    }

    /// <summary>
    /// 完整版召唤物随机化引擎——忠实移植 Elden Ring Summon Randomizer v1.0.8 的算法：
    ///
    ///   1. 从 NpcParam 实时构建敌人池（behaviorVariationId &gt; 10000 且 ID &lt; 89000000，
    ///      并用数字截断法到 NpcThinkParam 里找配套 AI）——不需要它自带的
    ///      Resources\EnemyInfoResource.txt（那份文件在 v1.0.8 里其实是死代码）。
    ///   2. 多召展开：按几率给骨灰条目插入 0~N 个子行，一次召出多个敌人。
    ///   3. 敌人选择：骨灰复用 / 同族变体复用概率 / 多召重复几率 / 大体积多召限制。
    ///   4. 克隆驯服：把选中的敌人克隆成新的 NpcParam + NpcThinkParam，
    ///      teamType=47（友方）、清除掉落与魂、限制碰撞体积、补友方特效、AI 标记 isBuddyAI。
    ///   5. 仿身泪滴（Buddy 20700000）特例：本体保持原版，多召的是玩家复制体。
    ///   6. 骨灰召唤消耗重掷（FP / HP 范围）。
    ///   7. 强化倍率：血量与攻击倍率以「加法」落在 SpEffect 290000-290010 上（与原版一致，
    ///      与骨灰强化曲线叠加）。
    ///
    /// 与原版的差异（都属于安全/体验改进，不改变游戏内效果）：
    ///   · 仿身泪滴分支不再创建用不到的孤儿克隆行；
    ///   · 每次运行先清理上一轮生成的克隆行（名字以 buddyRando 开头），可反复运行不膨胀；
    ///   · 敌人选择陷入极端重试时不再报错中止，而是强制接受并记录警告；
    ///   · 写回前把行按 ID 排序（保证参数表结构规范）。
    /// </summary>
    public sealed class SummonRandomizerEngine
    {
        /// <summary>克隆行名字前缀——同时作为"本工具/原版工具生成的克隆"的清理标记。</summary>
        public const string CloneNamePrefix = "buddyRando";

        /// <summary>仿身泪滴骨灰的 Buddy ID。</summary>
        public const int MimicBuddyId = 20700000;

        /// <summary>多召子行允许的最大碰撞体积（hitHeight + hitRadius）。</summary>
        private const double MaxMultiBuddySize = 6.0;

        private readonly SummonSettings _s;
        private readonly List<PARAMDEF> _paramdefs;
        private readonly Action<string> _log;

        public SummonRandomizerEngine(SummonSettings settings, List<PARAMDEF> paramdefs,
            Action<string> log = null)
        {
            _s = settings ?? throw new ArgumentNullException(nameof(settings));
            _paramdefs = paramdefs ?? throw new ArgumentNullException(nameof(paramdefs));
            _log = log;
        }

        private void Log(string message) => _log?.Invoke(message);

        // ==================== 带字段名索引的参数表封装 ====================

        private sealed class ParamRef
        {
            public string Name;
            public BinderFile File;
            public PARAM Param;
            public Dictionary<string, int> Fields;

            public long GetInt(PARAM.Row row, string field) =>
                Fields.TryGetValue(field, out int i) && row.Cells[i].Value != null
                    ? SafeToLong(row.Cells[i].Value)
                    : 0;

            public double GetDbl(PARAM.Row row, string field) =>
                Fields.TryGetValue(field, out int i) && row.Cells[i].Value != null
                    ? SafeToDouble(row.Cells[i].Value)
                    : 0;

            public bool SetInt(PARAM.Row row, string field, long value) =>
                Fields.TryGetValue(field, out int i) && SetCellInt(row, i, value);

            public bool SetDbl(PARAM.Row row, string field, double value) =>
                Fields.TryGetValue(field, out int i) && SetCellDbl(row, i, value);

            public bool Has(string field) => Fields.ContainsKey(field);

            public PARAM.Row Find(int id) => Param.Rows.Find(r => r.ID == id);
        }

        private static long SafeToLong(object v)
        {
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static double SafeToDouble(object v)
        {
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        /// <summary>按单元格当前类型写入整数（byte/sbyte/short/ushort/int/uint/long/ulong 都能接）。</summary>
        private static bool SetCellInt(PARAM.Row row, int idx, long value)
        {
            try
            {
                object cur = row.Cells[idx].Value;
                switch (cur)
                {
                    case byte _: row.Cells[idx].Value = unchecked((byte)value); return true;
                    case sbyte _: row.Cells[idx].Value = unchecked((sbyte)value); return true;
                    case short _: row.Cells[idx].Value = unchecked((short)value); return true;
                    case ushort _: row.Cells[idx].Value = unchecked((ushort)value); return true;
                    case int _: row.Cells[idx].Value = unchecked((int)value); return true;
                    case uint _: row.Cells[idx].Value = unchecked((uint)value); return true;
                    case long _: row.Cells[idx].Value = value; return true;
                    case ulong _: row.Cells[idx].Value = unchecked((ulong)value); return true;
                    case bool _: row.Cells[idx].Value = value != 0; return true;
                    default:
                        row.Cells[idx].Value = Convert.ChangeType(value, cur?.GetType() ?? typeof(int), CultureInfo.InvariantCulture);
                        return true;
                }
            }
            catch { return false; }
        }

        private static bool SetCellDbl(PARAM.Row row, int idx, double value)
        {
            try
            {
                object cur = row.Cells[idx].Value;
                if (cur is float) { row.Cells[idx].Value = (float)value; return true; }
                if (cur is double) { row.Cells[idx].Value = value; return true; }
                if (cur is decimal) { row.Cells[idx].Value = (decimal)value; return true; }
                row.Cells[idx].Value = Convert.ChangeType(value, cur?.GetType() ?? typeof(float), CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private ParamRef Load(BND4 regulation, string name, bool required, SummonRunResult res)
        {
            BinderFile file = regulation.Files.FirstOrDefault(f =>
                System.IO.Path.GetFileNameWithoutExtension(f.Name).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (file == null)
            {
                if (required) res.Warnings.Add($"参数表 {name} 不存在，召唤物随机化已跳过。");
                return null;
            }
            PARAM p = PARAM.Read(file.Bytes);
            if (!p.ApplyParamdefCarefully(_paramdefs))
            {
                if (required) res.Warnings.Add($"参数表 {name} 找不到匹配的 paramdef，召唤物随机化已跳过。");
                return null;
            }
            var fields = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < p.AppliedParamdef.Fields.Count; i++)
                fields[p.AppliedParamdef.Fields[i].InternalName] = i;

            return new ParamRef { Name = name, File = file, Param = p, Fields = fields };
        }

        /// <summary>克隆一行：全新 ID、复制全部单元格值、自定义行名，插到源行之后。</summary>
        private static PARAM.Row CloneRow(PARAM param, PARAM.Row source, int newId, string name)
        {
            var row = new PARAM.Row(newId, name, param.AppliedParamdef);
            for (int i = 0; i < row.Cells.Count && i < source.Cells.Count; i++)
                row.Cells[i].Value = source.Cells[i].Value;
            param.Rows.Insert(param.Rows.IndexOf(source) + 1, row);
            return row;
        }

        // ==================== 主流程 ====================

        public SummonRunResult Run(BND4 regulation, string seed)
        {
            var res = new SummonRunResult();
            var rng = new Random(Permute.SeedToInt(seed + "|summon"));
            _s.Clamp();

            // ---- 读取参数表 ----
            ParamRef buddy = Load(regulation, "BuddyParam", true, res);
            ParamRef npc = Load(regulation, "NpcParam", true, res);
            ParamRef think = Load(regulation, "NpcThinkParam", true, res);
            if (buddy == null || npc == null || think == null)
            {
                res.Skipped = true;
                return res;
            }
            ParamRef sp = Load(regulation, "SpEffectParam", false, res);
            ParamRef goods = Load(regulation, "EquipParamGoods", false, res);

            if (!buddy.Has("npcParamId") || !buddy.Has("npcThinkParamId"))
            {
                res.Skipped = true;
                res.Warnings.Add("BuddyParam 缺少 npcParamId / npcThinkParamId 字段，召唤物随机化已跳过。");
                return res;
            }

            // ---- 1) 构建敌人池（在修改任何数据之前完成）----
            Log("召唤物随机化：正在构建敌人池……");
            var thinkById = new HashSet<int>();
            Dictionary<int, PARAM.Row> thinkRowById = new Dictionary<int, PARAM.Row>();
            foreach (PARAM.Row r in think.Param.Rows)
            {
                if (r.ID <= 0) continue;
                if ((r.Name ?? "").StartsWith(CloneNamePrefix, StringComparison.Ordinal)) continue;
                thinkById.Add(r.ID);
                thinkRowById[r.ID] = r;
            }

            var pool = new List<(int NpcId, int ThinkId, PARAM.Row NpcRow)>();
            int excludedNoThink = 0;
            foreach (PARAM.Row r in npc.Param.Rows)
            {
                int id = r.ID;
                if (id <= 0 || id >= 89000000) continue;
                if ((r.Name ?? "").StartsWith(CloneNamePrefix, StringComparison.Ordinal)) continue;
                if (npc.GetInt(r, "behaviorVariationId") <= 10000) continue;

                int thinkId = ResolveThinkId(id, thinkById);
                if (thinkId == 0) { excludedNoThink++; continue; }
                pool.Add((id, thinkId, r));
            }
            res.PoolSize = pool.Count;
            Log($"召唤物随机化：敌人池 {pool.Count} 个候选" +
                (excludedNoThink > 0 ? $"（另有 {excludedNoThink} 个因找不到配套 AI 被排除）" : "") + "。");

            if (pool.Count < 2)
            {
                res.Skipped = true;
                res.Warnings.Add($"敌人池只有 {pool.Count} 个候选（至少需要 2 个），召唤物随机化已跳过，文件未被修改。");
                return res;
            }

            var fullPool = new List<(int NpcId, int ThinkId, PARAM.Row NpcRow)>(pool);
            bool poolFallbackUsed = false;

            // ---- 2) 清理上一轮生成的克隆行 ----
            res.CleanedCloneRows = RemoveMarkedRows(npc) + RemoveMarkedRows(think);
            if (res.CleanedCloneRows > 0)
                Log($"召唤物随机化：已清理上一轮生成的克隆行 {res.CleanedCloneRows} 个。");

            // ---- 3) BuddyParam：清空旧数据 + 多召展开 ----
            List<PARAM.Row> rows = buddy.Param.Rows;
            int bases = 0, children = 0;
            for (int j = 0; j < rows.Count; j++)
            {
                PARAM.Row row = rows[j];
                if (row.ID == 0) continue;
                if (row.ID % 100 != 0)
                {
                    rows.RemoveAt(j);   // 原版行为：旧的多召子行全部清除后按设置重建
                    j--;
                    continue;
                }
                bases++;
                buddy.SetInt(row, "npcParamId", -1);

                if (_s.MultiBuddyMax > 1 && rng.Next(1, 100) <= _s.MultiBuddyInitialChance)
                {
                    CloneRow(buddy.Param, rows[j], rows[j].ID + 1, CloneNamePrefix + " " + (rows[j].ID + 1));
                    j++; children++;
                    for (int k = 1; k < _s.MultiBuddyMax; k++)
                    {
                        if (rng.Next(1, 100) <= _s.MultiBuddyAdditionalChance)
                        {
                            CloneRow(buddy.Param, rows[j], rows[j].ID + 1, CloneNamePrefix + " " + (rows[j].ID + 1));
                            j++; children++;
                        }
                        else break;
                    }
                }
            }
            res.BuddyBases = bases;
            res.BuddyChildren = children;
            Log($"召唤物随机化：处理 {bases} 个骨灰条目，其中生成 {children} 个多召副本。");

            var byId = new Dictionary<int, PARAM.Row>();
            foreach (PARAM.Row r in rows)
                if (r.ID > 0) byId[r.ID] = r;

            var usedNpcIds = new HashSet<int>(npc.Param.Rows.Select(r => r.ID));
            var usedThinkIds = new HashSet<int>(think.Param.Rows.Select(r => r.ID));

            // ---- 4) 逐个骨灰选择敌人 + 克隆驯服 ----
            int shortSlots = 0;
            int assignmentFailures = 0;
            for (int l = 0; l < rows.Count; l++)
            {
                PARAM.Row row3 = rows[l];
                if (row3.ID == 0) continue;

                int num9 = row3.ID - row3.ID % 100;
                bool flag = l > 0 && row3.ID - rows[l - 1].ID == 1;
                bool isChild = row3.ID % 100 != 0;
                bool isMimic = num9 == MimicBuddyId;

                if (pool.Count == 0)
                {
                    if (!poolFallbackUsed)
                    {
                        poolFallbackUsed = true;
                        res.Warnings.Add("敌人池在分配过程中被耗尽（「允许骨灰重复」关闭且候选太少），" +
                                         "已自动回退为允许重复。建议开启「允许骨灰重复」或减少多召。");
                        Log("召唤物随机化：敌人池耗尽，自动回退为允许重复。");
                    }
                    pool = new List<(int NpcId, int ThinkId, PARAM.Row NpcRow)>(fullPool);
                    if (pool.Count == 0) { assignmentFailures++; continue; }
                }

                // ---- 4a) 选择候选 ----
                // 与已分配的敌人比较：同族时按「变体复用几率」决定接受或重选；
                // 多召子行不允许体积过大的敌人（否则一群巨型敌人会挤在一起）。
                int num3 = rng.Next(0, pool.Count);
                long scanCount = 0;
                bool accepted = false;
                while (!accepted)
                {
                    accepted = true;
                    for (int m = 0; m < rows.Count; m++)
                    {
                        scanCount++;
                        if (scanCount >= 3_000_000L + pool.Count)
                        {
                            res.Warnings.Add("召唤物随机化的敌人选择陷入长时间重试，已强制接受当前候选。" +
                                             "建议开启「允许骨灰重复」或降低多召设置。");
                            goto accept;
                        }

                        int num5 = (int)buddy.GetInt(rows[m], "npcParamId");
                        int num6 = pool[num3].NpcId;

                        if (!_s.BuddyReuse && num6 - num6 % 1000 == num5 - num5 % 1000)
                        {
                            if (rng.Next(1, 100) <= _s.VariantReuseChance) goto accept;   // 接受
                            RerollCandidate(ref num3, ref scanCount, pool.Count, rng);
                            accepted = false;
                            break;
                        }
                        if (isChild && _s.ForbidLargeMultiBuddies)
                        {
                            PARAM.Row src = pool[num3].NpcRow;
                            double size = npc.GetDbl(src, "hitHeight") + npc.GetDbl(src, "hitRadius");
                            if (size > MaxMultiBuddySize)
                            {
                                RerollCandidate(ref num3, ref scanCount, pool.Count, rng);
                                accepted = false;
                                break;
                            }
                        }
                    }
                }
                accept:

                // ---- 4b) 多召重复：第 2+ 个副本按概率复用第 1 个的敌人 ----
                int num10, num11;
                if (flag && rng.Next(1, 100) <= _s.MultiBuddyDupeChance)
                {
                    PARAM.Row baseRow = byId.TryGetValue(num9, out PARAM.Row br) ? br : null;
                    num10 = baseRow != null ? (int)buddy.GetInt(baseRow, "npcParamId") : pool[num3].NpcId;
                    num11 = baseRow != null ? (int)buddy.GetInt(baseRow, "npcThinkParamId") : pool[num3].ThinkId;
                }
                else
                {
                    num10 = pool[num3].NpcId;
                    num11 = pool[num3].ThinkId;
                    if (!_s.BuddyReuse) pool.RemoveAt(num3);
                }

                if (isMimic)
                {
                    // ---- 仿身泪滴特例（不克隆，直接指向原版友方数据）----
                    if (row3.ID == num9)
                    {
                        buddy.SetInt(row3, "npcParamId", 133201000);
                        buddy.SetInt(row3, "npcThinkParamId", 133200000);
                    }
                    else
                    {
                        buddy.SetInt(row3, "npcParamId", 100000010);
                        buddy.SetInt(row3, "npcThinkParamId", 100000010);
                        buddy.SetInt(row3, "npcPlayerInitParamId", 26050);
                        buddy.SetInt(row3, "generateAnimId", 60500);
                        buddy.SetInt(row3, "pcFollowType", 0);
                    }
                    Log($"  仿身泪滴 {row3.ID}：使用原版玩家复制体配置。");
                    continue;
                }

                // ---- 4c) 克隆 + 驯服 ----
                PARAM.Row srcNpc = npc.Find(num10);
                PARAM.Row srcThink = think.Find(num11);
                if (srcNpc == null || srcThink == null)
                {
                    res.Warnings.Add($"敌人 {num10} 或其 AI {num11} 不存在，骨灰 {row3.ID} 未改动。");
                    assignmentFailures++;
                    continue;
                }

                int num12 = num10;
                while (usedNpcIds.Contains(num12)) num12++;
                int num13 = num11;
                while (usedThinkIds.Contains(num13)) num13++;

                PARAM.Row cloneNpc = CloneRow(npc.Param, srcNpc, num12, CloneNamePrefix + " " + num12);
                usedNpcIds.Add(num12);
                res.ClonesCreated++;
                res.CloneNpcIds.Add(num12);

                PARAM.Row cloneThink = CloneRow(think.Param, srcThink, num13, CloneNamePrefix + " " + num13);
                usedThinkIds.Add(num13);

                // 驯服：友方阵营、无掉落、无魂、限制碰撞、补友方特效
                npc.SetInt(cloneNpc, "teamType", 47);
                npc.SetInt(cloneNpc, "itemLotId_enemy", -1);
                npc.SetInt(cloneNpc, "itemLotId_map", -1);
                npc.SetInt(cloneNpc, "GameClearSpEffectID", -1);
                npc.SetInt(cloneNpc, "getSoul", 0);
                if (npc.GetDbl(cloneNpc, "hitRadius") > 1.0) npc.SetDbl(cloneNpc, "hitRadius", 1.0);
                if (npc.GetDbl(cloneNpc, "hitHeight") > 5.0) npc.SetDbl(cloneNpc, "hitHeight", 5.0);

                int filled = 0;
                int[] friendFills = { 295000, 296000, 297000 };
                for (int n = 0; n <= 31; n++)
                {
                    string f = "spEffectID" + n;
                    if (!npc.Has(f)) continue;
                    long v = npc.GetInt(cloneNpc, f);
                    if (v >= 7000 && v < 7700) npc.SetInt(cloneNpc, f, -1);
                    if (v <= 0 && filled < friendFills.Length)
                    {
                        npc.SetInt(cloneNpc, f, friendFills[filled]);
                        filled++;
                    }
                }
                if (filled < friendFills.Length) shortSlots++;

                think.SetInt(cloneThink, "isBuddyAI", 1);

                // ---- 4d) 写回骨灰条目 ----
                buddy.SetInt(row3, "npcParamId", num12);
                buddy.SetInt(row3, "npcThinkParamId", num13);
                buddy.SetInt(row3, "npcPlayerInitParamId", -1);
                buddy.SetInt(row3, "npcParamId_ridden", -1);
                buddy.SetInt(row3, "npcThinkParamId_ridden", -1);
                buddy.SetInt(row3, "generateAnimId", -1);
                buddy.SetInt(row3, "appearOnAroundSekihi", 0);
                buddy.SetInt(row3, "pcFollowType", 0);

                double chr = npc.GetDbl(cloneNpc, "chrHitRadius");
                double xv = 0;
                if (flag && l > 0)
                {
                    double pv = buddy.GetDbl(rows[l - 1], "x_offset");
                    xv = pv >= 0 ? pv + chr : pv - chr;
                    xv *= -1;
                }
                buddy.SetDbl(row3, "x_offset", xv);
                buddy.SetDbl(row3, "z_offset", -chr);

                for (int n = 0; n <= 10; n++)
                    buddy.SetInt(row3, "dopingSpEffect_lv" + n, 290000 + n);
            }

            if (shortSlots > 0)
            {
                res.ShortEffectSlots = shortSlots;
                res.Warnings.Add($"{shortSlots} 个召唤克隆没有足够的空效果槽，未能补满 3 个友方特效（仍可正常使用）。");
            }
            if (assignmentFailures > 0)
                res.Warnings.Add($"{assignmentFailures} 个骨灰条目未能分配敌人（保持未改动状态）。");

            res.Reports.Add(new ParamReport
            {
                ParamName = "BuddyParam",
                ParamType = buddy.Param.ParamType,
                Description = "召唤物（完整随机化：换敌 + 多召）",
                TotalRows = rows.Count,
                AffectedRows = res.ClonesCreated + children,
                AffectedFields = 14,
            });
            res.Reports.Add(new ParamReport
            {
                ParamName = "NpcParam",
                ParamType = npc.Param.ParamType,
                Description = "召唤敌人克隆（驯服为友方）",
                TotalRows = npc.Param.Rows.Count,
                AffectedRows = res.ClonesCreated,
                AffectedFields = 11,
            });
            res.Reports.Add(new ParamReport
            {
                ParamName = "NpcThinkParam",
                ParamType = think.Param.ParamType,
                Description = "召唤敌 AI 克隆（isBuddyAI）",
                TotalRows = think.Param.Rows.Count,
                AffectedRows = res.ClonesCreated,
                AffectedFields = 1,
            });

            // ---- 5) 强化倍率：SpEffect 290000-290010（血量/伤害，加法）----
            if (sp != null && _s.ApplyMultipliers &&
                (Math.Abs(_s.HpMultiplier - 1.0) > 0.0001 || Math.Abs(_s.DamageMultiplier - 1.0) > 0.0001))
            {
                int touched = SummonTuner.ApplyRateDeltas(sp.Param, _s.HpMultiplier, _s.DamageMultiplier, _log);
                if (touched > 0)
                {
                    res.Reports.Add(new ParamReport
                    {
                        ParamName = "SpEffectParam",
                        ParamType = sp.Param.ParamType,
                        Description = $"召唤物强化倍率（血量 ×{_s.HpMultiplier:0.##}，伤害 ×{_s.DamageMultiplier:0.##}）",
                        TotalRows = sp.Param.Rows.Count,
                        AffectedRows = touched,
                        AffectedFields = 6,
                    });
                    sp.File.Bytes = sp.Param.Write();
                }
            }

            // ---- 6) FP 倍率：只改克隆出来的召唤 NPC 的 mp ----
            if (_s.ApplyMultipliers && Math.Abs(_s.FpMultiplier - 1.0) > 0.0001 && res.CloneNpcIds.Count > 0)
            {
                int touched = SummonTuner.ApplyMpMultiplier(npc.Param, res.CloneNpcIds, _s.FpMultiplier);
                if (touched > 0)
                    res.Reports.Add(new ParamReport
                    {
                        ParamName = "NpcParam",
                        ParamType = npc.Param.ParamType,
                        Description = $"召唤物 FP 倍率（×{_s.FpMultiplier:0.##}）",
                        TotalRows = npc.Param.Rows.Count,
                        AffectedRows = touched,
                        AffectedFields = 1,
                    });
            }

            // ---- 7) 骨灰召唤消耗重掷 ----
            if (goods != null && _s.RandomizeAshCosts)
            {
                int touched = RandomizeAshCosts(goods, rng);
                res.CostRowsTouched = touched;
                if (touched > 0)
                {
                    res.Reports.Add(new ParamReport
                    {
                        ParamName = "EquipParamGoods",
                        ParamType = goods.Param.ParamType,
                        Description = $"骨灰召唤消耗重掷（FP {_s.FpCostMin}-{_s.FpCostMax}，" +
                                      $"{_s.HpCostChance}% 改 HP {_s.HpCostMin}-{_s.HpCostMax}）",
                        TotalRows = goods.Param.Rows.Count,
                        AffectedRows = touched,
                        AffectedFields = 2,
                    });
                    Log($"召唤物随机化：已重掷 {touched} 个骨灰道具的召唤消耗。");
                }
            }

            // ---- 8) 排序 + 写回 ----
            SortAndWrite(buddy);
            SortAndWrite(npc);
            SortAndWrite(think);
            if (goods != null && res.CostRowsTouched > 0) goods.File.Bytes = goods.Param.Write();

            Log($"召唤物随机化完成：克隆敌人 NPC {res.ClonesCreated} 个，多召副本 {children} 个，敌人池 {res.PoolSize} 个。");
            return res;
        }

        /// <summary>重选候选：前 100 万次随机跳，之后顺序推进（与原版一致，保证一定能结束）。</summary>
        private static void RerollCandidate(ref int num3, ref long scanCount, int poolCount, Random rng)
        {
            if (scanCount < 1_000_000) num3 = rng.Next(0, poolCount);
            else
            {
                num3++;
                if (num3 >= poolCount) num3 = 0;
            }
        }

        /// <summary>
        /// 数字截断法找配套 AI：依次尝试精确 ID、去尾 1~6 位（取最大者，等价于原版从后往前扫描）。
        /// </summary>
        private static int ResolveThinkId(int npcId, HashSet<int> thinkIds)
        {
            int pow = 1;
            for (int t = 0; t <= 6; t++)
            {
                int cand = npcId - npcId % pow;
                if (thinkIds.Contains(cand)) return cand;
                pow *= 10;
            }
            return 0;
        }

        /// <summary>删除名字以 CloneNamePrefix 开头的行（上一轮生成的克隆）。</summary>
        private static int RemoveMarkedRows(ParamRef pr)
        {
            int removed = 0;
            for (int i = pr.Param.Rows.Count - 1; i >= 0; i--)
            {
                string name = pr.Param.Rows[i].Name;
                if (name != null && name.StartsWith(CloneNamePrefix, StringComparison.Ordinal))
                {
                    pr.Param.Rows.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        private static void SortAndWrite(ParamRef pr)
        {
            pr.Param.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
            pr.File.Bytes = pr.Param.Write();
        }

        /// <summary>
        /// 骨灰召唤消耗重掷：把所有 goodsUseAnim=34 的行按设置重掷 FP/HP 消耗；
        /// 非整百的子行沿用同一组内上一行的消耗（与原版一致）。
        /// </summary>
        private int RandomizeAshCosts(ParamRef goods, Random rng)
        {
            if (!goods.Has("goodsUseAnim") || !goods.Has("consumeMP") || !goods.Has("consumeHP"))
                return 0;

            List<PARAM.Row> ashRows = goods.Param.Rows
                .Where(r => r.ID > 0 && goods.GetInt(r, "goodsUseAnim") == 34)
                .OrderBy(r => r.ID)
                .ToList();
            if (ashRows.Count == 0) return 0;

            var byId = new Dictionary<int, PARAM.Row>();
            foreach (PARAM.Row r in ashRows) byId[r.ID] = r;

            foreach (PARAM.Row r in ashRows)
            {
                goods.SetInt(r, "consumeHP", 0);
                goods.SetInt(r, "consumeMP", 0);
            }

            foreach (PARAM.Row r in ashRows)
            {
                if (r.ID % 100 != 0) continue;
                if (rng.Next(1, 100) <= _s.HpCostChance && _s.HpCostMax > 0)
                    goods.SetInt(r, "consumeHP", rng.Next(_s.HpCostMin, Math.Max(_s.HpCostMin + 1, _s.HpCostMax)));
                else
                    goods.SetInt(r, "consumeMP", rng.Next(_s.FpCostMin, Math.Max(_s.FpCostMin + 1, _s.FpCostMax)));
            }

            foreach (PARAM.Row r in ashRows)
            {
                if (r.ID % 100 == 0) continue;
                if (byId.TryGetValue(r.ID - 1, out PARAM.Row prev))
                {
                    goods.SetInt(r, "consumeHP", goods.GetInt(prev, "consumeHP"));
                    goods.SetInt(r, "consumeMP", goods.GetInt(prev, "consumeMP"));
                }
            }

            return ashRows.Count;
        }
    }

    /// <summary>
    /// 召唤物数值调整（完整/温和两种模式共用）。
    /// 血量/伤害倍率以「加法」形式落在 SpEffect 290000-290010 上——这是游戏里
    /// 骨灰随强化等级成长所用的效果系列，与原版 Summon Randomizer 的做法一致，
    /// 因此倍率会和强化加成叠加，而不是互相覆盖。
    /// </summary>
    internal static class SummonTuner
    {
        public static int ApplyRateDeltas(PARAM sp, double hpMult, double dmgMult, Action<string> log)
        {
            var fields = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < sp.AppliedParamdef.Fields.Count; i++)
                fields[sp.AppliedParamdef.Fields[i].InternalName] = i;

            string[] rateFields =
            {
                "physicsAttackPowerRate", "magicAttackPowerRate",
                "fireAttackPowerRate", "thunderAttackPowerRate", "darkAttackPowerRate",
            };

            double hpDelta = hpMult - 1.0;
            double dmgDelta = dmgMult - 1.0;
            int touched = 0;

            for (int n = 0; n <= 10; n++)
            {
                PARAM.Row row = sp.Rows.Find(r => r.ID == 290000 + n);
                if (row == null) continue;

                if (fields.TryGetValue("maxHpRate", out int hpIdx) && Math.Abs(hpDelta) > 0.0001)
                {
                    double cur = SafeToDouble(row.Cells[hpIdx].Value);
                    row.Cells[hpIdx].Value = (float)(cur + hpDelta);
                }
                if (Math.Abs(dmgDelta) > 0.0001)
                {
                    foreach (string f in rateFields)
                    {
                        if (!fields.TryGetValue(f, out int idx)) continue;
                        double cur = SafeToDouble(row.Cells[idx].Value);
                        row.Cells[idx].Value = (float)(cur + dmgDelta);
                    }
                }
                touched++;
            }

            log?.Invoke($"召唤物倍率：已调整 SpEffect 290000-2900{touched - 1:00} 共 {touched} 个" +
                        $"（血量 +{hpDelta * 100:0.##}%，攻击 +{dmgDelta * 100:0.##}%）。");
            return touched;
        }

        public static int ApplyMpMultiplier(PARAM npc, IEnumerable<int> npcIds, double fpMult)
        {
            int idxMp = -1;
            for (int i = 0; i < npc.AppliedParamdef.Fields.Count; i++)
                if (npc.AppliedParamdef.Fields[i].InternalName == "mp") { idxMp = i; break; }
            if (idxMp < 0) return 0;

            var ids = npcIds as HashSet<int> ?? new HashSet<int>(npcIds);
            int touched = 0;
            foreach (PARAM.Row row in npc.Rows)
            {
                if (!ids.Contains(row.ID)) continue;
                double cur = SafeToDouble(row.Cells[idxMp].Value);
                if (cur <= 0) continue;
                double next = Math.Max(1, Math.Min(4000000000.0, Math.Round(cur * fpMult)));
                try
                {
                    row.Cells[idxMp].Value = Convert.ChangeType(next, row.Cells[idxMp].Value.GetType(), CultureInfo.InvariantCulture);
                    touched++;
                }
                catch { }
            }
            return touched;
        }

        /// <summary>温和模式：把 FP 倍率施加到 BuddyParam 引用到的 NPC 上。</summary>
        public static int ApplyMpToReferenced(PARAM buddy, PARAM npc, double fpMult)
        {
            int idxBuddy = -1;
            for (int i = 0; i < buddy.AppliedParamdef.Fields.Count; i++)
                if (buddy.AppliedParamdef.Fields[i].InternalName == "npcParamId") { idxBuddy = i; break; }
            if (idxBuddy < 0) return 0;

            var ids = new HashSet<int>();
            foreach (PARAM.Row row in buddy.Rows)
            {
                if (row.ID <= 0) continue;
                long v = SafeToLong(row.Cells[idxBuddy].Value);
                if (v > 0 && v < 900000000) ids.Add((int)v);
            }
            return ApplyMpMultiplier(npc, ids, fpMult);
        }

        private static long SafeToLong(object v)
        {
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static double SafeToDouble(object v)
        {
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }
}
