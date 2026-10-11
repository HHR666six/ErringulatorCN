using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ErringulatorCN.Core;
using Microsoft.Win32;

namespace ErringulatorCN.App
{
    public partial class MainWindow : Window
    {
        private bool _running;
        private bool _uiReady;
        private bool _suppressPresetEvent;
        private readonly StringBuilder _logBuffer = new StringBuilder();

        /// <summary>常见安装位置（注册表探测失败时的后备）。</summary>
        private static readonly string[] DefaultGameDirs =
        {
            @"D:\steam\steamapps\common\ELDEN RING\Game",
            @"C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING\Game",
            @"C:\Program Files\Steam\steamapps\common\ELDEN RING\Game",
            @"E:\steam\steamapps\common\ELDEN RING\Game",
            @"F:\steam\steamapps\common\ELDEN RING\Game",
        };

        private static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ErringulatorCN", "settings.json");

        /// <summary>程序版本（显示在标题栏与日志开头，便于确认跑的是哪一版）。</summary>
        private static readonly string AppVersion =
            "v" + (System.Reflection.Assembly.GetExecutingAssembly()
                     .GetName().Version?.ToString(3) ?? "?");

        public MainWindow()
        {
            InitializeComponent();
            Title += "  " + AppVersion;
            InitSummonPresets();
            LoadDefaults();
            LoadState();
            _uiReady = true;
            UpdateSummonUiEnabled();
            Closing += (s, e) => SaveState();
        }

        // ==================== 初始化 ====================

        private void InitSummonPresets()
        {
            foreach (SummonPreset p in SummonPreset.All)
                CmbSummonPreset.Items.Add(p);
            _suppressPresetEvent = true;
            CmbSummonPreset.SelectedIndex = 0;
            _suppressPresetEvent = false;
            ApplyPresetToUi("fun");
        }

        private void LoadDefaults()
        {
            string gameDir = FindGameDir();
            if (gameDir != null)
            {
                InputBox.Text = gameDir;
            }
            else
            {
                Log("未自动找到《艾尔登法环》游戏目录（已尝试注册表与常见路径），请手动选择包含 regulation.bin 的目录。");
            }

            OutputBox.Text = Path.Combine(AppContext.BaseDirectory, "output");

            try
            {
                string defs = EffectRandomizer.ResolveParamdefDir();
                Log($"paramdef 定义已就绪：{defs}");
            }
            catch (Exception ex)
            {
                Log("警告：" + ex.Message);
            }
        }

        /// <summary>通过注册表找 Steam，并解析 libraryfolders.vdf 里的所有库目录。</summary>
        private static string FindGameDir()
        {
            var candidates = new List<string>();
            try
            {
                using RegistryKey key =
                    Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ??
                    Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");

                string steamPath = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(steamPath) && Directory.Exists(steamPath))
                {
                    candidates.Add(Path.Combine(steamPath, "steamapps", "common", "ELDEN RING", "Game"));

                    string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (string line in File.ReadAllLines(vdf))
                        {
                            Match m = Regex.Match(line, "\"path\"\\s+\"(.+?)\"");
                            if (!m.Success) continue;
                            string lib = m.Groups[1].Value.Replace("\\\\", "\\");
                            candidates.Add(Path.Combine(lib, "steamapps", "common", "ELDEN RING", "Game"));
                        }
                    }
                }
            }
            catch { /* 注册表不可用时退回常见路径 */ }

            candidates.AddRange(DefaultGameDirs);

