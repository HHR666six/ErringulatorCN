using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SoulsFormats;

namespace ErringulatorCN.Core
{
    /// <summary>
    /// 效果随机化核心：以撒"错误道具"式的效果错位映射。
    ///
    /// 做法是把同一个表里各条目的"效果字段组"整体互相交换——
    /// 效果组内部的关联字段（例如 behaviorId 与 refId1/refId2）必须同步搬家，
    /// 否则行为会指向不存在的条目而失效。这样"炎爆"可能放出"彗星阿兹尔"，
    /// "苔药"可能触发"龟壳护符"的效果，而名称、图标、价格等展示信息保持原样。
    /// </summary>
    public sealed class EffectRandomizer
    {
        private readonly RandomizerSettings _settings;
        private readonly Action<string> _log;
        private readonly Action<RandomizeProgress> _progress;

        private List<PARAMDEF> _paramdefs;

        public EffectRandomizer(RandomizerSettings settings, Action<string> log = null,
            Action<RandomizeProgress> progress = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log;
            _progress = progress;
        }

        private void Log(string message) => _log?.Invoke(message);

        private void Report(string message, int completed, int total) =>
            _progress?.Invoke(new RandomizeProgress { Message = message, Completed = completed, Total = total });

        // ==================== 目标表定义 ====================

        private sealed class TargetSpec
        {
            /// <summary>参数表名（BND4 内的文件名，不含扩展名）。</summary>
            public string ParamName;

            /// <summary>中文说明，用于日志与界面。</summary>
            public string Description;

            /// <summary>行的筛选条件。</summary>
            public Func<PARAM.Row, bool> RowFilter = _ => true;

            /// <summary>各自独立错位的字段（每个字段单独置换）。</summary>
            public string[] LooseFields = Array.Empty<string>();

            /// <summary>整体错位的效果组：组内字段同步搬家，保证引用关系不被打断。</summary>
            public string[][] EffectGroups = Array.Empty<string[]>();
        }

        // ---- 魔法 / 祷告：行为与特效链 ----
        private static readonly string[] MagicBehaviorGroup =
        {
            "behaviorId", "ezStateBehaviorType",
            "spEffectCategory", "refType", "hasSpEffectType",
            "refCategory1", "refId1", "refCategory2", "refId2",
            "refCategory3", "refId3", "refCategory4", "refId4",
            "refCategory5", "refId5", "refCategory6", "refId6",
            "refCategory7", "refId7", "refCategory8", "refId8",
            "refCategory9", "refId9", "refCategory10", "refId10",
        };

        private static readonly string[] MagicVariantGroup =
        {
            "replaceMagicId", "replaceCategory",
            "ReplacementMagic1", "ReplacementMagic2", "ReplacementMagic3", "ReplacementMagic4",
            "ReplacementStatusType", "ReplacementStatus1", "ReplacementStatus2",
            "ReplacementStatus3", "ReplacementStatus4",
            "consumeType1", "consumeType2", "consumeType3", "consumeType4", "consumeType5",
            "consumeType6", "consumeType7", "consumeType8", "consumeType9", "consumeType10",
            "createLimitGroupId",
        };

        private static readonly string[] MagicCostFields =
        {
            "mp", "stamina", "mp_charge", "stamina_charge", "consumeSA",
            "requirementIntellect", "requirementFaith", "requirementLuck",
            "slotLength",
        };

        private static readonly string[] MagicSfxFields =
        {
            "castSfxId", "fireSfxId", "effectSfxId",
        };

        // ---- 战灰 ----
        private static readonly string[] AshBehaviorGroup =
        {
            "swordArtsType", "swordArtsTypeNew", "artsSpeedType", "refStatus",
        };

        private static readonly string[] AshCostFields =
        {
            "usePoint_L1", "usePoint_L2", "usePoint_R1", "usePoint_R2",
            "useMagicPoint_L1", "useMagicPoint_L2", "useMagicPoint_R1", "useMagicPoint_R2",
        };

        // ---- 道具 ----
        private static readonly string[] GoodsEffectGroup =
        {
            "refId_default", "behaviorId",
            "refCategory", "spEffectCategory", "useEnableSpEffectType",
            "goodsUseAnim", "refId_1", "refVirtualWepId",
        };

        private static readonly string[] GoodsCostFields =
        {
            "consumeMP", "consumeHP", "consumeHeroPoint", "useLimitSpEffectType",
        };

        private static readonly string[] GoodsSfxFields =
        {
            "sfxVariationId", "castSfxId", "fireSfxId", "effectSfxId",
        };

        // ---- 护符 ----
        private static readonly string[] AccessoryEffectGroup =
        {
            "refId", "refCategory", "spEffectCategory", "behaviorId",
            "residentSpEffectId1", "residentSpEffectId2", "residentSpEffectId3", "residentSpEffectId4",
        };

        // ---- 武器 ----
        private static readonly string[] WeaponEffectGroup =
        {
            "residentSpEffectId", "residentSpEffectId1", "residentSpEffectId2",
            "spEffectBehaviorId0", "spEffectBehaviorId1", "spEffectBehaviorId2",
            "behaviorVariationId", "absorpParamId", "attackElementCorrectId",
            "spAttribute", "spAtkcategory", "spAttributeVariationValue",
        };

        // ---- 武器动作模组：动画套组整体搬家（换了动作，武器本身数值不动）----
        // wepmotionCategory 决定该武器用哪套攻击动画；
        // wepmotionOneHandId / wepmotionBothHandId 是同一套动作家族里的单手/双手变体。
        // 三者必须一起搬家，否则"用着 A 的挥砍、判定却是 B 的连段"会彻底失灵。
        private static readonly string[] WeaponMotionGroup =
        {
            "wepmotionCategory", "wepmotionOneHandId", "wepmotionBothHandId",
        };

