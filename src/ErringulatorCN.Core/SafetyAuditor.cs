using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SoulsFormats;

namespace ErringulatorCN.Core
{
    /// <summary>单个受保护物品的自检结果。</summary>
    public sealed class SafetyCheckItem
    {
        public int ID { get; set; }
        public string Name { get; set; }
        public bool Changed { get; set; }
        public int ChangedFields { get; set; }
        public List<string> Details { get; } = new List<string>();
    }

    /// <summary>生成后自检报告。</summary>
    public sealed class SafetyCheckReport
    {
        public int TotalChecked { get; set; }
        public int ChangedCount { get; set; }
        public List<SafetyCheckItem> Items { get; } = new List<SafetyCheckItem>();
        public List<string> Errors { get; } = new List<string>();
        public bool Passed => Errors.Count == 0 && ChangedCount == 0;
    }

    /// <summary>
    /// 生成后安全自检：把生成文件与原始文件逐字段对比，
    /// 确认灵马哨笛、望远镜、携带照明灯、圣杯瓶等关键道具没有被改动。
    /// </summary>
    public static class SafetyAuditor
    {
        /// <summary>必须保持零改动的关键道具（ID → 中文名）。</summary>
        public static readonly (int ID, string Name)[] ProtectedGoods =
        {
            (130,  "灵马哨笛"),
            (181,  "灵马哨笛(变体)"),
            (2040, "望远镜"),
            (2070, "携带照明灯"),
            (250,  "灵药圣杯瓶"),
            (251,  "灵药圣杯瓶(变体)"),
            (1000, "红露滴圣杯瓶+0"),
            (1050, "青露滴圣杯瓶+0"),
            (115,  "记忆的恩惠"),
        };

        private static readonly string[] CheckFields =
        {
            "refId_default", "behaviorId", "spEffectCategory", "goodsUseAnim", "refCategory",
            "refId_1", "refVirtualWepId", "sfxVariationId", "castSfxId", "fireSfxId", "effectSfxId",
            "consumeMP", "consumeHP", "useEnableSpEffectType",
        };

        /// <summary>
        /// 对比两份 regulation.bin 的 EquipParamGoods，检查受保护物品是否被动过。
        /// <paramref name="preloadedParamdefs"/> 可传入已载入的定义列表，
        /// 避免每次自检重复反序列化 194 个 XML（随机化主流程会传入）。
        /// </summary>
        public static SafetyCheckReport Check(string originalPath, string generatedPath,
            string paramdefDir = null, List<PARAMDEF> preloadedParamdefs = null)
        {
            var report = new SafetyCheckReport();

            List<PARAMDEF> paramdefs = preloadedParamdefs;
            if (paramdefs == null)
            {
                try
                {
                    string dir = EffectRandomizer.ResolveParamdefDir(paramdefDir);
                    paramdefs = Directory.GetFiles(dir, "*.xml")
                        .Select(p => { try { return PARAMDEF.XmlDeserialize(p); } catch { return null; } })
                        .Where(d => d != null).ToList();
                }
                catch (Exception ex)
                {
                    report.Errors.Add("载入 paramdef 失败：" + ex.Message);
                    return report;
                }
            }

            Dictionary<int, PARAM.Row> Load(string path)
            {
                BND4 bnd = SFUtil.DecryptERRegulation(path);
                BinderFile f = bnd.Files.FirstOrDefault(x =>
                    Path.GetFileNameWithoutExtension(x.Name).Equals("EquipParamGoods", StringComparison.OrdinalIgnoreCase));
                if (f == null) throw new InvalidOperationException("找不到 EquipParamGoods 参数表。");
                PARAM p = PARAM.Read(f.Bytes);
                if (!p.ApplyParamdefCarefully(paramdefs))
                    throw new InvalidOperationException("EquipParamGoods 的 paramdef 不匹配。");
                return p.Rows.ToDictionary(r => r.ID);
            }

            Dictionary<int, PARAM.Row> rowsA, rowsB;
            try
            {
                rowsA = Load(originalPath);
                rowsB = Load(generatedPath);
            }
            catch (Exception ex)
            {
                report.Errors.Add("读取文件失败：" + ex.Message);
                return report;
            }

            foreach ((int id, string name) in ProtectedGoods)
            {
                var item = new SafetyCheckItem { ID = id, Name = name };
                report.Items.Add(item);
                report.TotalChecked++;

                if (!rowsA.TryGetValue(id, out PARAM.Row ra) || !rowsB.TryGetValue(id, out PARAM.Row rb))
                {
                    item.Details.Add("缺少该行，无法核对");
                    continue;
                }

                foreach (string k in CheckFields)
                {
                    object va, vb;
                    try { va = ra[k].Value; } catch { continue; }
                    try { vb = rb[k].Value; } catch { continue; }
                    if (!Equals(va, vb))
                    {
                        item.ChangedFields++;
                        if (item.Details.Count < 4) item.Details.Add($"{k}: {va} → {vb}");
                    }
                }

                item.Changed = item.ChangedFields > 0;
                if (item.Changed) report.ChangedCount++;
            }

            return report;
        }
    }
}
