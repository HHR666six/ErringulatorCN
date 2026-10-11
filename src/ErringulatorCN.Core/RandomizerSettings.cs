using System;
using System.Collections.Generic;

namespace ErringulatorCN.Core
{
    /// <summary>错位方式：决定"使用效果"如何在同类目内部互相交换。</summary>
    public enum ErringMode
    {
        /// <summary>完全洗牌：每个条目的效果随机落到别的条目上（以撒"错误道具"式）。</summary>
        Shuffle = 0,

        /// <summary>顺移：效果整体向后平移 N 位（确定性置换，边界循环）。</summary>
        Shift = 1,

        /// <summary>倒序：效果顺序完全反转（确定性置换）。</summary>
        Reverse = 2,
    }

    /// <summary>召唤物错位的池范围。</summary>
    public enum SummonPoolScope
    {
        /// <summary>所有有效召唤条目放在一起错位（最混乱）。</summary>
        All = 0,

        /// <summary>按 Buddy ID 段位分组，只在同段位内错位（较温和）。</summary>
        SameSegment = 1,
    }

    /// <summary>随机化设置。所有开关默认开启，对应界面上的勾选项。</summary>
    public sealed class RandomizerSettings
    {
        /// <summary>输入目录：必须包含 regulation.bin。</summary>
        public string InputDir { get; set; }

        /// <summary>输出目录：为空时写回输入目录。</summary>
        public string OutputDir { get; set; }

        /// <summary>paramdef 定义目录（含 *.xml）。为空时自动探测。</summary>
        public string ParamdefDir { get; set; }

        /// <summary>随机种子；为空时自动生成。</summary>
        public string Seed { get; set; }

        /// <summary>错位方式。</summary>
        public ErringMode Mode { get; set; } = ErringMode.Shuffle;

        /// <summary>顺移模式下的位移量。</summary>
        public int ShiftAmount { get; set; } = 1;

        // ==== 效果随机化总开关 ====
        /// <summary>随机化魔法与祷告的使用效果。</summary>
        public bool RandomizeMagic { get; set; } = true;

        /// <summary>随机化战灰的使用效果。</summary>
        public bool RandomizeAshOfWar { get; set; } = true;

        /// <summary>随机化道具（消耗品/可反复使用道具）的使用效果。</summary>
        public bool RandomizeGoods { get; set; } = true;

        /// <summary>随机化装备（武器/护符）附带的效果。</summary>
        public bool RandomizeEquipment { get; set; } = true;

        /// <summary>同时随机化资源消耗（MP/耐力/使用次数等）。会让游戏更难，但混乱感更足。</summary>
        public bool RandomizeCost { get; set; } = true;

        /// <summary>
        /// NPC 对话随机：把 TalkParam 里各条对白的整套内容
        /// （台词 ID / 语音 / 动作 / 口型 / 触发事件等）互相错位，
        /// 于是 NPC 会说出别人的台词、配上别人的口型与动作。
        /// </summary>
        public bool RandomizeTalks { get; set; }

        /// <summary>
        /// 武器动作模组随机：把 EquipParamWeapon 的动画套组字段
        /// （wepmotionCategory / wepmotionOneHandId / wepmotionBothHandId）
        /// 整体互相错位，于是武器会使用别的武器的攻击动作。
        /// </summary>
        public bool RandomizeWeaponMoveset { get; set; }

        // ==== 附加开关 ====
        /// <summary>同时随机化武器所挂载的战灰（swordArtsParamId）。更混乱，风险更高。</summary>
        public bool RandomizeWeaponAshLink { get; set; }

        /// <summary>
        /// 解除战灰的武器类型限制：把所有战灰gem的 canMountWep_* 开关打开，
        /// 让任何武器都能装备任意战灰。随机化战灰后不会出现"武器没法用该战灰"的情况。
        /// </summary>
        public bool UnlockAshOfWarForAllWeapons { get; set; }