        // ---- NPC 对话：整条对白内容整体搬家（说别人的话，动作/口型配套）----
        //
        // 【初始版实现（按用户要求回退到此行为）】
        // 台词(男/女)、语音(男/女)、动作、口型同步、触发事件等成套搬走，
        // 错位之后的对话依然"完整可用"——只是变成了别人的台词。
        //
        // 已知特性：因为说话动作/口型/时间等"节奏字段"也一起搬家，
        // 部分 NPC（尤其商人）的对话会以极快速度掠过、难以看清——
        // 这是本实现方式的固有现象（同类工具的已知问题）。1.2.3/1.2.4
        // 曾把搬运范围收窄以规避此现象，此处按用户要求回退为初始行为。
        private static readonly string[] TalkContentGroup =
        {
            "msgId", "voiceId",
            "msgId_female", "voiceId_female",
            "spEffectId0", "motionId0",
            "spEffectId1", "motionId1",
            "returnPos", "reactionId", "eventId",
            "lipSyncStart", "lipSyncTime", "timeout", "talkAnimationId",
        };

        // ---- 道具排除名单 ----

        /// <summary>
        /// 沿用 Erringulator 的 BlacklistGoods：圣杯瓶系列、灵药圣杯瓶、望远镜、提灯、
        /// 灵马哨笛、记忆的恩惠、多人联机道具、姿势动作、赠礼道具等。
        /// 这些道具的效果一旦被错位，游戏功能会直接损坏（无法召唤灵马、无法回血等）。
        /// </summary>
        private static readonly HashSet<int> GoodsBlacklist = new HashSet<int>
        {
            0, 10, 11, 12, 13, 14, 15, 16, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 35, 40, 41, 42, 43, 44,
            60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79,
            90, 91, 92, 93, 94, 98, 99,
            100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114,
            115, 130, 135, 150, 166,
            170, 171, 172, 174, 175, 178, 179, 180, 181, 182, 183, 184,
            250, 251,
            // 红/蓝圣杯瓶全等级（上游黑名单覆盖 1000-1075 整段）
            1000, 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009,
            1010, 1011, 1012, 1013, 1014, 1015, 1016, 1017, 1018, 1019,
            1020, 1021, 1022, 1023, 1024, 1025, 1026, 1027, 1028, 1029,
            1030, 1031, 1032, 1033, 1034, 1035, 1036, 1037, 1038, 1039,
            1040, 1041, 1042, 1043, 1044, 1045, 1046, 1047, 1048, 1049,
            1050, 1051, 1052, 1053, 1054, 1055, 1056, 1057, 1058, 1059,
            1060, 1061, 1062, 1063, 1064, 1065, 1066, 1067, 1068, 1069,
            1070, 1071, 1072, 1073, 1074, 1075,
            2040,  // 望远镜
            2070,  // 携带照明灯
            9000, 9001, 9002, 9003, 9004, 9005, 9006, 9007, 9008, 9009,
            9010, 9011, 9012, 9013, 9014, 9015, 9016, 9017, 9018, 9019,
            9020, 9021, 9022, 9023, 9024, 9026, 9027, 9028, 9029,
            9030, 9031, 9032, 9033, 9034, 9035, 9036, 9037, 9039,
            9040, 9041, 9042, 9043, 9045, 9046, 9047, 9048, 9049, 9050,
            50200, 50201, 50202, 50203,
            51760, 53090, 53230, 53400, 53630, 53650, 53651, 53652, 53653,
            53654, 53655, 53656, 53657, 53658,
            999999999,
        };

        /// <summary>
        /// 允许参与错位的 goodsType。排除掉明确"不可直接使用/非效果物品"的类型：
        /// 1 = 合成素材，10 = 地图，12 = 制作笔记，14 = 灵药露滴，16/17/18 = 收集品与任务物品。
        /// </summary>
        private static readonly HashSet<int> GoodsUsableTypes = new HashSet<int>
        {
            0, 2, 4, 5, 9, 11, 15, 19, 27, 28,
        };

        // ==================== 对话数据健康检查 ====================

        /// <summary>对话的"节奏字段"（说话动作 / 口型 / 时间 / 事件等）。</summary>
        private static readonly string[] TalkRhythmFields =
        {
            "talkAnimationId", "lipSyncStart", "lipSyncTime", "timeout",
            "motionId0", "motionId1",
            "reactionId", "eventId", "returnPos",
            "spEffectId0", "spEffectId1",
        };

        /// <summary>1.16 官方原版 TalkParam 节奏整表指纹（全表 11 个节奏字段的哈希）。</summary>
        private const string VanillaTalkRhythmFingerprint = "098347F2F6AB513B";

        /// <summary>计算 TalkParam 节奏整表指纹（与检测逻辑、诊断工具完全一致）。</summary>
        private static string ComputeTalkRhythmFingerprint(PARAM talk)
        {
            var sb = new StringBuilder();
            foreach (PARAM.Row r in talk.Rows.Where(x => x.ID > 0).OrderBy(x => x.ID))
            {
                sb.Append(r.ID).Append(':');
                foreach (string f in TalkRhythmFields)
                    sb.Append(GoodsInt(r, f)).Append(':');
                sb.Append('|');
            }
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash).Substring(0, 16);
        }

