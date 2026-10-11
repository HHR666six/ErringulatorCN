using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ErringulatorCN.Core
{
    /// <summary>召唤物随机化的处理方式。</summary>
    public enum SummonEngineMode
    {
        /// <summary>温和：只把现有召唤条目的「敌人 + AI」成对错位，不新增/克隆行。</summary>
        Mild = 0,

        /// <summary>
        /// 完整：移植 Elden Ring Summon Randomizer v1.0.8 的完整算法——
        /// 随机换敌、多召（一次召出多个）、驯服克隆（teamType=47 等）、
        /// 仿身泪滴特例、骨灰召唤消耗重掷、强化倍率。可选六套预设。
        /// </summary>
        Full = 1,
    }

    /// <summary>
    /// 召唤物随机化设置。
    /// 完整模式的 14 项参数与原版 Summon Randomizer v1.0.8 的
    /// 「Settings Preset」字符串完全互通（逗号分隔，可直接互相复制粘贴）。
    /// </summary>
    public sealed class SummonSettings
    {
        /// <summary>处理方式：温和 / 完整。</summary>
        public SummonEngineMode Engine { get; set; } = SummonEngineMode.Full;

        // ==== 完整模式参数（字段顺序与 SR 设置串一致）====

        /// <summary>变体复用几率 %（某个敌人被选后，仍允许选它同族变体的概率）。</summary>
        public int VariantReuseChance { get; set; } = 20;

        /// <summary>初始多召几率 %（一个骨灰一次召出多于 1 个敌人的概率）。</summary>
        public int MultiBuddyInitialChance { get; set; } = 50;

        /// <summary>多召上限：最多一次召出的数量（1 = 不多召；上限 9 → 最多 10 个）。</summary>
        public int MultiBuddyMax { get; set; } = 5;

        /// <summary>追加多召几率 %（已有 2+ 个时，再追加第 3、4、5… 个的概率）。</summary>
        public int MultiBuddyAdditionalChance { get; set; } = 50;

        /// <summary>多召重复几率 %（第 2+ 个多召与第 1 个是同一种敌人的概率）。</summary>
        public int MultiBuddyDupeChance { get; set; } = 80;

        /// <summary>允许骨灰重复：同一个敌人可被多个骨灰选用（否则尽量不重复）。</summary>
        public bool BuddyReuse { get; set; }

        /// <summary>禁止大体积多召：多召(第 2 个起)不选碰撞体大的敌人，避免挤在一起物理异常。</summary>
        public bool ForbidLargeMultiBuddies { get; set; } = true;

        // ==== 骨灰召唤消耗重掷（EquipParamGoods 里 goodsUseAnim=34 的行）====

        /// <summary>是否重掷骨灰的召唤消耗（FP/HP）。关闭则保持原版消耗。</summary>
        public bool RandomizeAshCosts { get; set; } = true;

        /// <summary>FP 消耗范围下限（INCLUDING；当未触发 HP 消耗时使用）。</summary>
        public int FpCostMin { get; set; } = 30;

        /// <summary>FP 消耗范围上限（不含，与原版一致）。</summary>
        public int FpCostMax { get; set; } = 60;

        /// <summary>改用 HP 消耗的几率 %。</summary>
        public int HpCostChance { get; set; } = 10;

        /// <summary>HP 消耗范围下限（含）。</summary>
        public int HpCostMin { get; set; } = 300;

        /// <summary>HP 消耗范围上限（不含）。</summary>
        public int HpCostMax { get; set; } = 600;

        // ==== 召唤物倍率（两种模式都会应用）====

        /// <summary>是否应用召唤物倍率（关闭则召唤物数值保持原样）。</summary>
        public bool ApplyMultipliers { get; set; } = true;

        /// <summary>血量倍率（加法落在 SpEffect 290000-290010 的 maxHpRate 上，与原版一致）。</summary>
        public double HpMultiplier { get; set; } = 2.0;

        /// <summary>伤害倍率（加法落在 SpEffect 290000-290010 的物/魔/火/雷/暗攻击倍率上）。</summary>
        public double DamageMultiplier { get; set; } = 3.0;

        /// <summary>FP 倍率（仅在完整模式下对克隆出来的召唤 NPC 的 mp 生效；1.0 = 不变）。</summary>
        public double FpMultiplier { get; set; } = 1.0;

        // ==== 温和模式参数（1.0.x 行为）====

        /// <summary>温和模式：错位池范围。</summary>
        public SummonPoolScope Pool { get; set; } = SummonPoolScope.All;

        /// <summary>温和模式：是否同时错位骑乘形态（npcParamId_ridden 等）。</summary>
        public bool RandomizeRidden { get; set; } = true;

        // ==================== 规范化 ====================

        /// <summary>把数值夹进合法范围，避免用户输入越界。</summary>
        public void Clamp()
        {
            VariantReuseChance = ClampInt(VariantReuseChance, 0, 100);
            MultiBuddyInitialChance = ClampInt(MultiBuddyInitialChance, 0, 100);
            MultiBuddyAdditionalChance = ClampInt(MultiBuddyAdditionalChance, 0, 100);
            MultiBuddyDupeChance = ClampInt(MultiBuddyDupeChance, 0, 100);
            MultiBuddyMax = ClampInt(MultiBuddyMax, 1, 9);

            FpCostMin = ClampInt(FpCostMin, 0, 999);
            FpCostMax = ClampInt(FpCostMax, 0, 999);
            if (FpCostMax < FpCostMin) FpCostMax = FpCostMin;
            HpCostChance = ClampInt(HpCostChance, 0, 100);
            HpCostMin = ClampInt(HpCostMin, 0, 9999);
            HpCostMax = ClampInt(HpCostMax, 0, 9999);
            if (HpCostMax < HpCostMin) HpCostMax = HpCostMin;

            HpMultiplier = ClampDouble(HpMultiplier, 0.01, 10.0);
            DamageMultiplier = ClampDouble(DamageMultiplier, 0.01, 10.0);
            FpMultiplier = ClampDouble(FpMultiplier, 0.01, 10.0);
        }

        private static int ClampInt(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        private static double ClampDouble(double v, double lo, double hi) =>
            double.IsNaN(v) ? lo : v < lo ? lo : v > hi ? hi : v;

        // ==================== 与原版 Summon Randomizer 互通的设置串 ====================

        /// <summary>
        /// 导出为原版 SR 的「Settings Preset」字符串（14 项、逗号分隔、末尾含逗号）。
        /// 可以直接粘贴进原版工具，也可以用本工具的「应用」按钮恢复。
        /// </summary>
        public string ToSettingsString()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append(VariantReuseChance).Append(',');
            sb.Append(MultiBuddyInitialChance).Append(',');
            sb.Append(MultiBuddyMax).Append(',');
            sb.Append(MultiBuddyAdditionalChance).Append(',');
            sb.Append(MultiBuddyDupeChance).Append(',');
            sb.Append(BuddyReuse ? "True" : "False").Append(',');
            sb.Append(HpMultiplier.ToString("0.##", ci)).Append(',');
            sb.Append(DamageMultiplier.ToString("0.##", ci)).Append(',');
            sb.Append(FpCostMin).Append(',');
            sb.Append(FpCostMax).Append(',');
            sb.Append(HpCostChance).Append(',');
            sb.Append(HpCostMin).Append(',');
            sb.Append(HpCostMax).Append(',');
            sb.Append(ForbidLargeMultiBuddies ? "True" : "False").Append(',');
            return sb.ToString();
        }

        /// <summary>
        /// 解析原版 SR 的设置串。缺项按默认值补齐（与原版行为一致），
        /// 多余项忽略；解析失败返回 false 并给出原因。
        /// </summary>
        public bool TryParseSettingsString(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "设置串为空。";
                return false;
            }

            List<string> parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(p => p.Trim()).ToList();
            if (parts.Count < 6)
            {
                error = $"设置串至少需要 6 项（当前 {parts.Count} 项）。请完整复制原版随机器的 Settings Preset。";
                return false;
            }

            var ci = CultureInfo.InvariantCulture;
            try
            {
                var d = new SummonSettings();   // 缺项默认值
                int i = 0;
                VariantReuseChance = int.Parse(parts[i++], ci);
                MultiBuddyInitialChance = int.Parse(parts[i++], ci);
                MultiBuddyMax = int.Parse(parts[i++], ci);
                MultiBuddyAdditionalChance = int.Parse(parts[i++], ci);
                MultiBuddyDupeChance = int.Parse(parts[i++], ci);
                BuddyReuse = bool.Parse(parts[i++]);
                if (i < parts.Count) HpMultiplier = double.Parse(parts[i++], ci);
                if (i < parts.Count) DamageMultiplier = double.Parse(parts[i++], ci);
                if (i < parts.Count) FpCostMin = int.Parse(parts[i++], ci);
                if (i < parts.Count) FpCostMax = int.Parse(parts[i++], ci);
                if (i < parts.Count) HpCostChance = int.Parse(parts[i++], ci);
                if (i < parts.Count) HpCostMin = int.Parse(parts[i++], ci);
                if (i < parts.Count) HpCostMax = int.Parse(parts[i++], ci);
                if (i < parts.Count) ForbidLargeMultiBuddies = bool.Parse(parts[i++]);
                Clamp();
                return true;
            }
            catch (Exception ex)
            {
                error = "设置串格式不正确：" + ex.Message;
                return false;
            }
        }

        /// <summary>按预设名套用设置。</summary>
        public void ApplyPreset(string key)
        {
            SummonPreset p = SummonPreset.All.FirstOrDefault(x =>
                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (p == null) return;
            TryParseSettingsString(p.SettingsString, out _);
        }

        /// <summary>找出当前设置与哪个预设完全一致（用于界面上回显预设名）；没有则返回 null。</summary>
        public string MatchPresetKey()
        {
            string cur = ToSettingsString();
            foreach (SummonPreset p in SummonPreset.All)
            {
                var probe = new SummonSettings();
                if (probe.TryParseSettingsString(p.SettingsString, out _) &&
                    string.Equals(probe.ToSettingsString(), cur, StringComparison.Ordinal))
                    return p.Key;
            }
            return null;
        }
    }

    /// <summary>一套召唤物随机化预设（与原版 Summon Randomizer 数值一致）。</summary>
    public sealed class SummonPreset
    {
        public string Key { get; }
        public string Label { get; }
        public string Description { get; }
        public string SettingsString { get; }

        public SummonPreset(string key, string label, string description, string settingsString)
        {
            Key = key;
            Label = label;
            Description = description;
            SettingsString = settingsString;
        }

        public override string ToString() => Label;

        /// <summary>六套预设，数值取自原版 Summon Randomizer v1.0.8。</summary>
        public static readonly SummonPreset[] All =
        {
            new SummonPreset("fun", "欢乐（原版默认）",
                "50% 多召、血 ×2、伤害 ×3、骨灰 FP 消耗 30-60。与原版默认完全一致。",
                "20,50,5,50,80,False,2,3,30,60,10,300,600,True,"),
            new SummonPreset("reasonable", "合理",
                "偏少的多召（最多 4 个）、血 ×1.5、伤害 ×1.25、骨灰 FP 消耗 30-100。",
                "30,20,4,60,100,False,1.5,1.25,30,100,10,300,900,True,"),
            new SummonPreset("chaos", "混沌",
                "必多召（100%）、无重复演示、血 ×3、伤害 ×2、骨灰消耗更高。",
                "0,100,5,100,0,False,3,2,60,100,50,600,1000,False,"),
            new SummonPreset("family", "家族",
                "同族变体复用 50%、多召多达 8 个、允许骨灰重复、血 ×1.5、伤害 ×2。",
                "50,100,8,80,100,False,1.5,2,30,60,10,300,600,False,"),
            new SummonPreset("playground", "游乐场",
                "必多召、血 ×10、伤害 ×3、骨灰召唤不消耗 FP/HP。纯粹的混乱。",
                "20,90,8,50,50,False,10,3,0,0,0,0,0,True,"),
            new SummonPreset("balanced", "均衡",
                "温和多召（最多 3 个）、血量与伤害保持原样（×1）。",
                "30,20,3,50,100,False,1,1,30,100,10,300,900,True,"),
        };
    }
}
