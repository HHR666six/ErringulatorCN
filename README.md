# ErringulatorCN — 艾尔登法环效果随机化器（中文版）

为《艾尔登法环》**1.16.x**（含《黄金树之影》DLC）制作的效果错位随机化工具，
简体中文界面。它以「以撒的结合 · 错误道具」式错位为核心，并完整移植了
Elden Ring Summon Randomizer v1.0.8 的召唤物随机化。

> 当前版本 **1.2.7**。程序标题栏与运行日志会显示版本号。

## 下载（开箱即用）

不想自己构建？到 **[Releases](https://github.com/HHR666six/ErringulatorCN/releases)** 下载最新
`ErringulatorCN-x.y.z.zip`（约 88 MB，含主程序 / 命令行工具 / 参数表定义 / 使用说明）：

1. 解压到任意目录（**不要**在压缩包里直接运行）
2. 双击 `ErringulatorCN.exe`（自包含单文件，无需安装 .NET 运行时）
3. 选择游戏目录（Steam 安装自动探测）→ 勾选随机化内容 → 开始 → 重启游戏

> 修改 `regulation.bin` 前请先备份；修改后的文件严禁联机使用。

## 功能一览

### 效果错位随机化
把同一类目里各条目的「效果字段」成套互相错位——名称与图标不变，
实际效果变成别人的：

* **魔法 / 祷告**：交换 behaviorId、spEffectCategory、refId1~10 等效果链
* **战灰**：交换动作类型与速度类型；可选**解除武器类型限制**
  （等价于 Unlocked Ashes of War，实测 canMountWep 全部置 1）
* **道具**：交换使用效果（保守筛选，圣杯瓶/灵马哨笛/望远镜等受保护；
  **追忆类道具**——接肢的追忆、死龙的追忆等全部 25 件——同样参与随机）
* **护符 / 武器**：交换附带的特殊效果 ID
* **NPC 对话**：说别人的台词（台词与语音成套搬移）
* **武器动作模组**：用别的武器的攻击动画（wepmotionCategory 成套错位）

### 召唤物随机化（完整移植 Summon Randomizer v1.0.8）
骨灰不再召唤固定敌人，而是按设置随机召唤：

* 随机换敌 + **多召**（一次召出多个，几率/上限可调）
* **驯服克隆**：敌人生成为友方（teamType=47、无掉落、合法碰撞体）
* **仿身泪滴特例**（多召出玩家分身）、骑乘形态清理
* **骨灰召唤消耗重掷**（FP / HP 范围）
* **强化倍率**：血量/伤害以加法落在 SpEffect 290000-290010
  （与骨灰强化等级叠加）
* **六套预设**（欢乐/合理/混沌/家族/游乐场/均衡，与原版数值一致）
* **设置串互通**：可直接粘贴原版 Summon Randomizer 的 Settings Preset

### 工程与易用性
* 关键道具保护自检（每次生成后逐字段核对 9 项受保护道具）
* 对话数据体检（识别曾被旧版搬乱的输入并给出提醒）
* Steam 安装目录自动探测（含多库）、设置自动记忆、运行日志落盘
* 命令行诊断工具（校验 / 对比 / 体检）

## 使用

详细说明见 **[使用说明.txt](使用说明.txt)**。要点：

1. 游戏版本 1.16.x；本工具只修改 `regulation.bin`，不碰存档与游戏本体
2. 选择游戏目录与输出目录（建议先用 Mod Engine 2 加载，或就地覆盖并保留备份）
3. 勾选要随机化的内容 → 开始随机化（约 20 秒）→ 重启游戏
4. **相同种子 + 相同设置 + 相同原始文件 = 完全相同的结果**（已逐表验证）

与其它随机化 mod 的配合顺序（如 Elden Ring Randomizer，Nexus 428）
见使用说明第十四节。

## 构建

需要 **.NET 8 SDK**（Windows）。

```bat
:: 图形界面（自包含单文件）
cd src\ErringulatorCN.App
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -o ..\..\publish

:: 命令行工具
cd src\ErringulatorCN.Cli
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ..\..\publish_cli
```

参数表定义（`paramdefs`，194 个 XML）随 App 工程自动复制到输出目录。
发布物需与 `paramdefs` 放在同一层。

## 目录结构

```
使用说明.txt              详细中文说明书（含原理、FAQ、与其它 mod 配合）
src/
  ErringulatorCN.Core/    核心：效果错位 + 召唤物引擎 + 内嵌 SoulsFormats
  ErringulatorCN.App/     WPF 图形界面（含 paramdefs）
  ErringulatorCN.Cli/     命令行诊断工具
  tools/                  开发期辅助工具（数据分析 / 校验）
  README-源码说明.txt      源码结构、构建与实现备注
```

## 稳定性与验证

仓库内附带自动化回归脚本 `src/_test/stability.ps1`（22 个场景：
重复运行、就地覆盖、压力与边界、对话/动作全量、确定性、NoOp 契约、
坏输入处理）。1.2.7 全量通过，细节见使用说明第十节。

## 依赖与致谢

* [SoulsFormats](https://github.com/JKAnderson/SoulsFormats) —— JKAnderson（内嵌源码，MIT）
* [Erringulator](https://github.com/JKAnderson/Erringulator) —— JKAnderson（思路与效果字段设计）
* Paramdomizer —— Mechapope
* [Elden Ring Summon Randomizer](https://www.nexusmods.com/eldenring/mods/428) —— thefifthmatt 的 428 之外，
  召唤物逻辑来自 King Bore Haha / Geeeeeorge 的 Summon Randomizer（本工具按其 v1.0.8 适配 1.16）
* Paramdex / Smithbox 社区 —— 参数表定义（paramdefs）

## 免责声明

* 修改后的 `regulation.bin` **严禁联机使用**，存在封号风险；建议离线游玩、
  搭配 Mod Engine 2 与独立存档。
* 随机化可能导致崩溃、数值异常等状况，属正常现象；请保留原始文件备份。
* 本项目仅供学习与个人娱乐使用，不包含任何游戏本体数据。

## License

[MIT](LICENSE)