        /// <summary>
        /// 随机化召唤物（骨灰/灵体召唤出来的敌人）。
        /// 具体处理方式与参数见 <see cref="Summons"/>：
        /// 温和模式只做「敌人 + AI」成对错位；完整模式移植 Elden Ring
        /// Summon Randomizer v1.0.8 的全套算法（换敌、多召、驯服克隆、倍率等）。
        /// </summary>
        public bool RandomizeSummons { get; set; } = true;

        /// <summary>召唤物随机化的详细设置（引擎、预设、多召、倍率等）。</summary>
        public SummonSettings Summons { get; set; } = new SummonSettings();

        /// <summary>包含 NPC 专用魔法/祷告（ID ≥ 53000）。通常不建议。</summary>
        public bool IncludeNpcSpells { get; set; }

        /// <summary>输出目录已存在 regulation.bin 时先备份。</summary>
        public bool BackupExisting { get; set; } = true;

        /// <summary>
        /// 道具保守模式（默认开启，强烈建议保持）：
        /// 只把"有明确使用效果、且真的能直接使用"的道具放进错位池，
        /// 排除素材、制作笔记、地图、露滴、钥匙/内容物、未实装条目等。
        /// 关闭后几乎所有 goodsType=0 的道具都会参与（不推荐）。
        /// </summary>
        public bool GoodsConservativeMode { get; set; } = true;

        /// <summary>输出到与输入相同的目录/文件时，自动改用输入备份作为来源（避免叠加随机化）。</summary>
        public bool LoadBackup { get; set; }

        /// <summary>
        /// 空操作模式：只解密→重新打包加密，不做任何随机化。
        /// 用于验证"文件重写"本身（SoulsFormats 序列化规范化）带来的差异。
        /// </summary>
        public bool NoOp { get; set; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(InputDir))
                throw new ArgumentException("输入目录不能为空。");
            if (!System.IO.Directory.Exists(InputDir))
                throw new ArgumentException($"输入目录不存在：{InputDir}");
            if (!System.IO.File.Exists(System.IO.Path.Combine(InputDir, "regulation.bin")))
                throw new ArgumentException($"输入目录里没有 regulation.bin：{InputDir}");
        }
    }

    /// <summary>单个参数表的随机化统计。</summary>
    public sealed class ParamReport
    {
        public string ParamName { get; set; }
        public string ParamType { get; set; }

        /// <summary>中文说明（例如"魔法与祷告"）。</summary>
        public string Description { get; set; }

        public int TotalRows { get; set; }
        public int AffectedRows { get; set; }
        public int AffectedFields { get; set; }
        public List<string> SkippedFields { get; } = new List<string>();
        public List<string> AppliedFields { get; } = new List<string>();
    }

    /// <summary>随机化过程中的进度信息，供界面显示进度条。</summary>
    public sealed class RandomizeProgress
    {
        /// <summary>当前阶段说明（中文）。</summary>
        public string Message { get; set; }

        /// <summary>已完成的步骤数。</summary>
        public int Completed { get; set; }

        /// <summary>总步骤数；未知时为 0。</summary>
        public int Total { get; set; }

        /// <summary>已完成的百分比（0–100）；未知时为 -1。</summary>
        public int Percent => Total > 0 ? Math.Min(100, (int)(Completed * 100.0 / Total)) : -1;
    }

    /// <summary>一次随机化的总体结果。</summary>
    public sealed class RandomizeReport
    {
        public string Seed { get; set; }
        public ErringMode Mode { get; set; }
        public string InputPath { get; set; }
        public string OutputPath { get; set; }
        public string BackupPath { get; set; }

        /// <summary>本次随机化实际使用的来源说明（输入目录当前文件 / 备份文件）。</summary>
        public string Source { get; set; }
        public DateTime StartedAt { get; set; }
        public TimeSpan Elapsed { get; set; }
        public List<ParamReport> Params { get; } = new List<ParamReport>();
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>生成后的关键道具安全自检（灵马哨笛、望远镜、圣杯瓶等是否被误改）。</summary>
        public SafetyCheckReport Safety { get; set; }
    }
}