            foreach (string dir in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, "regulation.bin")))
                        return dir;
                }
                catch { }
            }
            return null;
        }

        // ==================== 界面交互 ====================

        private void BrowseInput(object sender, RoutedEventArgs e)
        {
            string picked = PickFolder("选择游戏目录（内含 regulation.bin）", InputBox.Text);
            if (picked != null) InputBox.Text = picked;
        }

        private void BrowseOutput(object sender, RoutedEventArgs e)
        {
            string picked = PickFolder("选择输出目录", OutputBox.Text);
            if (picked != null) OutputBox.Text = picked;
        }

        private string PickFolder(string title, string initial)
        {
            try
            {
                var dlg = new OpenFolderDialog { Title = title };
                if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                    dlg.InitialDirectory = initial;
                return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开目录选择框：" + ex.Message, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        private void OpenOutput(object sender, RoutedEventArgs e)
        {
            string dir = OutputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(dir))
                dir = InputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "输出目录还不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开目录"); }
        }

        private void RestoreBackup(object sender, RoutedEventArgs e)
        {
            string dir = OutputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(dir)) dir = InputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(dir)) return;

            string target = Path.Combine(dir, "regulation.bin");
            string bak = Path.Combine(dir, "regulation.bin.bak");
            if (!File.Exists(bak))
            {
                string legacy = Path.Combine(dir, "regulation-erringulator-backup.bin");
                if (File.Exists(legacy)) bak = legacy;
            }
            if (!File.Exists(bak))
            {
                MessageBox.Show(this, $"没有找到备份文件：\n{Path.Combine(dir, "regulation.bin.bak")}", "无法恢复",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(this, $"将用备份覆盖：\n{target}\n\n继续吗？", "恢复备份",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            try
            {
                File.Copy(bak, target, true);
                Log($"已从备份恢复：{target}");
                StatusText.Text = "已恢复备份";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.ToString(), "恢复失败");
            }
        }

        // ==================== 召唤物设置界面 ====================

        private void SummonUi_Changed(object sender, RoutedEventArgs e) => UpdateSummonUiEnabled();

        private void UpdateSummonUiEnabled()
        {
            // XAML 加载期间控件的事件会早于其它控件创建触发，此处统一跳过；
            // 加载完成后构造函数会显式调用一次。
            if (!_uiReady || PanelSummonFull == null || PanelSummonMild == null) return;

            bool enabled = ChkSummons.IsChecked == true;
            bool full = RadSummonFull.IsChecked == true;

            PanelSummonFull.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
            PanelSummonMild.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            PanelSummonFull.IsEnabled = enabled;
            PanelSummonMild.IsEnabled = enabled;

            CmbSummonPreset.IsEnabled = enabled && full;
            TxtSummonSettings.IsEnabled = enabled && full;

            bool multi = enabled && ChkSummonMulti.IsChecked == true;
            foreach (TextBox t in new[] { TxtHpMul, TxtDmgMul, TxtFpMul }) t.IsEnabled = multi;

            bool costs = enabled && full && ChkAshCosts.IsChecked == true;
            foreach (TextBox t in new[] { TxtFpCostMin, TxtFpCostMax, TxtHpCostChance, TxtHpCostMin, TxtHpCostMax })
                t.IsEnabled = costs;
        }

        private void SummonPreset_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPresetEvent) return;
            if (CmbSummonPreset.SelectedItem is SummonPreset p)
                ApplyPresetToUi(p.Key);
        }

        private void ApplyPresetToUi(string key)
        {
            SummonPreset p = null;
            foreach (SummonPreset sp in SummonPreset.All)
                if (string.Equals(sp.Key, key, StringComparison.OrdinalIgnoreCase)) { p = sp; break; }
            if (p == null) return;

            var s = new SummonSettings();
            s.ApplyPreset(key);
            SyncSummonUiFromSettings(s, updatePresetCombo: false);
            TxtPresetDesc.Text = p.Description;
        }

        private void SyncSummonUiFromSettings(SummonSettings s, bool updatePresetCombo)
        {
            TxtInitChance.Text = s.MultiBuddyInitialChance.ToString();
            TxtAddChance.Text = s.MultiBuddyAdditionalChance.ToString();
            TxtMultiMax.Text = s.MultiBuddyMax.ToString();
            TxtDupeChance.Text = s.MultiBuddyDupeChance.ToString();
            TxtVariantChance.Text = s.VariantReuseChance.ToString();
            ChkBuddyReuse.IsChecked = s.BuddyReuse;
            ChkForbidLarge.IsChecked = s.ForbidLargeMultiBuddies;
            ChkAshCosts.IsChecked = s.RandomizeAshCosts;
            TxtFpCostMin.Text = s.FpCostMin.ToString();
            TxtFpCostMax.Text = s.FpCostMax.ToString();
            TxtHpCostChance.Text = s.HpCostChance.ToString();
            TxtHpCostMin.Text = s.HpCostMin.ToString();
            TxtHpCostMax.Text = s.HpCostMax.ToString();
            ChkSummonMulti.IsChecked = s.ApplyMultipliers;
            TxtHpMul.Text = s.HpMultiplier.ToString("0.##", CultureInfo.InvariantCulture);
            TxtDmgMul.Text = s.DamageMultiplier.ToString("0.##", CultureInfo.InvariantCulture);
            TxtFpMul.Text = s.FpMultiplier.ToString("0.##", CultureInfo.InvariantCulture);
            ChkSummonRidden.IsChecked = s.RandomizeRidden;
            RadSummonAll.IsChecked = s.Pool == SummonPoolScope.All;
            RadSummonSame.IsChecked = s.Pool == SummonPoolScope.SameSegment;
            TxtSummonSettings.Text = s.ToSettingsString();

            if (updatePresetCombo)
            {
                string key = s.MatchPresetKey();
                _suppressPresetEvent = true;
                CmbSummonPreset.SelectedItem = null;
                if (key != null)
                {
                    foreach (SummonPreset p in SummonPreset.All)
                        if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                        { CmbSummonPreset.SelectedItem = p; break; }
                    TxtPresetDesc.Text = (CmbSummonPreset.SelectedItem as SummonPreset)?.Description ?? "";
                }
                else
                {
                    TxtPresetDesc.Text = "自定义（与预设不一致）";
                }
                _suppressPresetEvent = false;
            }

            UpdateSummonUiEnabled();
        }

        private SummonSettings ReadSummonSettingsFromUi()
        {
            var s = new SummonSettings
            {
                Engine = RadSummonMild.IsChecked == true ? SummonEngineMode.Mild : SummonEngineMode.Full,
                MultiBuddyInitialChance = ParseInt(TxtInitChance, 50),
                MultiBuddyAdditionalChance = ParseInt(TxtAddChance, 50),
                MultiBuddyMax = ParseInt(TxtMultiMax, 5),
                MultiBuddyDupeChance = ParseInt(TxtDupeChance, 80),
                VariantReuseChance = ParseInt(TxtVariantChance, 20),
                BuddyReuse = ChkBuddyReuse.IsChecked == true,
                ForbidLargeMultiBuddies = ChkForbidLarge.IsChecked == true,
                RandomizeAshCosts = ChkAshCosts.IsChecked == true,
                FpCostMin = ParseInt(TxtFpCostMin, 30),
                FpCostMax = ParseInt(TxtFpCostMax, 60),
                HpCostChance = ParseInt(TxtHpCostChance, 10),
                HpCostMin = ParseInt(TxtHpCostMin, 300),
                HpCostMax = ParseInt(TxtHpCostMax, 600),
                ApplyMultipliers = ChkSummonMulti.IsChecked == true,
                HpMultiplier = ParseDbl(TxtHpMul, 2.0),
                DamageMultiplier = ParseDbl(TxtDmgMul, 3.0),
                FpMultiplier = ParseDbl(TxtFpMul, 1.0),
                Pool = RadSummonSame.IsChecked == true ? SummonPoolScope.SameSegment : SummonPoolScope.All,
                RandomizeRidden = ChkSummonRidden.IsChecked == true,
            };
            s.Clamp();
            return s;
        }

        private static int ParseInt(TextBox box, int fallback) =>
            int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static double ParseDbl(TextBox box, double fallback) =>
            double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

        /// <summary>
        /// 运行前校验数字输入框：
        ///   非数字 → 报错阻止运行（并聚焦到第一个出错的输入框），不再静默用默认值；
        ///   超出允许范围 → 记录提示（实际按范围钳制处理，与原版随机器的控件上下限一致）。
        /// </summary>
        private (List<(TextBox Box, string Msg)> Errors, List<string> Notices) ValidateInputs()
        {
            var errors = new List<(TextBox, string)>();
            var notices = new List<string>();

            void CheckI(TextBox box, string label, int min, int max)
            {
                string raw = box.Text?.Trim() ?? "";
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                {
                    errors.Add((box, $"{label}：「{raw}」不是有效整数"));
                    return;
                }
                if (v < min || v > max)
                    notices.Add($"{label} {v} 超出范围，将按 {(v < min ? min : max)} 处理（允许 {min}~{max}）");
            }
            void CheckD(TextBox box, string label, double min, double max)
            {
                string raw = box.Text?.Trim() ?? "";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    errors.Add((box, $"{label}：「{raw}」不是有效数字"));
                    return;
                }
                if (v < min || v > max)
                    notices.Add($"{label} {v:0.##} 超出范围，将按 {(v < min ? min : max):0.##} 处理（允许 {min:0.##}~{max:0.##}）");
            }

            if (RadShift.IsChecked == true) CheckI(ShiftBox, "顺移位数", -99999, 99999);

            if (ChkSummons.IsChecked == true)
            {
                if (RadSummonFull.IsChecked == true)
                {
                    CheckI(TxtInitChance, "初始多召几率 %", 0, 100);
                    CheckI(TxtAddChance, "追加多召几率 %", 0, 100);
                    CheckI(TxtMultiMax, "多召上限", 1, 9);
                    CheckI(TxtDupeChance, "多召重复几率 %", 0, 100);
                    CheckI(TxtVariantChance, "变体复用几率 %", 0, 100);
                    if (ChkAshCosts.IsChecked == true)
                    {
                        CheckI(TxtFpCostMin, "骨灰 FP 消耗下限", 0, 999);
                        CheckI(TxtFpCostMax, "骨灰 FP 消耗上限", 0, 999);
                        CheckI(TxtHpCostChance, "改用 HP 几率 %", 0, 100);
                        CheckI(TxtHpCostMin, "骨灰 HP 消耗下限", 0, 9999);
                        CheckI(TxtHpCostMax, "骨灰 HP 消耗上限", 0, 9999);
                    }
                }
                if (ChkSummonMulti.IsChecked == true)
                {
                    CheckD(TxtHpMul, "血量倍率", 0.01, 10);
                    CheckD(TxtDmgMul, "伤害倍率", 0.01, 10);
                    CheckD(TxtFpMul, "FP 倍率", 0.01, 10);
                }
            }
            return (errors, notices);
        }

        private void ExportSummonSettings(object sender, RoutedEventArgs e)
        {
            SummonSettings s = ReadSummonSettingsFromUi();
            TxtSummonSettings.Text = s.ToSettingsString();
            Log("已把当前召唤物设置导出到「设置串」文本框（格式与原版 Summon Randomizer 互通），可复制保存或分享。");
        }

        private void ApplySummonSettingsText(object sender, RoutedEventArgs e)
        {
            var s = new SummonSettings();
            if (!s.TryParseSettingsString(TxtSummonSettings.Text, out string err))
            {
                MessageBox.Show(this, err, "设置串无法解析", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SyncSummonUiFromSettings(s, updatePresetCombo: true);
            Log("已应用设置串：" + s.ToSettingsString());
        }

        // ==================== 设置持久化 ====================

        private sealed class UiState
        {
            public string InputDir { get; set; }
            public string OutputDir { get; set; }
            public string Seed { get; set; }
            public int Mode { get; set; }
            public int ShiftAmount { get; set; } = 1;
            public bool RandomizeMagic { get; set; } = true;
            public bool RandomizeAshOfWar { get; set; } = true;
            public bool RandomizeGoods { get; set; } = true;
            public bool RandomizeEquipment { get; set; } = true;
            public bool RandomizeCost { get; set; } = true;
            public bool GoodsConservativeMode { get; set; } = true;
            public bool UnlockAsh { get; set; } = true;
            public bool RandomizeSummons { get; set; } = true;
            public bool WeaponAsh { get; set; }
            public bool NpcSpells { get; set; }
            public bool Talks { get; set; }
            public bool WeaponMotion { get; set; }
            public SummonSettings Summons { get; set; }
        }

        private void SaveState()
        {
            try
            {
                var st = new UiState
                {
                    InputDir = InputBox.Text?.Trim(),
                    OutputDir = OutputBox.Text?.Trim(),
                    Seed = SeedBox.Text?.Trim(),
                    Mode = RadShift.IsChecked == true ? 1 : RadReverse.IsChecked == true ? 2 : 0,
                    ShiftAmount = ParseInt(ShiftBox, 1),
                    RandomizeMagic = ChkMagic.IsChecked == true,
                    RandomizeAshOfWar = ChkAsh.IsChecked == true,
                    RandomizeGoods = ChkGoods.IsChecked == true,
                    RandomizeEquipment = ChkEquip.IsChecked == true,
                    RandomizeCost = ChkCost.IsChecked == true,
                    GoodsConservativeMode = ChkGoodsSafe.IsChecked == true,
                    UnlockAsh = ChkUnlockAsh.IsChecked == true,
                    RandomizeSummons = ChkSummons.IsChecked == true,
                    WeaponAsh = ChkWeaponAsh.IsChecked == true,
                    NpcSpells = ChkNpcSpells.IsChecked == true,
                    Talks = ChkTalks.IsChecked == true,
                    WeaponMotion = ChkWeaponMotion.IsChecked == true,
                    Summons = ReadSummonSettingsFromUi(),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 持久化失败不影响使用 */ }
        }

        private void LoadState()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                UiState st = JsonSerializer.Deserialize<UiState>(File.ReadAllText(StatePath));
                if (st == null) return;

                if (!string.IsNullOrWhiteSpace(st.InputDir) && Directory.Exists(st.InputDir)) InputBox.Text = st.InputDir;
                if (!string.IsNullOrWhiteSpace(st.OutputDir)) OutputBox.Text = st.OutputDir;
                if (!string.IsNullOrWhiteSpace(st.Seed)) SeedBox.Text = st.Seed;

                RadShift.IsChecked = st.Mode == 1;
                RadReverse.IsChecked = st.Mode == 2;
                RadShuffle.IsChecked = st.Mode == 0;
                ShiftBox.Text = st.ShiftAmount.ToString();
                ChkMagic.IsChecked = st.RandomizeMagic;
                ChkAsh.IsChecked = st.RandomizeAshOfWar;
                ChkGoods.IsChecked = st.RandomizeGoods;
                ChkEquip.IsChecked = st.RandomizeEquipment;
                ChkCost.IsChecked = st.RandomizeCost;
                ChkGoodsSafe.IsChecked = st.GoodsConservativeMode;
                ChkUnlockAsh.IsChecked = st.UnlockAsh;
                ChkSummons.IsChecked = st.RandomizeSummons;
                ChkWeaponAsh.IsChecked = st.WeaponAsh;
                ChkNpcSpells.IsChecked = st.NpcSpells;
                ChkTalks.IsChecked = st.Talks;
                ChkWeaponMotion.IsChecked = st.WeaponMotion;
                if (st.Summons != null)
                {
                    st.Summons.Clamp();
                    RadSummonFull.IsChecked = st.Summons.Engine == SummonEngineMode.Full;
                    RadSummonMild.IsChecked = st.Summons.Engine == SummonEngineMode.Mild;
                    SyncSummonUiFromSettings(st.Summons, updatePresetCombo: true);
                }
            }
            catch { /* 配置损坏时用默认值 */ }
        }

        // ==================== 执行随机化 ====================

        private async void RunRandomize(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            (List<(TextBox Box, string Msg)> inputErrors, List<string> inputNotices) = ValidateInputs();
            if (inputErrors.Count > 0)
            {
                MessageBox.Show(this,
                    "以下输入有问题，请修正后再开始：\n\n" +
                    string.Join("\n", inputErrors.Select(x => "· " + x.Msg)),
                    "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);
                inputErrors[0].Box?.Focus();
                return;
            }

            SummonSettings summonSettings = ReadSummonSettingsFromUi();

            var settings = new RandomizerSettings
            {
                InputDir = InputBox.Text?.Trim(),
                OutputDir = OutputBox.Text?.Trim(),
                Seed = SeedBox.Text?.Trim(),
                RandomizeMagic = ChkMagic.IsChecked == true,
                RandomizeAshOfWar = ChkAsh.IsChecked == true,
                RandomizeGoods = ChkGoods.IsChecked == true,
                RandomizeEquipment = ChkEquip.IsChecked == true,
                RandomizeCost = ChkCost.IsChecked == true,
                GoodsConservativeMode = ChkGoodsSafe.IsChecked == true,
                UnlockAshOfWarForAllWeapons = ChkUnlockAsh.IsChecked == true,
                RandomizeSummons = ChkSummons.IsChecked == true,
                Summons = summonSettings,
                RandomizeWeaponAshLink = ChkWeaponAsh.IsChecked == true,
                RandomizeTalks = ChkTalks.IsChecked == true,
                RandomizeWeaponMoveset = ChkWeaponMotion.IsChecked == true,
                IncludeNpcSpells = ChkNpcSpells.IsChecked == true,
                Mode = RadShift.IsChecked == true ? ErringMode.Shift
                     : RadReverse.IsChecked == true ? ErringMode.Reverse
                     : ErringMode.Shuffle,
                ShiftAmount = ParseInt(ShiftBox, 1),
            };

            if (!settings.RandomizeMagic && !settings.RandomizeAshOfWar &&
                !settings.RandomizeGoods && !settings.RandomizeEquipment &&
                !settings.RandomizeWeaponAshLink && !settings.RandomizeSummons &&
                !settings.RandomizeTalks && !settings.RandomizeWeaponMoveset &&
                !settings.UnlockAshOfWarForAllWeapons)
            {
                MessageBox.Show(this, "请至少勾选一项要随机化的内容。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                settings.Validate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "目录设置有误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveState();

            _running = true;
            BtnRun.IsEnabled = false;
            BtnRestore.IsEnabled = false;
            StatusText.Text = "正在随机化……";
            LogBox.Clear();
            _logBuffer.Clear();
            Progress.Value = 0;
            Log($"========== 开始随机化（{AppVersion}）==========");
            foreach (string notice in inputNotices) Log("提示：" + notice);

            var sw = Stopwatch.StartNew();
            RandomizeReport report = null;
            Exception failure = null;

            await Task.Run(() =>
            {
                try
                {
                    var randomizer = new EffectRandomizer(
                        settings,
                        msg => Dispatcher.Invoke(() => Log(msg)),
                        p => Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = p.Message;
                            if (p.Percent >= 0) Progress.Value = p.Percent;
                        }));
                    report = randomizer.Run();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            sw.Stop();
            _running = false;
            BtnRun.IsEnabled = true;
            BtnRestore.IsEnabled = true;
            Progress.Value = failure == null ? 100 : 0;

            if (failure != null)
            {
                Log("失败：" + failure.Message);
                StatusText.Text = "随机化失败";
                WriteLogFile(null);
                MessageBox.Show(this, failure.ToString(), "随机化失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SeedBox.Text = report.Seed;
            Log("");
            Log("========== 结果汇总 ==========");
            Log($"随机种子：{report.Seed}");
            Log($"错位方式：{DescribeMode(report.Mode, settings.ShiftAmount)}");
            Log($"随机化来源：{report.Source}");
            Log($"实际读取：{report.InputPath}");
            Log($"输出文件：{report.OutputPath}");
            if (!string.IsNullOrEmpty(report.BackupPath))
                Log($"原文件备份：{report.BackupPath}");
            Log("");
            Log("被随机化的参数表：");
            foreach (ParamReport pr in report.Params)
            {
                Log($"  · {pr.Description}（{pr.ParamName}）：{pr.AffectedRows} 行 × {pr.AffectedFields} 个字段" +
                    (pr.SkippedFields.Count > 0 ? $"，跳过 {pr.SkippedFields.Count} 个不存在的字段" : ""));
            }

            if (report.Warnings.Count > 0)
            {
                Log("");
                Log("警告：");
                foreach (string w in report.Warnings) Log("  ! " + w);
            }

            // 关键道具保护自检（让"灵马哨笛/望远镜是否被随机"一眼可见）
            string safetyLine = "";
            if (report.Safety != null)
            {
                Log("");
                Log("---------- 关键道具保护自检 ----------");
                if (report.Safety.Errors.Count > 0)
                {
                    safetyLine = "自检未能完成";
                    foreach (string err in report.Safety.Errors) Log("  ? " + err);
                }
                else
                {
                    foreach (SafetyCheckItem it in report.Safety.Items)
                    {
                        Log(report.Safety.Passed
                            ? $"  ✓ {it.Name}（ID {it.ID}）：未被改动"
                            : $"  {(it.Changed ? "✗" : "✓")} {it.Name}（ID {it.ID}）：{(it.Changed ? $"被改动 {it.ChangedFields} 个字段" : "未被改动")}");
                    }
                    safetyLine = report.Safety.Passed
                        ? $"{report.Safety.TotalChecked} 项受保护道具全部未被改动"
                        : $"异常：{report.Safety.ChangedCount} 项被改动";
                }
            }

            Log("");
            Log($"完成，总耗时 {sw.Elapsed.TotalSeconds:F1} 秒。请重启游戏后再进入（修改不会立即生效）。");
            StatusText.Text = $"完成（用时 {sw.Elapsed.TotalSeconds:F0} 秒）";

            string logPath = WriteLogFile(report);
            if (logPath != null)
                Log($"本次日志已保存：{logPath}");

            MessageBox.Show(this,
                $"随机化完成！\n\n种子：{report.Seed}\n输出：{report.OutputPath}\n" +
                (string.IsNullOrEmpty(safetyLine) ? "" : $"\n关键道具自检：{safetyLine}\n") +
                "\n注意：regulation.bin 的改动需要重启游戏才会生效。\n" +
                "请勿在联机模式下使用本工具的产物。",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string DescribeMode(ErringMode mode, int shift) => mode switch
        {
            ErringMode.Shift => $"顺移 {shift} 位",
            ErringMode.Reverse => "倒序",
            _ => "完全洗牌",
        };

        private void Log(string message)
        {
            _logBuffer.AppendLine(message);
            LogBox.AppendText(message + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        /// <summary>把本次运行日志写到输出目录（排障与复现用）。</summary>
        private string WriteLogFile(RandomizeReport report)
        {
            try
            {
                string dir = OutputBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
                string path = Path.Combine(dir,
                    $"ErringulatorCN_log_{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                var sb = new StringBuilder();
                sb.AppendLine("ErringulatorCN 日志");
                sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                if (report != null)
                {
                    sb.AppendLine("种子：" + report.Seed);
                    sb.AppendLine("输入：" + report.InputPath);
                    sb.AppendLine("输出：" + report.OutputPath);
                }
                sb.AppendLine(new string('-', 60));
                sb.Append(_logBuffer);
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                return path;
            }
            catch { return null; }
        }
    }
}
