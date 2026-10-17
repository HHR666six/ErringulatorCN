using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        /// 道具黑名单——与原版 Erringulator 的 BlacklistGoods
        /// （RandomizerData / GeneratorData.json）逐项完全一致（共 208 项）：
        /// 圣杯瓶系列、灵药圣杯瓶、望远镜、提灯、灵马哨笛、记忆的恩惠、
        /// 多人联机道具、姿势动作、赠礼道具等。
        /// 这些道具的效果一旦被错位，游戏功能会直接损坏（无法召唤灵马、无法回血等）。
        /// 注意：维护本清单时请与原版保持同步，不要随意增删。
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
            // 红/蓝圣杯瓶全等级。注意：与原版完全一致地只收录
            // 1000-1025 与 1050-1075（1.2.10 起删除了原版没有的中间号段
            // 1026-1049——该号段在 1.16 里并不存在，删除无任何行为影响，
            // 但保证了与原版黑名单逐项相同）。
            1000, 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009,
            1010, 1011, 1012, 1013, 1014, 1015, 1016, 1017, 1018, 1019,
            1020, 1021, 1022, 1023, 1024, 1025,
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
        /// 3 = 追忆类道具（接肢的追忆、死龙的追忆、DLC 各追忆等 25 件，全表仅此类使用）——
        /// 应要求自 1.2.8 起参与随机化：它们的整套使用字段会与其他道具互相错位。
        /// </summary>
        private static readonly HashSet<int> GoodsUsableTypes = new HashSet<int>
        {
            0, 2, 4, 5, 9, 11, 15, 19, 27, 28,
            3,  // 追忆（Remembrance）类道具
        };

        // ==================== 对话数据健康检查 ====================

        /// <summary>
        /// 1.16 官方原版 TalkParam 的「说话动作配对指纹」（id↔talkAnimationId 对的哈希）。
        ///
        /// 只对"带说话动作的行"取值配对做指纹：旧版（1.2.2/1.2.3）的对话错位
        /// 必然把这些动画值搬离原行，因此该指纹必定改变；而其它工具
        /// （如 Elden Ring Randomizer 只改 5 行的 lipSyncStart）不会触发，
        /// 避免了误报。
        /// </summary>
        private const string VanillaTalkAnimFingerprint = "B729174AC3AB7BBA";

        /// <summary>计算 TalkParam 说话动作配对指纹（格式与诊断工具一致：id:anim 以 | 连接）。</summary>
        private static string ComputeTalkAnimFingerprint(PARAM talk)
        {
            var pairs = talk.Rows
                .Where(x => x.ID > 0 && GoodsInt(x, "talkAnimationId") != -1)
                .OrderBy(x => x.ID)
                .Select(x => x.ID + ":" + GoodsInt(x, "talkAnimationId"));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", pairs)));
            return Convert.ToHexString(hash).Substring(0, 16);
        }

        /// <summary>
        /// 对话数据体检：输入文件的「说话动作配对」若与 1.16 原版不一致，说明
        /// 它多半曾被本工具旧版本（1.2.2/1.2.3）搬乱过——那种损坏不会随重新
        /// 生成自动复原，部分 NPC（如商人）对话会仍然飞速掠过，此处提醒用户。
        /// 注：其它工具（mod 等）对 TalkParam 的正常修改通常不会改变该指纹；
        /// 若用户明确知道此文件被别的工具改过，可忽略该提醒。
        /// </summary>
        private void CheckTalkRhythmHealth(Dictionary<string, BinderFile> byName, RandomizeReport report)
        {
            if (!byName.TryGetValue("TalkParam", out BinderFile file)) return;
            PARAM talk = PARAM.Read(file.Bytes);
            if (!talk.ApplyParamdefCarefully(_paramdefs)) return;

            string fp = ComputeTalkAnimFingerprint(talk);
            if (fp == VanillaTalkAnimFingerprint)
            {
                Log("对话数据体检：说话动作配对与 1.16 原版一致。");
                return;
            }

            string msg =
                $"【提示】检测到输入文件的 NPC 对话「说话动作配对」与 1.16 原版不一致" +
                $"（当前指纹 {fp}，原版 {VanillaTalkAnimFingerprint}）。" +
                "如果这份文件曾被本工具的旧版本（1.2.2/1.2.3）做过对话随机，这类损坏不会因重新生成而复原，" +
                "部分 NPC（如商人的）对话会飞速掠过——请先「恢复备份」或用干净的原版文件作输入；" +
                "如果它是被 Elden Ring Randomizer 等其它工具正常修改过的文件，可忽略本条提示。";
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
        /// 保守模式判定：只有"带明确使用效果"的可直接使用道具才参与错位。
        /// 排除素材、笔记、地图、露滴、钥匙/内容物、未实装条目，以及黑名单道具。
        ///
        /// 1.2.10 起：不再要求 goodsUseAnim &gt; 0——部分真正可用的消耗品
        /// （苔药 900-960、干肝 1150-1180、生肉丸、DLC 药膳等 19 件）的
        /// 使用动作字段就是 0，但带真实效果（refId_default 等），应当参与。
        /// 判定改为「有明确效果即纳入；254/255 占位动作仍排除」——既无动作
        /// 又无效果的素材/杂项（211 行）依旧被排除在外。
        /// </summary>
        private static bool IsGoodsEligible(PARAM.Row row)
        {
            if (row.ID <= 0) return false;
            if (GoodsBlacklist.Contains(row.ID)) return false;

            if (!GoodsUsableTypes.Contains((int)GoodsInt(row, "goodsType"))) return false;

            // 254/255 是"未实装/不可用"的占位动作
            long anim = GoodsInt(row, "goodsUseAnim");
            if (anim >= 254) return false;

            // 必须带明确的使用效果，否则交换没有意义
            long refDefault = GoodsInt(row, "refId_default");
            long behavior = GoodsInt(row, "behaviorId");
            long spEffect = GoodsInt(row, "spEffectCategory");
            long enableSpEffect = GoodsInt(row, "useEnableSpEffectType");
            bool hasEffect = refDefault > 0 || behavior > 0 || spEffect > 0 || enableSpEffect > 0;
            return hasEffect;
        }

        /// <summary>非保守模式：沿用 Erringulator 的 goodsType == 0 过滤（仍排除黑名单）；追忆（type 3）同样参与。</summary>
        private static bool IsGoodsAnyType0(PARAM.Row row)
        {
            if (row.ID <= 0) return false;
            if (GoodsBlacklist.Contains(row.ID)) return false;
            long type = GoodsInt(row, "goodsType");
            return type == 0 || type == 3;
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

            // ---- 附加：护符效果（按 ID 约定错位 SpEffect 效果行）----
            // 护符的真实效果不在 EquipParamAccessory 的字段里，而是由游戏按 ID
            // 约定查找 SpEffect：SpEffectID = 310000 + (护符ID - 1000) × 10。
            // 只搬 refId 等字段无法改变游戏内效果，必须错位这些约定效果行本身。
            if (_settings.RandomizeEquipment && !_settings.NoOp)
            {
                ParamReport pr = ProcessAccessoryEffects(byName, rng, report);
                if (pr != null) report.Params.Add(pr);

                // ---- 子项：攻击元素错位（装备效果的延伸）----
                // 父项「武器效果」已在武器层错位元素字段（spAttribute /
                // spAtkcategory / attackElementCorrectId / spAttributeVariationValue）；
                // 本子项把元素错位延伸到「攻击判定层」——AtkParam_Pc / AtkParam_Npc
                // 每行的魔/火/雷/暗伤害槽连同各自攻击修正在行内旋转。
                // 判定/动画/特效/音效不动，只换实际生效的元素伤害类型。
                // 与 Elden Ring Randomizer 正交（它从不改攻击参数表）。
                if (_settings.RandomizeAttackElements)
                {
                    ParamReport prAtk = ProcessAttackElements(byName, rng, report);
                    if (prAtk != null) report.Params.Add(prAtk);
                }
            }

            // ---- 附加：敌人伤害抗性随机（1.3.1 新增，独立开关 + Boss/非Boss 范围）----
            // NpcParam 的异常状态抗性（XXGuardResist）与元素减伤
            //（mag/fire/thun/darkGuardCutRate）在同类字段内行间错位；
            // Boss 与非 Boss 分池独立置换，互不交叉。
            if (_settings.RandomizeEnemyResist && !_settings.NoOp)
            {
                ParamReport prRes = ProcessEnemyResist(byName, rng, report);
                if (prRes != null) report.Params.Add(prRes);
            }

            // ---- 附加：敌人韧性随机（1.6.0 新增，独立开关 + Boss/非Boss 范围）----
            // NpcParam.toughness 是 ToughnessParam 的行索引（0/20/35 → 补正 0/1.5/3.0），
            // 在同类敌人池内成对错位「档位索引 + 回复补正」，引用零风险。
            if (_settings.RandomizeEnemyToughness && !_settings.NoOp)
            {
                ParamReport prTgh = ProcessEnemyToughness(byName, rng, report);
                if (prTgh != null) report.Params.Add(prTgh);
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
        /// 护符效果错位（真正生效的那一步）。
        ///
        /// 实测确认：护符的能力加成（如玛丽卡的烙印 +3 心智/智力/信仰/感应、
        /// 烈风的护符 +5 力气）并不写在 EquipParamAccessory 的 refId 等字段里，
        /// 而是由游戏按 ID 约定查找 SpEffect：
        ///     SpEffectID = 310000 + (护符ID − 1000) × 10
        /// （1.16 实测：157 个护符中 116 个存在这样的约定效果行，一一对应。）
        ///
        /// 因此这里把这些约定效果行的整行内容互相错位——某个护符会真正
        /// 获得另一个护符的能力加成，游戏内效果随之改变。
        /// </summary>
        private ParamReport ProcessAccessoryEffects(Dictionary<string, BinderFile> byName,
            Random rng, RandomizeReport report)
        {
            if (!byName.TryGetValue("EquipParamAccessory", out BinderFile accFile) ||
                !byName.TryGetValue("SpEffectParam", out BinderFile spFile))
            {
                report.Warnings.Add("护符效果错位：缺少 EquipParamAccessory 或 SpEffectParam 参数表，已跳过。");
                return null;
            }

            PARAM acc = PARAM.Read(accFile.Bytes);
            PARAM sp = PARAM.Read(spFile.Bytes);
            if (!acc.ApplyParamdefCarefully(_paramdefs) || !sp.ApplyParamdefCarefully(_paramdefs))
            {
                report.Warnings.Add("护符效果错位：EquipParamAccessory / SpEffectParam 无匹配 paramdef，已跳过。");
                return null;
            }

            var spById = new Dictionary<int, PARAM.Row>();
            foreach (PARAM.Row r in sp.Rows)
                if (r.ID > 0 && !spById.ContainsKey(r.ID)) spById[r.ID] = r;

            // 收集 (护符ID, 约定效果行) 配对，按护符 ID 排序保证确定性
            var pairs = new List<(int AccId, PARAM.Row Row)>();
            foreach (PARAM.Row a in acc.Rows)
            {
                if (a.ID < 1000) continue;
                long spId = 310000L + (a.ID - 1000L) * 10L;
                if (spId > int.MaxValue) continue;
                if (spById.TryGetValue((int)spId, out PARAM.Row er))
                    pairs.Add((a.ID, er));
            }
            pairs.Sort((x, y) => x.AccId.CompareTo(y.AccId));

            if (pairs.Count < 2)
            {
                report.Warnings.Add($"护符效果错位：按 ID 约定只找到 {pairs.Count} 个效果行，已跳过。");
                Log("跳过 护符效果错位（约定效果行不足）");
                return null;
            }

            int[] perm = Permute.BuildPermutation(pairs.Count, _settings.Mode, _settings.ShiftAmount, rng);

            // 整行内容错位（ID 不动，行的全部单元格互相交换）
            var backup = new object[pairs.Count][];
            for (int i = 0; i < pairs.Count; i++)
            {
                PARAM.Row row = pairs[i].Row;
                var cells = new object[row.Cells.Count];
                for (int j = 0; j < cells.Length; j++) cells[j] = row.Cells[j].Value;
                backup[i] = cells;
            }
            for (int i = 0; i < pairs.Count; i++)
            {
                object[] src = backup[perm[i]];
                var cells = pairs[i].Row.Cells;
                for (int j = 0; j < cells.Count && j < src.Length; j++) cells[j].Value = src[j];
            }

            spFile.Bytes = sp.Write();
            Log($"已随机化 护符效果（SpEffect 约定行错位）：{pairs.Count} 个护符的效果行内容互相错位" +
                $"（例：护符 {pairs[0].AccId} 现在生效 SpEffect {pairs[0].Row.ID} 位置上的其他护符效果）。");

            return new ParamReport
            {
                ParamName = "SpEffectParam",
                ParamType = sp.ParamType,
                Description = "护符效果（按 ID 约定错位效果行）",
                TotalRows = sp.Rows.Count,
                AffectedRows = pairs.Count,
                AffectedFields = sp.AppliedParamdef.Fields.Count,
            };
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

        /// <summary>
        /// 攻击元素错位（装备效果的子项）——AtkParam_Pc / AtkParam_Npc。
        ///
        /// 机制（数据侧 1.16 实测）：
        ///   · AtkParam 每行是一次攻击判定，带 4 个非物理元素伤害槽——
        ///     atkMag（魔力）/ atkFire（火焰）/ atkThun（雷电）/ atkDark（暗）
        ///     ——以及对应的百分比修正 atkXxxCorrection。
        ///   · 游戏按「哪槽有值就按哪类伤害结算」；攻击的动画、判定、特效、音效
        ///     由武器/法术与行为自身决定，不随伤害槽变化。因此把四个槽连同修正
        ///     在行内整体旋转，就能实现「刀砍出火、法术劈雷」，而表现全部保持原样。
        ///   · 纯行内置换：不跨行、不引用其它表，天然零引用风险；行内四槽的
        ///     数值多重集守恒（总伤害规模不变，只换类型）。
        ///     只处理「四槽中至少一个非 0」的行（多数行靠武器基础值结算，自动跳过）。
        ///
        /// 1.3.1 增强——spAttributeVariationValue（SFX 变体值，u8）：
        ///   · 它与 spAttribute 组合决定命中特效的元素视觉变体（paramdef 描述：
        ///     「特殊属性と組み合わせてSFX、SEにバリエーションを持たせる」）。
        ///   · 单字段无法行内旋转；改为在「同表内带值的行」之间做行间循环移位
        ///     （ShiftAmount 参与的确定性置换）——每个有变体的判定行换成别的
        ///     判定行的变体值，命中特效的元素视觉随伤害换位而更明显。
        ///     它是纯表现参数（不参与任何伤害计算），换值不产生引用风险。
        /// </summary>
        private ParamReport ProcessAttackElements(Dictionary<string, BinderFile> byName,
            Random rng, RandomizeReport report)
        {
            string[] dmgFields = { "atkMag", "atkFire", "atkThun", "atkDark" };
            string[] corrFields = { "atkMagCorrection", "atkFireCorrection", "atkThunCorrection", "atkDarkCorrection" };
            const string variationField = "spAttributeVariationValue";

            int totalTouched = 0;
            int totalVarTouched = 0;
            int totalRows = 0;
            bool anyWritten = false;

            foreach (string tbl in new[] { "AtkParam_Pc", "AtkParam_Npc" })
            {
                if (!byName.TryGetValue(tbl, out BinderFile file))
                {
                    report.Warnings.Add($"攻击元素错位：缺少 {tbl}，该表已跳过。");
                    continue;
                }

                PARAM atk = PARAM.Read(file.Bytes);
                if (!atk.ApplyParamdefCarefully(_paramdefs))
                {
                    report.Warnings.Add($"攻击元素错位：{tbl} 无匹配 paramdef，已跳过。");
                    continue;
                }

                var fieldIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < atk.AppliedParamdef.Fields.Count; i++)
                    fieldIndex[atk.AppliedParamdef.Fields[i].InternalName] = i;

                if (!dmgFields.All(fieldIndex.ContainsKey) || !corrFields.All(fieldIndex.ContainsKey))
                {
                    report.Warnings.Add($"攻击元素错位：{tbl} 缺少元素伤害或修正字段，已跳过。");
                    continue;
                }
                bool hasVariation = fieldIndex.ContainsKey(variationField);

                int touched = 0;
                foreach (PARAM.Row r in atk.Rows)
                {
                    if (r.ID <= 0) continue;

                    var dmg = new double[4];
                    var corr = new double[4];
                    bool anyDmg = false;
                    for (int i = 0; i < 4; i++)
                    {
                        dmg[i] = GoodsDouble(r, dmgFields[i]);
                        corr[i] = GoodsDouble(r, corrFields[i]);
                        if (dmg[i] != 0.0) anyDmg = true;
                    }
                    if (!anyDmg) continue;   // 纯基础值行不动

                    // 行内旋转 1~3 位（保证至少一个槽变化）
                    int shift = 1 + rng.Next(3);
                    bool changed = false;

                    for (int i = 0; i < 4; i++)
                    {
                        int src = (i + shift) % 4;
                        if (WriteCellDouble(r.Cells[fieldIndex[dmgFields[i]]], dmg[src])) changed = true;
                        if (WriteCellDouble(r.Cells[fieldIndex[corrFields[i]]], corr[src])) changed = true;
                    }

                    if (changed) touched++;
                }

                // ---- 1.3.1：spAttributeVariationValue 行间循环移位 ----
                int varTouched = 0;
                if (hasVariation)
                {
                    int varIdx = fieldIndex[variationField];
                    var varRows = new List<(PARAM.Row Row, double Value)>();
                    foreach (PARAM.Row r in atk.Rows)
                    {
                        if (r.ID <= 0) continue;
                        double v = GoodsDouble(r, variationField);
                        if (v > 0) varRows.Add((r, v));   // 只动带变体值的行（0 = 无变体，保持）
                    }
                    if (varRows.Count >= 2)
                    {
                        int[] perm = Permute.BuildPermutation(varRows.Count, _settings.Mode, _settings.ShiftAmount, rng);
                        var backup = varRows.Select(x => x.Value).ToArray();
                        for (int i = 0; i < varRows.Count; i++)
                        {
                            if (WriteCellDouble(varRows[i].Row.Cells[varIdx], backup[perm[i]])) varTouched++;
                        }
                    }
                }

                if (touched > 0 || varTouched > 0)
                {
                    file.Bytes = atk.Write();
                    anyWritten = true;
                    totalRows += atk.Rows.Count;
                    totalTouched += touched;
                    totalVarTouched += varTouched;
                    Log($"已随机化 攻击元素（{tbl}）：{touched} 行元素槽行内换位" +
                        (varTouched > 0 ? $"，{varTouched} 行 SFX 变体值换位（视觉表现）" : "") + "。");
                }
                else
                {
                    Log($"跳过 攻击元素（{tbl}）：没有带元素伤害的行。");
                }
            }

            if (!anyWritten)
            {
                Log("跳过 攻击元素错位（没有可处理的行）");
                return null;
            }

            var pr = new ParamReport
            {
                ParamName = "AtkParam_Pc + AtkParam_Npc",
                ParamType = "ATK_PARAM_ST",
                Description = "攻击元素错位（装备效果子项：魔/火/雷/暗行内换位 + SFX 变体换位）",
                TotalRows = totalRows,
                AffectedRows = totalTouched,
                AffectedFields = hasVariationFieldCount(dmgFields, corrFields, true),   // 4 伤害槽 + 4 修正槽 + 1 变体
            };
            pr.AppliedFields.AddRange(dmgFields);
            pr.AppliedFields.AddRange(corrFields);
            pr.AppliedFields.Add(variationField);
            return pr;
        }

        /// <summary>小的本地辅助：统一计算报告里的字段数。</summary>
        private static int hasVariationFieldCount(string[] dmg, string[] corr, bool hasVar) =>
            dmg.Length + corr.Length + (hasVar ? 1 : 0);

        /// <summary>
        /// 敌人伤害抗性随机（1.3.1 新增）——NpcParam。
        ///
        /// 数据侧实测（1.16）：
        ///   · NpcParam 的 def_mag/fire/thunder/dark 全表恒 100——本体无差异，
        ///     不是本功能的对象。
        ///   · 真实抗性差异在两组字段上：
        ///     (a) XXGuardResist（poison/disease/blood/curse/sleep/madness/freeze）：
        ///         常态生效的异常状态抗性（百分比减伤，值域 20/60/70/80 等）；
        ///     (b) magGuardCutRate / fireGuardCutRate / thunGuardCutRate / darkGuardCutRate：
        ///         防御姿态时的元素减伤率（40/50/70/80 等）。
        ///
        /// 做法：两组字段各自在「同类字段内」做行间错位——敌人 A 的毒抗换成
        /// 敌人 B 的毒抗，敌人 C 的火抗减伤换成敌人 D 的……每个同类字段的
        /// 池内多重集守恒（不增不减），结构与引用完全不动。
        ///
        /// Boss 分池：npcType==1 或 isSoulGetByBoss==1 视为 Boss 行；
        /// Boss 池与非 Boss 池各自独立置换（Boss 只跟 Boss 交换抗性），
        /// 由 EnemyResistScope 控制哪些池参与。
        /// </summary>
        private ParamReport ProcessEnemyResist(Dictionary<string, BinderFile> byName,
            Random rng, RandomizeReport report)
        {
            if (!byName.TryGetValue("NpcParam", out BinderFile npcFile))
            {
                report.Warnings.Add("敌人抗性随机：缺少 NpcParam，已跳过。");
                return null;
            }

            PARAM npc = PARAM.Read(npcFile.Bytes);
            if (!npc.ApplyParamdefCarefully(_paramdefs))
            {
                report.Warnings.Add("敌人抗性随机：NpcParam 无匹配 paramdef，已跳过。");
                return null;
            }

            var fieldIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < npc.AppliedParamdef.Fields.Count; i++)
                fieldIndex[npc.AppliedParamdef.Fields[i].InternalName] = i;

            // 异常状态抗性（常态生效）+ 元素减伤（防御时生效）
            string[] resistFields =
            {
                "poisonGuardResist", "diseaseGuardResist", "bloodGuardResist",
                "curseGuardResist", "sleepGuardResist", "madnessGuardResist", "freezeGuardResist",
                "magGuardCutRate", "fireGuardCutRate", "thunGuardCutRate", "darkGuardCutRate",
            };
            var usable = resistFields.Where(fieldIndex.ContainsKey).ToArray();
            if (usable.Length == 0)
            {
                report.Warnings.Add("敌人抗性随机：NpcParam 缺少抗性字段，已跳过。");
                return null;
            }
            foreach (string missing in resistFields.Where(f => !fieldIndex.ContainsKey(f)))
                Log($"敌人抗性随机：字段 {missing} 在本版 paramdef 中不存在，已跳过该字段。");

            var scope = _settings.EnemyResistScope;
            bool wantBoss = (scope & EnemyResistScope.Boss) != 0;
            bool wantNonBoss = (scope & EnemyResistScope.NonBoss) != 0;
            if (!wantBoss && !wantNonBoss)
            {
                Log("跳过 敌人抗性随机（范围未选择任何敌人）");
                return null;
            }

            int touched = 0;
            int bossTouched = 0, nonBossTouched = 0;

            foreach (string f in usable)
            {
                int idx = fieldIndex[f];
                var bossPool = new List<(PARAM.Row Row, double Value)>();
                var normalPool = new List<(PARAM.Row Row, double Value)>();

                foreach (PARAM.Row r in npc.Rows)
                {
                    if (r.ID <= 0) continue;
                    if ((r.Name ?? "").StartsWith(SummonRandomizerEngine.CloneNamePrefix, StringComparison.Ordinal))
                        continue;   // 召唤物克隆行不参与（它们会被召唤引擎重建）
                    double v = GoodsDouble(r, f);
                    if (v <= 0) continue;   // 0 = 无抗性标记，保持原样
                    if (IsBossRow(r)) bossPool.Add((r, v));
                    else normalPool.Add((r, v));
                }

                int before = touched;
                if (wantBoss && bossPool.Count >= 2)
                {
                    int[] perm = Permute.BuildPermutation(bossPool.Count, _settings.Mode, _settings.ShiftAmount, rng);
                    var backup = bossPool.Select(x => x.Value).ToArray();
                    for (int i = 0; i < bossPool.Count; i++)
                        if (WriteCellDouble(bossPool[i].Row.Cells[idx], backup[perm[i]])) touched++;
                    bossTouched += touched - before;
                    before = touched;
                }
                if (wantNonBoss && normalPool.Count >= 2)
                {
                    int[] perm = Permute.BuildPermutation(normalPool.Count, _settings.Mode, _settings.ShiftAmount, rng);
                    var backup = normalPool.Select(x => x.Value).ToArray();
                    for (int i = 0; i < normalPool.Count; i++)
                        if (WriteCellDouble(normalPool[i].Row.Cells[idx], backup[perm[i]])) touched++;
                    nonBossTouched += touched - before;
                }
            }

            if (touched == 0)
            {
                Log("跳过 敌人抗性随机（没有带抗性值的行）");
                return null;
            }

            npcFile.Bytes = npc.Write();

            string scopeDesc = (wantBoss && wantNonBoss) ? "Boss + 非Boss"
                : wantBoss ? "仅 Boss" : "仅非 Boss";
            Log($"已随机化 敌人抗性（NpcParam，{scopeDesc}）：{usable.Length} 类抗性字段行间错位，" +
                $"Boss 池改动 {bossTouched} 处，非 Boss 池改动 {nonBossTouched} 处。");

            var pr = new ParamReport
            {
                ParamName = "NpcParam",
                ParamType = npc.ParamType,
                Description = $"敌人伤害抗性随机（{scopeDesc}：异常状态抗性 + 元素减伤同类换主）",
                TotalRows = npc.Rows.Count,
                AffectedRows = touched,
                AffectedFields = usable.Length,
            };
            pr.AppliedFields.AddRange(usable);
            return pr;
        }

        /// <summary>
        /// Boss 判定（抗性随机与韧性随机共用）：npcType==1（Boss 敌人类型）
        /// 或 isSoulGetByBoss==1（Boss 掉魂标记）任一命中即视为 Boss 行。
        /// </summary>
        private static bool IsBossRow(PARAM.Row r)
        {
            long nt, sb;
            try { nt = Convert.ToInt64(r["npcType"].Value); } catch { nt = 0; }
            try { sb = Convert.ToInt64(r["isSoulGetByBoss"].Value); } catch { sb = 0; }
            return nt == 1 || sb == 1;
        }

        /// <summary>
        /// 敌人韧性随机（1.6.0 新增）——NpcParam。
        ///
        /// 数据侧实测（1.16）：
        ///   · NpcParam.toughness 是 <b>ToughnessParam 的行索引</b>，不是韧性数值本身：
        ///     索引 0 → correctionRate 0（无韧性）、20 → 1.5、35 → 3.0；
        ///     ToughnessParam 共 84 行（ID 0~251），是「档位 → 补正倍率」查表。
        ///   · 全表只有 3 个档位被敌人使用：0（5511 个）、20（41）、35（1492）。
        ///
        /// 做法：在「同一类敌人池」内把 (toughness, toughnessRecoverCorrection)
        /// 成对错位——每个敌人换成另一个敌人的完整韧性档位。
        ///   · 索引始终取自本池已有的值，引用零风险；
        ///   · 池内多重集守恒（档位分布不变，只换持有者）；
        ///   · Boss 池与非 Boss 池独立置换，BOSS 不会拿到杂兵的档位。
        ///
        /// 注意： toughness=0 是合法的「无韧性档位」，因此 0 值行也参与置换
        /// （与抗性随机不同，那里 0 表示「未设置」）。
        /// </summary>
        private ParamReport ProcessEnemyToughness(Dictionary<string, BinderFile> byName,
            Random rng, RandomizeReport report)
        {
            if (!byName.TryGetValue("NpcParam", out BinderFile npcFile))
            {
                report.Warnings.Add("敌人韧性随机：缺少 NpcParam，已跳过。");
                return null;
            }

            PARAM npc = PARAM.Read(npcFile.Bytes);
            if (!npc.ApplyParamdefCarefully(_paramdefs))
            {
                report.Warnings.Add("敌人韧性随机：NpcParam 无匹配 paramdef，已跳过。");
                return null;
            }

            var fieldIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < npc.AppliedParamdef.Fields.Count; i++)
                fieldIndex[npc.AppliedParamdef.Fields[i].InternalName] = i;

            if (!fieldIndex.ContainsKey("toughness"))
            {
                report.Warnings.Add("敌人韧性随机：NpcParam 缺少 toughness 字段，已跳过。");
                return null;
            }
            bool hasRecover = fieldIndex.ContainsKey("toughnessRecoverCorrection");

            var scope = _settings.EnemyToughnessScope;
            bool wantBoss = (scope & EnemyToughnessScope.Boss) != 0;
            bool wantNonBoss = (scope & EnemyToughnessScope.NonBoss) != 0;
            if (!wantBoss && !wantNonBoss)
            {
                Log("跳过 敌人韧性随机（范围未选择任何敌人）");
                return null;
            }

            int touched = 0, bossTouched = 0, nonBossTouched = 0;

            // 池 = (行, 韧性档位索引, 回复补正值)；成对搬运，保持档位完整
            var bossPool = new List<(PARAM.Row Row, double Tough, double Recover)>();
            var normalPool = new List<(PARAM.Row Row, double Tough, double Recover)>();
            foreach (PARAM.Row r in npc.Rows)
            {
                if (r.ID <= 0) continue;
                if ((r.Name ?? "").StartsWith(SummonRandomizerEngine.CloneNamePrefix, StringComparison.Ordinal))
                    continue;   // 召唤物克隆行不参与（由召唤引擎重建）
                double t = GoodsDouble(r, "toughness");
                double rc = hasRecover ? GoodsDouble(r, "toughnessRecoverCorrection") : 0.0;
                bool boss = IsBossRow(r);
                if (boss) bossPool.Add((r, t, rc)); else normalPool.Add((r, t, rc));
            }

            void PermutePool(List<(PARAM.Row Row, double Tough, double Recover)> pool, ref int counter)
            {
                if (pool.Count < 2) return;
                int[] perm = Permute.BuildPermutation(pool.Count, _settings.Mode, _settings.ShiftAmount, rng);
                var backup = pool.Select(x => (x.Tough, x.Recover)).ToArray();
                for (int i = 0; i < pool.Count; i++)
                {
                    var (t, rc) = backup[perm[i]];
                    if (WriteCellDouble(pool[i].Row.Cells[fieldIndex["toughness"]], t)) counter++;
                    if (hasRecover && WriteCellDouble(pool[i].Row.Cells[fieldIndex["toughnessRecoverCorrection"]], rc)) counter++;
                }
            }

            int before = touched;
            if (wantBoss) { PermutePool(bossPool, ref touched); bossTouched = touched - before; }
            before = touched;
            if (wantNonBoss) { PermutePool(normalPool, ref touched); nonBossTouched = touched - before; }

            if (touched == 0)
            {
                Log("跳过 敌人韧性随机（没有可置换了行的行）");
                return null;
            }

            npcFile.Bytes = npc.Write();

            string scopeDesc = (wantBoss && wantNonBoss) ? "Boss + 非Boss"
                : wantBoss ? "仅 Boss" : "仅非 Boss";
            Log($"已随机化 敌人韧性（NpcParam，{scopeDesc}）：韧性档位索引 + 回复补正成对错位，" +
                $"Boss 池改动 {bossTouched} 处，非 Boss 池改动 {nonBossTouched} 处。");

            var pr = new ParamReport
            {
                ParamName = "NpcParam",
                ParamType = npc.ParamType,
                Description = $"敌人韧性随机（{scopeDesc}：韧性档位索引换主，对应 ToughnessParam 行）",
                TotalRows = npc.Rows.Count,
                AffectedRows = touched,
                AffectedFields = hasRecover ? 2 : 1,
            };
            pr.AppliedFields.Add("toughness");
            if (hasRecover) pr.AppliedFields.Add("toughnessRecoverCorrection");
            return pr;
        }

        /// <summary>安全读 double（单元格可能为 int/uint/float 等类型）。</summary>
        private static double GoodsDouble(PARAM.Row row, string field)
        {
            try { return Convert.ToDouble(row[field].Value, CultureInfo.InvariantCulture); }
            catch { return 0.0; }
        }

        /// <summary>按目标单元格的原生类型写入浮点值；值未变化时返回 false。</summary>
        private static bool WriteCellDouble(PARAM.Cell cell, double value)
        {
            try
            {
                object cur = cell.Value;
                if (cur == null) return false;
                if (Math.Abs(Convert.ToDouble(cur, CultureInfo.InvariantCulture) - value) < 1e-9) return false;

                switch (cur)
                {
                    case float _: cell.Value = (float)value; return true;
                    case double _: cell.Value = value; return true;
                    case byte _: cell.Value = unchecked((byte)(long)Math.Round(value)); return true;
                    case sbyte _: cell.Value = unchecked((sbyte)(long)Math.Round(value)); return true;
                    case short _: cell.Value = unchecked((short)(long)Math.Round(value)); return true;
                    case ushort _: cell.Value = unchecked((ushort)(long)Math.Round(value)); return true;
                    case int _: cell.Value = unchecked((int)(long)Math.Round(value)); return true;
                    case uint _: cell.Value = unchecked((uint)(long)Math.Round(value)); return true;
                    case long _: cell.Value = (long)Math.Round(value); return true;
                    default:
                        cell.Value = Convert.ChangeType(value, cur.GetType(), CultureInfo.InvariantCulture);
                        return true;
                }
            }
            catch { return false; }
        }

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