        /// <summary>
        /// 对话数据体检：输入文件的节奏数据若与 1.16 原版不一致，说明它曾被
        /// 旧版本（1.2.2/1.2.3）搬乱过——那种损坏不会随重新生成自动复原，
        /// 部分 NPC（如商人）对话会仍然飞速掠过。此处主动提醒用户先恢复备份。
        /// </summary>
        private void CheckTalkRhythmHealth(Dictionary<string, BinderFile> byName, RandomizeReport report)
        {
            if (!byName.TryGetValue("TalkParam", out BinderFile file)) return;
            PARAM talk = PARAM.Read(file.Bytes);
            if (!talk.ApplyParamdefCarefully(_paramdefs)) return;

            string fp = ComputeTalkRhythmFingerprint(talk);
            if (fp == VanillaTalkRhythmFingerprint)
            {
                Log("对话数据体检：说话动作/节奏数据与 1.16 原版一致。");
                return;
            }

            string msg =
                $"【重要】检测到输入文件的 NPC 对话「节奏数据」（说话动作/口型等）与 1.16 原版不一致" +
                $"（当前指纹 {fp}，原版 {VanillaTalkRhythmFingerprint}）。" +
                "这通常意味着此文件曾被旧版本（1.2.2/1.2.3）搬乱过——这类损坏不会因重新生成而复原，" +
                "部分 NPC（如商人咖列）的对话仍会飞速掠过。" +
                "请先点「恢复备份」（或改用从未做过对话随机的原版 regulation.bin）作为输入，再重新生成。";
            Log("警告：" + msg);
            report.Warnings.Add(msg);
        }

        /// <summary>安全读取道具行的整数字段；缺失字段或读取失败返回 -1。</summary>
        private static long GoodsInt(PARAM.Row row, string field)
        {
            try { return Convert.ToInt64(row[field].Value); }
            catch { return -1; }
        }

        /// <summary>
        /// 保守模式判定：只有"真的能直接使用"且"带明确使用效果"的道具才参与错位。
        /// 排除素材、笔记、地图、露滴、钥匙/内容物、未实装条目，以及黑名单道具。
        /// </summary>
        private static bool IsGoodsEligible(PARAM.Row row)
        {
            if (row.ID <= 0) return false;
            if (GoodsBlacklist.Contains(row.ID)) return false;

            if (!GoodsUsableTypes.Contains((int)GoodsInt(row, "goodsType"))) return false;

            // goodsUseAnim == 0 表示道具不可直接使用（素材、制作笔记、地图、收集品等）
            long anim = GoodsInt(row, "goodsUseAnim");
            if (anim <= 0) return false;

            // 254/255 是"未实装/不可用"的占位动作
            if (anim >= 254) return false;

            // 必须带明确的使用效果，否则交换没有意义
            long refDefault = GoodsInt(row, "refId_default");
            long behavior = GoodsInt(row, "behaviorId");
            long spEffect = GoodsInt(row, "spEffectCategory");
            long enableSpEffect = GoodsInt(row, "useEnableSpEffectType");
            bool hasEffect = refDefault > 0 || behavior > 0 || spEffect > 0 || enableSpEffect > 0;
            return hasEffect;
        }

        /// <summary>非保守模式：沿用 Erringulator 的 goodsType == 0 过滤（仍排除黑名单）。</summary>
        private static bool IsGoodsAnyType0(PARAM.Row row)
        {
            if (row.ID <= 0) return false;
            if (GoodsBlacklist.Contains(row.ID)) return false;
            return GoodsInt(row, "goodsType") == 0;
        }

        private List<TargetSpec> BuildTargets()
        {
            var list = new List<TargetSpec>();
            if (_settings.RandomizeMagic)
            {
                var groups = new List<string[]> { MagicBehaviorGroup };
                var loose = new List<string>(MagicSfxFields);
                if (_settings.RandomizeCost) loose.AddRange(MagicCostFields);

                list.Add(new TargetSpec
                {
                    ParamName = "Magic",
                    Description = "魔法与祷告",
                    RowFilter = r => _settings.IncludeNpcSpells || r.ID < 53000,
                    LooseFields = loose.ToArray(),
                    EffectGroups = groups.ToArray(),
                });
                // 变体效果组：替换魔法/替换状态（与主行为组分开，避免引用断裂）
                list.Add(new TargetSpec
                {
                    ParamName = "Magic",
                    Description = "魔法变体效果",
                    RowFilter = r => _settings.IncludeNpcSpells || r.ID < 53000,
                    EffectGroups = new[] { MagicVariantGroup },
                });
            }

            if (_settings.RandomizeAshOfWar)
            {
                var loose = new List<string>();
                if (_settings.RandomizeCost) loose.AddRange(AshCostFields);
                list.Add(new TargetSpec
                {
                    ParamName = "SwordArtsParam",
                    Description = "战灰",
                    EffectGroups = new[] { AshBehaviorGroup },
                    LooseFields = loose.ToArray(),
                });
            }

            if (_settings.RandomizeGoods)
            {
                var loose = new List<string>(GoodsSfxFields);
                if (_settings.RandomizeCost) loose.AddRange(GoodsCostFields);
                bool conservative = _settings.GoodsConservativeMode;
                list.Add(new TargetSpec
                {
                    ParamName = "EquipParamGoods",
                    Description = conservative ? "道具（仅可直接使用且带效果）" : "道具（全部 goodsType=0）",
                    RowFilter = conservative ? (Func<PARAM.Row, bool>)IsGoodsEligible : IsGoodsAnyType0,
                    EffectGroups = new[] { GoodsEffectGroup },
                    LooseFields = loose.ToArray(),
                });
            }

            if (_settings.RandomizeEquipment)
            {
                list.Add(new TargetSpec
                {
                    ParamName = "EquipParamAccessory",
                    Description = "护符效果",
                    EffectGroups = new[] { AccessoryEffectGroup },
                });
                list.Add(new TargetSpec
                {
                    ParamName = "EquipParamWeapon",
                    Description = "武器效果",
                    EffectGroups = new[] { WeaponEffectGroup },
                });
            }

            // ---- 附加：武器动作模组（换动画套组；放在最后，避免影响上面各表的随机序列）----
            if (_settings.RandomizeWeaponMoveset)
            {
                list.Add(new TargetSpec
                {
                    ParamName = "EquipParamWeapon",
                    Description = "武器动作模组（用别的武器动画）",
                    RowFilter = r => GoodsInt(r, "wepmotionCategory") > 0,
                    EffectGroups = new[] { WeaponMotionGroup },
                });
            }

            // ---- 附加：NPC 对话（说别人的台词）----
            // 【初始版实现（按用户要求回退）】所有有台词的行（msgId > 0）都参与，
            // 不做事后筛选。注意：部分 NPC 对话会飞速掠过（见 TalkContentGroup 注释）。
            if (_settings.RandomizeTalks)
            {
                list.Add(new TargetSpec
                {
                    ParamName = "TalkParam",
                    Description = "NPC 对话（说别人的台词）",
                    RowFilter = r => GoodsInt(r, "msgId") > 0,
                    EffectGroups = new[] { TalkContentGroup },
                });
            }

            return list;
        }

        // ==================== 主流程 ====================

        public RandomizeReport Run()
        {
            var sw = Stopwatch.StartNew();
            var report = new RandomizeReport { StartedAt = DateTime.Now };

            _settings.Validate();

            string seed = string.IsNullOrWhiteSpace(_settings.Seed) ? Permute.NewSeed() : _settings.Seed.Trim();
            report.Seed = seed;
            report.Mode = _settings.Mode;
            var rng = new Random(Permute.SeedToInt(seed));
            Log($"种子：{seed}");

            string defDir = EffectRandomizer.ResolveParamdefDir(_settings.ParamdefDir);
            Report("正在载入 paramdef 定义……", 1, 100);
            Log($"paramdef 目录：{defDir}");
            _paramdefs = LoadParamdefs(defDir);
            Log($"已载入 paramdef：{_paramdefs.Count} 个");
            if (_paramdefs.Count == 0)
                throw new InvalidOperationException($"paramdef 目录里没有可用的 *.xml：{defDir}");

            // ---- 输入 / 输出 ----
            string outputDir = string.IsNullOrWhiteSpace(_settings.OutputDir) ? _settings.InputDir : _settings.OutputDir;
            Directory.CreateDirectory(outputDir);
            string outputPath = Path.Combine(outputDir, "regulation.bin");

            // ---- 备份（必须在读取之前完成）----
            // 统一约定：备份就是 regulation.bin.bak，永远指向"未被本工具改过的原始文件"。
            string bakPath = Path.Combine(outputDir, "regulation.bin.bak");
            if (_settings.BackupExisting && File.Exists(outputPath))
            {
                if (!File.Exists(bakPath))
                {
                    File.Copy(outputPath, bakPath);
                    Log($"已备份原始文件：{bakPath}");
                }
                else
                {
                    Log($"已存在备份，保留不动：{bakPath}");
                }
                report.BackupPath = bakPath;
            }
            else if (File.Exists(bakPath))
            {
                report.BackupPath = bakPath;
            }

            // ---- 确定随机化来源 ----
            // 默认：永远使用「输入目录当前的 regulation.bin」作为来源。
            // 这样换了种子就一定换结果，也不会莫名其妙地反复套用同一份旧数据。
            // 只有用户显式开启 LoadBackup 时，才改为从备份重算。
            string inputPath = Path.Combine(_settings.InputDir, "regulation.bin");
            report.Source = "输入目录当前文件";

            if (_settings.LoadBackup && File.Exists(bakPath))
            {
                inputPath = bakPath;
                report.Source = "备份文件（用户选择从备份重算）";
                Log($"按设置从备份重算：{inputPath}");
            }
            else
            {
                Log($"随机化来源：{inputPath}" +
                    (File.Exists(bakPath) ? $"（同目录存在备份 {Path.GetFileName(bakPath)}，本次未使用）" : ""));
            }

            // 提示：来源若看起来已被本工具改过，提醒用户先还原
            if (File.Exists(bakPath) && !_settings.LoadBackup)
            {
                try
                {
                    long cur = new FileInfo(inputPath).Length;
                    long bak = new FileInfo(bakPath).Length;
                    if (cur != bak)
                        Log("提示：来源文件与备份大小不同，说明它可能已经被随机化过。" +
                            "若想从干净的原版重新开始，请先点「恢复备份」。");
                }
                catch { /* 忽略 */ }
            }

            report.InputPath = inputPath;
            report.OutputPath = outputPath;

            // ---- 解密读取 ----
            Report("正在解密 regulation.bin……", 5, 100);
            Log("正在解密并读取 regulation.bin……");
            BND4 regulation = SFUtil.DecryptERRegulation(inputPath);
            Log($"BND4 载入完成：{regulation.Files.Count} 个参数文件，压缩方式 {regulation.Compression}");

            var byName = new Dictionary<string, BinderFile>(StringComparer.OrdinalIgnoreCase);
            foreach (BinderFile file in regulation.Files)
                byName[Path.GetFileNameWithoutExtension(file.Name)] = file;

            // ---- 对话数据体检（仅在启用对话随机时）----
            // 输入若被旧版本搬乱过，重新生成无法复原，这里提前提醒用户先恢复备份。
            if (_settings.RandomizeTalks && !_settings.NoOp)
                CheckTalkRhythmHealth(byName, report);

            // ---- 逐表错位 ----
            List<TargetSpec> targets = _settings.NoOp ? new List<TargetSpec>() : BuildTargets();
            int totalSteps = targets.Count + 6;
            int step = 6;

            foreach (TargetSpec spec in targets)
            {
                Report($"正在随机化：{spec.Description}", step, totalSteps);
                step++;

                if (!byName.TryGetValue(spec.ParamName, out BinderFile file))
                {
                    report.Warnings.Add($"参数表不存在，已跳过：{spec.ParamName}");
                    Log($"跳过 {spec.ParamName}（参数表不存在）");
                    continue;
                }

                ParamReport pr = ProcessParam(regulation, file, spec, rng, report);
                if (pr != null) report.Params.Add(pr);
            }

            // ---- 附加：随机化召唤物（骨灰/灵体召唤出来的敌人）----
            // 两套引擎：
            //   完整（默认）＝ Elden Ring Summon Randomizer v1.0.8 的完整移植，
            //                 引擎内部自行处理换敌、多召、克隆驯服、倍率与骨灰消耗；
            //   温和       ＝ 1.0.x 的成对错位（不新增行），倍率在这里补上。
            // NoOp（只重打包）模式下所有附加处理一律跳过。
            if (_settings.RandomizeSummons && !_settings.NoOp)
            {
                SummonSettings ss = _settings.Summons ?? new SummonSettings();
                ss.Clamp();

                if (ss.Engine == SummonEngineMode.Full)
                {
                    Report("正在随机化召唤物（完整版：换敌 + 多召 + 驯服）……", step, totalSteps);
                    step++;
                    var engine = new SummonRandomizerEngine(ss, _paramdefs, Log);
                    SummonRunResult sr = engine.Run(regulation, seed);
                    report.Params.AddRange(sr.Reports);
                    report.Warnings.AddRange(sr.Warnings);
                    if (sr.Skipped) Log("召唤物随机化已跳过（详见警告列表）。");
                }
                else if (byName.TryGetValue("BuddyParam", out BinderFile buddyFile))
                {
                    Report("正在随机化召唤物（温和：成对错位）……", step, totalSteps);
                    step++;
                    ParamReport pr = ProcessBuddySummon(buddyFile, rng, report, ss);
                    if (pr != null) report.Params.Add(pr);

                    // 温和模式的倍率（完整模式的倍率由引擎内部处理）
                    if (ss.ApplyMultipliers)
                    {
                        ApplyMildModeSummonTuning(ss, byName, buddyFile, report);
                    }
                }
            }

            // ---- 附加：解除战灰的武器类型限制（让任何武器都能装任意战灰）----
            if (_settings.UnlockAshOfWarForAllWeapons && !_settings.NoOp &&
                byName.TryGetValue("EquipParamGem", out BinderFile gemFile))
            {
                ParamReport pr = ProcessGemUnlock(gemFile, report);
                if (pr != null) report.Params.Add(pr);
            }

            // ---- 附加：武器挂载的战灰错位 ----
            if (_settings.RandomizeWeaponAshLink && !_settings.NoOp &&
                byName.TryGetValue("EquipParamWeapon", out BinderFile weaponFile))
            {
                var spec = new TargetSpec
                {
                    ParamName = "EquipParamWeapon",
                    Description = "武器战灰挂载",
                    LooseFields = new[] { "swordArtsParamId" },
                };
                ParamReport pr = ProcessParam(regulation, weaponFile, spec, rng, report);
                if (pr != null) report.Params.Add(pr);
            }

            // ---- 写回并加密 ----
            Report("正在重新打包并加密……", totalSteps - 2, totalSteps);
            Log("正在重新打包并加密……");
            regulation.Compression = DCX.Type.DCX_ZSTD;
            SFUtil.EncryptERRegulation(outputPath, regulation);
            Log($"已写出：{outputPath}");

            // ---- 自检：重新读回并核对结构 ----
            Report("正在自检输出文件……", totalSteps - 1, totalSteps);
            try
            {
                BND4 verify = SFUtil.DecryptERRegulation(outputPath);
                Log($"自检：重新读取成功，参数文件 {verify.Files.Count} 个");
                if (verify.Files.Count != regulation.Files.Count)
                {
                    report.Warnings.Add("自检发现参数文件数量不一致，请谨慎使用。");
                }
                else
                {
                    for (int i = 0; i < verify.Files.Count; i++)
                    {
                        if (verify.Files[i].Bytes.Length != regulation.Files[i].Bytes.Length)
                        {
                            report.Warnings.Add($"自检发现 {Path.GetFileName(verify.Files[i].Name)} 长度异常。");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.Warnings.Add("自检失败（输出文件可能不可用）：" + ex.Message);
                Log("自检失败：" + ex.Message);
            }

            // ---- 关键道具安全自检：确认灵马哨笛/望远镜/圣杯瓶等未被误改 ----
            if (_settings.RandomizeGoods)
            {
                try
                {
                    SafetyCheckReport safety = SafetyAuditor.Check(inputPath, outputPath, defDir, _paramdefs);
                    report.Safety = safety;
                    if (safety.Errors.Count > 0)
                    {
                        Log("关键道具自检未能完成：" + string.Join("；", safety.Errors));
                    }
                    else if (safety.Passed)
                    {
                        Log($"关键道具自检：{safety.TotalChecked} 项受保护物品全部未被改动（含灵马哨笛、望远镜、携带照明灯、圣杯瓶）。");
                    }
                    else
                    {
                        Log($"关键道具自检：{safety.ChangedCount}/{safety.TotalChecked} 项被改动（异常！）");
                        foreach (SafetyCheckItem it in safety.Items.Where(i => i.Changed))
                            Log($"  !! {it.Name}(ID {it.ID}) 被改动 {it.ChangedFields} 个字段：{string.Join("；", it.Details)}");
                        report.Warnings.Add($"关键道具自检发现问题：{safety.ChangedCount} 项受保护物品被改动。");
                    }
                }
                catch (Exception ex)
                {
                    Log("关键道具自检失败：" + ex.Message);
                    report.Warnings.Add("关键道具自检失败：" + ex.Message);
                }
            }

            sw.Stop();
            report.Elapsed = sw.Elapsed;
            Report("完成", totalSteps, totalSteps);
            Log($"完成，用时 {sw.Elapsed.TotalSeconds:F1} 秒");
            return report;
        }

        /// <summary>
        /// 备份文件路径。统一使用 <c>regulation.bin.bak</c>，
        /// 与输入路径无关——就地覆盖、输出到别处两种工作流都指向同一个"原始文件"备份。
        /// 同时兼容 Erringulator 旧版命名（regulation-erringulator-backup.bin）。
        /// </summary>
        public static string BackupPathFor(string path)
        {
            string dir = Path.GetDirectoryName(path);
            string primary = Path.Combine(dir ?? "", Path.GetFileName(path) + ".bak");
            if (File.Exists(primary)) return primary;

            string legacy = Path.Combine(dir ?? "", $"{Path.GetFileNameWithoutExtension(path)}-erringulator-backup{Path.GetExtension(path)}");
            return File.Exists(legacy) ? legacy : primary;
        }

        /// <summary>
        /// 对单个参数表执行效果错位（同一张表可被多次处理，如武器效果 + 武器动作模组）。
        /// </summary>
        private ParamReport ProcessParam(BND4 regulation, BinderFile file, TargetSpec spec,
            Random rng, RandomizeReport report)
        {
            PARAM param = PARAM.Read(file.Bytes);
            if (!param.ApplyParamdefCarefully(_paramdefs))
            {
                report.Warnings.Add($"参数表 {spec.ParamName} 找不到行长为 {param.DetectedSize} 的定义，已跳过。");
                Log($"跳过 {spec.ParamName}（无匹配 paramdef）");
                return null;
            }

            PARAMDEF def = param.AppliedParamdef;
            var pr = new ParamReport
            {
                ParamName = spec.ParamName,
                ParamType = param.ParamType,
                TotalRows = param.Rows.Count,
                Description = spec.Description,
            };

            var fieldIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < def.Fields.Count; i++)
                fieldIndex[def.Fields[i].InternalName] = i;

            // 排除 ID<=0 的空槽位：给未使用的行写入数据没有意义
            PARAM.Row[] rows = param.Rows.Where(r => r.ID > 0 && spec.RowFilter(r)).ToArray();
            if (Environment.GetEnvironmentVariable("ERRINGULATOR_DIAG") == "1")
            {
                if (spec.ParamName == "EquipParamGoods")
                {
                    var ids = rows.Select(r => r.ID).ToHashSet();
                    Log($"[诊断] 道具参与 {ids.Count} 个；关键物品是否参战：" +
                        $"圣杯瓶1000={ids.Contains(1000)} 青瓶1050={ids.Contains(1050)} 灵马笛130={ids.Contains(130)} " +
                        $"望远镜2040={ids.Contains(2040)} 提灯2070={ids.Contains(2070)} 露滴10100={ids.Contains(10100)} " +
                        $"素材8600={ids.Contains(8600)} 笔记8700={ids.Contains(8700)} 地图11000={ids.Contains(11000)}");
                }
            }
            if (rows.Length < 2)
            {
                Log($"跳过 {spec.Description}（有效行数不足）");
                return null;
            }

            int[] perm = Permute.BuildPermutation(rows.Length, _settings.Mode, _settings.ShiftAmount, rng);
            int touchedFields = 0;

            // 1) 效果组：组内字段同步搬家，保持引用关系完整
            foreach (string[] group in spec.EffectGroups)
            {
                string[] usable = group.Where(fieldIndex.ContainsKey).ToArray();
                foreach (string missing in group.Where(f => !fieldIndex.ContainsKey(f)))
                    if (!pr.SkippedFields.Contains(missing)) pr.SkippedFields.Add(missing);

                if (usable.Length == 0) continue;

                var backup = new object[rows.Length][];
                for (int i = 0; i < rows.Length; i++)
                {
                    var v = new object[usable.Length];
                    for (int j = 0; j < usable.Length; j++)
                        v[j] = rows[i].Cells[fieldIndex[usable[j]]].Value;
                    backup[i] = v;
                }
                for (int i = 0; i < rows.Length; i++)
                {
                    object[] src = backup[perm[i]];
                    for (int j = 0; j < usable.Length; j++)
                        rows[i].Cells[fieldIndex[usable[j]]].Value = src[j];
                }

                touchedFields += usable.Length;
                pr.AppliedFields.Add($"组({usable.Length}字段)");
            }

            // 2) 独立字段：各自置换
            string[] looseUsable = spec.LooseFields.Where(fieldIndex.ContainsKey).ToArray();
            foreach (string missing in spec.LooseFields.Where(f => !fieldIndex.ContainsKey(f)))
                if (!pr.SkippedFields.Contains(missing)) pr.SkippedFields.Add(missing);

            foreach (string f in looseUsable)
            {
                int idx = fieldIndex[f];
                object[] src = new object[rows.Length];
                for (int i = 0; i < rows.Length; i++) src[i] = rows[i].Cells[idx].Value;
                for (int i = 0; i < rows.Length; i++) rows[i].Cells[idx].Value = src[perm[i]];
            }
            touchedFields += looseUsable.Length;
            pr.AppliedFields.AddRange(looseUsable);

            pr.AffectedRows = rows.Length;
            pr.AffectedFields = touchedFields;

            file.Bytes = param.Write();

            Log($"已随机化 {spec.Description}（{spec.ParamName}）：{rows.Length} 行 × {touchedFields} 字段" +
                (pr.SkippedFields.Count > 0 ? $"，{pr.SkippedFields.Count} 个字段在本版 paramdef 中不存在已跳过" : ""));

            return pr;
        }

        /// <summary>
        /// 温和模式的召唤物数值调整（完整模式由引擎内部处理）：
        /// 血量/伤害倍率加法落在 SpEffect 290000-290010；FP 倍率落在 Buddy 引用的 NPC 上。
        /// </summary>
        private void ApplyMildModeSummonTuning(SummonSettings ss,
            Dictionary<string, BinderFile> byName, BinderFile buddyFile, RandomizeReport report)
        {
            // 血量 / 伤害：SpEffect 290000-290010（骨灰强化曲线）加法
            if ((Math.Abs(ss.HpMultiplier - 1.0) > 0.0001 || Math.Abs(ss.DamageMultiplier - 1.0) > 0.0001) &&
                byName.TryGetValue("SpEffectParam", out BinderFile spFile))
            {
                PARAM sp = PARAM.Read(spFile.Bytes);
                if (sp.ApplyParamdefCarefully(_paramdefs))
                {
                    int n = SummonTuner.ApplyRateDeltas(sp, ss.HpMultiplier, ss.DamageMultiplier, Log);
                    if (n > 0)
                    {
                        spFile.Bytes = sp.Write();
                        report.Params.Add(new ParamReport
                        {
                            ParamName = "SpEffectParam",
                            ParamType = sp.ParamType,
                            Description = $"召唤物强化倍率（血量 ×{ss.HpMultiplier:0.##}，伤害 ×{ss.DamageMultiplier:0.##}）",
                            TotalRows = sp.Rows.Count,
                            AffectedRows = n,
                            AffectedFields = 6,
                        });
                    }
                }
                else
                {
                    report.Warnings.Add("SpEffect 无匹配 paramdef，召唤物倍率未应用。");
                }
            }

            // FP 倍率：只影响 Buddy 引用到的 NPC
            if (Math.Abs(ss.FpMultiplier - 1.0) > 0.0001 &&
                byName.TryGetValue("NpcParam", out BinderFile npcFile))
            {
                PARAM buddy = PARAM.Read(buddyFile.Bytes);
                PARAM npcParam = PARAM.Read(npcFile.Bytes);
                if (buddy.ApplyParamdefCarefully(_paramdefs) && npcParam.ApplyParamdefCarefully(_paramdefs))
                {
                    int n = SummonTuner.ApplyMpToReferenced(buddy, npcParam, ss.FpMultiplier);
                    if (n > 0)
                    {
                        npcFile.Bytes = npcParam.Write();
                        report.Params.Add(new ParamReport
                        {
                            ParamName = "NpcParam",
                            ParamType = npcParam.ParamType,
                            Description = $"召唤物 FP 倍率（×{ss.FpMultiplier:0.##}）",
                            TotalRows = npcParam.Rows.Count,
                            AffectedRows = n,
                            AffectedFields = 1,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 温和模式的召唤物随机化：把 BuddyParam 里每个召唤条目的
        /// 「npcParamId + npcThinkParamId」成对错位。成对搬家保证「敌人外观」与
        /// 「它的 AI」始终配套，于是某个骨灰会召唤出另一个骨灰原本的敌人，
        /// 而不会出现有模型没 AI 的哑弹。不新增/克隆任何行。
        /// </summary>
        private ParamReport ProcessBuddySummon(BinderFile file, Random rng, RandomizeReport report, SummonSettings ss)
        {
            PARAM param = PARAM.Read(file.Bytes);
            if (!param.ApplyParamdefCarefully(_paramdefs))
            {
                report.Warnings.Add("BuddyParam 找不到匹配的 paramdef，无法随机化召唤物。");
                Log("跳过 召唤物随机化（BuddyParam 无匹配 paramdef）");
                return null;
            }

            PARAMDEF def = param.AppliedParamdef;
            var fieldIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < def.Fields.Count; i++)
                fieldIndex[def.Fields[i].InternalName] = i;

            string[] pairFields = { "npcParamId", "npcThinkParamId" };
            foreach (string f in pairFields)
            {
                if (!fieldIndex.ContainsKey(f))
                {
                    report.Warnings.Add($"BuddyParam 缺少字段 {f}，无法随机化召唤物。");
                    return null;
                }
            }

            // 只取真正有召唤物配置的行（npcParamId 有效）
            PARAM.Row[] rows = param.Rows
                .Where(r => r.ID > 0 && GoodsInt(r, "npcParamId") > 0 && GoodsInt(r, "npcParamId") < 900000000)
                .ToArray();

            if (rows.Length < 2)
            {
                Log("跳过 召唤物随机化（有效召唤条目不足）");
                return null;
            }

            // 按池范围分组错位
            List<PARAM.Row[]> groups;
            if (ss.Pool == SummonPoolScope.SameSegment)
            {
                groups = rows.GroupBy(r => r.ID / 1000000)
                             .Select(g => g.ToArray())
                             .Where(g => g.Length >= 2)
                             .ToList();
            }
            else
            {
                groups = new List<PARAM.Row[]> { rows };
            }

            int touched = 0;
            foreach (PARAM.Row[] group in groups)
            {
                int[] perm = Permute.BuildPermutation(group.Length, _settings.Mode, _settings.ShiftAmount, rng);

                // npcParamId 与 npcThinkParamId 同步错位（保证配对有效）
                var backup = new long[group.Length][];
                for (int i = 0; i < group.Length; i++)
                {
                    backup[i] = new long[pairFields.Length];
                    for (int j = 0; j < pairFields.Length; j++)
                        backup[i][j] = GoodsInt(group[i], pairFields[j]);
                }
                for (int i = 0; i < group.Length; i++)
                {
                    long[] src = backup[perm[i]];
                    for (int j = 0; j < pairFields.Length; j++)
                    {
                        int idx = fieldIndex[pairFields[j]];
                        try { group[i].Cells[idx].Value = Convert.ToInt32(src[j]); } catch { }
                    }
                }

                // 骑乘形态的配对（可选）
                if (ss.RandomizeRidden)
                {
                    foreach (string f in new[] { "npcParamId_ridden", "npcThinkParamId_ridden" })
                    {
                        if (!fieldIndex.ContainsKey(f)) continue;
                        int idx = fieldIndex[f];
                        var vals = new object[group.Length];
                        for (int i = 0; i < group.Length; i++) vals[i] = group[i].Cells[idx].Value;
                        for (int i = 0; i < group.Length; i++) group[i].Cells[idx].Value = vals[perm[i]];
                    }
                }

                touched += group.Length;
            }

            if (touched == 0)
            {
                Log("跳过 召唤物随机化（没有可错位的分组）");
                return null;
            }

            file.Bytes = param.Write();

            var pr = new ParamReport
            {
                ParamName = "BuddyParam",
                ParamType = param.ParamType,
                Description = ss.Pool == SummonPoolScope.SameSegment
                    ? "召唤物（温和：同段位内错位）"
                    : "召唤物（温和：全池错位）",
                TotalRows = param.Rows.Count,
                AffectedRows = touched,
                AffectedFields = pairFields.Length,
            };
            pr.AppliedFields.AddRange(pairFields);

            Log($"已随机化 召唤物（BuddyParam）：{touched} 个召唤条目，池范围={(ss.Pool == SummonPoolScope.SameSegment ? "同段位" : "全部")}，" +
                $"npcParamId 与 npcThinkParamId 成对错位。");
            return pr;
        }

        /// <summary>
        /// 解除战灰的武器类型限制：把 EquipParamGem 中所有 canMountWep_* 开关置为 1。
        /// 这是「Unlocked Ashes of War」类 mod 的唯一机制——战灰能否装到某类武器上，
        /// 完全由这些开关决定；置 1 后任意武器都能装备任意战灰。
        /// </summary>
        private ParamReport ProcessGemUnlock(BinderFile file, RandomizeReport report)
        {
            PARAM param = PARAM.Read(file.Bytes);
            if (!param.ApplyParamdefCarefully(_paramdefs))
            {
                report.Warnings.Add("EquipParamGem 找不到匹配的 paramdef，无法解除战灰武器限制。");
                Log("跳过 解除战灰武器限制（EquipParamGem 无匹配 paramdef）");
                return null;
            }

            PARAMDEF def = param.AppliedParamdef;
            int[] mountFields = def.Fields
                .Select((f, i) => (f.InternalName, Index: i))
                .Where(x => x.InternalName.StartsWith("canMountWep_", StringComparison.Ordinal))
                .Select(x => x.Index)
                .ToArray();

            if (mountFields.Length == 0)
            {
                report.Warnings.Add("EquipParamGem 里没有 canMountWep_* 字段，无法解除战灰武器限制。");
                return null;
            }

            int rowsTouched = 0;
            int cellsSet = 0;
            foreach (PARAM.Row row in param.Rows)
            {
                if (row.ID <= 0) continue;
                bool touched = false;
                foreach (int idx in mountFields)
                {
                    if (def.Fields[idx].DisplayType == PARAMDEF.DefType.dummy8) continue;
                    object cur = row.Cells[idx].Value;
                    if (cur is byte b && b == 1) continue;
                    if (cur is sbyte sb && sb == 1) continue;
                    try
                    {
                        row.Cells[idx].Value = Convert.ToByte(1);
                        cellsSet++;
                        touched = true;
                    }
                    catch { /* 无法写入的字段跳过 */ }
                }
                if (touched) rowsTouched++;
            }

            file.Bytes = param.Write();

            var pr = new ParamReport
            {
                ParamName = "EquipParamGem",
                ParamType = param.ParamType,
                Description = "解除战灰武器限制",
                TotalRows = param.Rows.Count,
                AffectedRows = rowsTouched,
                AffectedFields = mountFields.Length,
            };
            pr.AppliedFields.Add("canMountWep_* 全部置 1");

            Log($"已解除战灰武器限制（EquipParamGem）：{rowsTouched} 行 × {mountFields.Length} 个 canMountWep_* 开关，共写入 {cellsSet} 处。");
            return pr;
        }

        // ==================== paramdef 载入 ====================
        public static string ResolveParamdefDir(string explicitDir = null)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(explicitDir)) candidates.Add(explicitDir);

            string baseDir = AppContext.BaseDirectory;
            candidates.Add(Path.Combine(baseDir, "paramdefs"));
            candidates.Add(Path.Combine(baseDir, "res", "defs"));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "paramdefs"));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "res", "defs"));
            // exe 的上一级目录（发布包里 exe 与 paramdefs 通常同层）
            string parent = Path.GetFullPath(Path.Combine(baseDir, ".."));
            candidates.Add(Path.Combine(parent, "paramdefs"));
            candidates.Add(Path.Combine(parent, "res", "defs"));

            foreach (string c in candidates)
            {
                try
                {
                    if (Directory.Exists(c) && Directory.GetFiles(c, "*.xml").Length > 0)
                        return c;
                }
                catch { /* 忽略不可访问的候选目录 */ }
            }

            throw new DirectoryNotFoundException(
                "找不到 paramdef 定义目录（需要包含 *.xml）。已尝试：\n" + string.Join("\n", candidates));
        }

        private static List<PARAMDEF> LoadParamdefs(string dir)
        {
            var list = new List<PARAMDEF>();
            foreach (string f in Directory.GetFiles(dir, "*.xml"))
            {
                try { list.Add(PARAMDEF.XmlDeserialize(f)); }
                catch { /* 个别 XML 解析失败不影响整体 */ }
            }
            return list;
        }
    }
}
