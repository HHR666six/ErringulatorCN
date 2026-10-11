ErringulatorCN 源码工程说明（1.2.2）
====================================================
1.2.0 新增：NPC 对话随机（TalkParam 内容组错位）、武器动作模组随机
（wepmotionCategory / 单手 / 双手变体整体错位），均通过 EffectRandomizer
的 TargetSpec 机制实现，见 EffectRandomizer.cs 的 TalkContentGroup 与
WeaponMotionGroup。

1.2.1 稳定性修复：所有附加处理（召唤物/战灰解锁/武器战灰挂载）补上
NoOp 模式门控；顺移位数输入框恢复可编辑；RandomizeReport.Steps 死属性移除。
稳定性测试脚本：src\_test\stability.ps1（11 个场景，可复跑）。

1.2.2 易用性：MainWindow 增加运行前数字校验（ValidateInputs，
非数字阻止运行并聚焦、越界给出日志提示）；App.xaml.cs 增加单实例
Mutex 防护（防止两个实例写坏同一输出目录）。

1.2.3 对话修复：TalkContentGroup 收窄为 4 个内容字段
（msgId/voiceId/msgId_female/voiceId_female）。此前把 talkAnimationId、
lipSync*、timeout 等节奏字段一起搬走，会让对话以别人的手势动画为
结束条件而飞速跳过（同类工具的已知问题）。数据依据：1.16 全表中
timeout/motion/reaction/event/spEffect 恒为 -1，lipSync* 基本恒定，
仅 talkAnimationId（264 行有值）有实质变化。验证：改动只落在 4 个
内容字段，节奏字段 0 改动；InspectRound2 工具已加入节奏字段哨兵检查。

1.2.4 对话池收窄：RowFilter 增加四项约束——voiceId==msgId（排除
共享/静音语音行，如 voiceId=99900001/700000010 的 331 行）、
msgId_female>0 且 voiceId_female==msgId_female、talkAnimationId==-1、
isForceDisp==0。这三类行（合计约 590 行）内容保持原版，池 10308→9717。
用户反馈 1.2.3 后仍有个别 NPC 飞速掠过即由这些行被搬动造成
（静音语音"瞬间放完"→对话立即跳过；动作时长决定对话时长）。
GUI 标题栏与运行日志自 1.2.4 起显示版本号。

1.2.5 对话数据自检：CheckTalkRhythmHealth 在启用对话随机时计算输入
文件 TalkParam 的"节奏整表指纹"（11 个节奏字段全表哈希），与常量
VanillaTalkRhythmFingerprint（1.16 原版=098347F2F6AB513B）比对；
不一致即警告"需先恢复备份"——解决了"用户在旧版损坏产物上反复重跑、
修复始终不生效"的排查困境（实测用户游戏文件指纹曾被旧版搬乱为
433626A1…，而其 .bak 与原版一致）。诊断用工具在
tools\DumpBuddies（打印两种指纹与 90200 动画行分布）。

1.2.6 对话随机回退为初始版（按用户要求）：TalkContentGroup 恢复为
15 字段全量、RowFilter 恢复为 msgId>0（所有有台词的行，含任务型/
脚本对话如白面具最后一段对白）。原因：1.2.4/1.2.5 的排除规则把
任务型对话也筛掉了，用户要求恢复完整覆盖并接受"飞速掠过"的固有
现象（1.2.3~1.2.5 的收窄与池排除逻辑已全部撤销；1.2.5 的对话数据
自检予以保留，用于识别被旧版搬乱过的输入）。节奏安全版仍可用
backup_1.1.0\ErringulatorCN_1.2.5.exe。

1.2.10 三项修复与增强：
  · 道具保守筛选去掉 anim==0 排除：改为「有真实效果即纳入，254/255
    占位动作仍排除」。新纳入 19 件使用动作字段为 0 的食用消耗品
    （苔药 900-960、干肝 1150-1180、生肉丸 1235、DLC 药膳 2001110-
    2001210），道具池 259→278；无动作且无效应的 211 行仍被排除。
  · GoodsBlacklist 删除 1026-1049（1.16 中不存在的号段），与原版
    Erringulator 的 GeneratorData.json → BlacklistGoods 逐项一致
    （208 项）；核对脚本：src\_test\compare_blacklists.ps1（或按
    _test 中同名逻辑用 bash 复核）。
  · 新增 ProcessAccessoryEffects：护符真实能力加成由游戏按 ID 约定
    查找 SpEffect（310000 + (accID-1000)*10；1.16 实测 157 个护符中
    116 个存在该约定行）。仅搬 EquipParamAccessory 的 refId 等字段
    游戏内不生效（原版 Erringulator 同样如此）；现把这 116 个约定
    效果行的整行内容按预设模式互相错位，护符游戏内效果真正改变。
    验证（bash 脚本 src\_test\verify_acc_full.sh）：442 字段全内容
    多重集守恒、115/116 行内容换位、改动严格限定在目标行内。

1.2.9 与 Elden Ring Randomizer（thefifthmatt）兼容修复：
  · 内嵌 SoulsFormats 的 DCX.DecompressDCXZSTD 原本断言 ZSTD 版本字节
    必须为 21(0x15)（游戏原版/SoulsFormats 写入值）；428 随机化器输出
    使用 15(0x0F)（其写入侧压缩级别），导致读取失败（"Read Byte: 0xF |
    Expected: 0x15"）。现改为 AssertByte(15, 21) 两者均接受；写入侧
    维持 21（游戏标准）。实测：428 输出 194 表读写正常、敌人池
    6200→6664、安全自检 9/9。
  · CheckTalkRhythmHealth 指纹由"11 个节奏字段全表哈希"收窄为
    "说话动作配对（id↔talkAnimationId）哈希"（原版常量
    B729174AC3AB7BBA）：旧版损坏必然改变该指纹，而 428 对 TalkParam
    的 5 行 lipSyncStart 合法修改不再触发误报；警告措辞同步软化
    （说明其它工具的正常修改可忽略）。

1.2.8 追忆类道具参与随机：GoodsUsableTypes 增加 goodsType=3
（全表恰为 25 件追忆：本体 2950-2964 + DLC 2002900-2010），
IsGoodsAnyType0（非保守模式）同步放行 type 3。道具池 234→259 行。
追忆的兑换逻辑按物品 ID 判定、不受影响；使用字段随池错位。

1.2.7 全面验证与内部优化：
  · 三个工程零警告构建（Core NoWarn 抑制第三方 SYSLIB0021 与有意
    使用的 CA2255；App/CLI 本就零警告）；
  · SafetyAuditor.Check 增加 preloadedParamdefs 重载，主流程传入已
    载入的定义列表，省去每次运行重复反序列化 194 个 XML；
  · 清理 ProcessParam 的死参数 incremental；
  · 稳定性脚本扩展为 3 部分共 22 个场景（src\_test\stability.ps1，
    纯 ASCII、PS 5.1 兼容），全部通过（详见使用说明第十节）。

一、目录结构
----------------------------------------------------
  src\ErringulatorCN.Core        核心库：效果错位随机化 + 召唤物随机化引擎
      EffectRandomizer.cs        效果错位主流程（魔法/战灰/道具/护符/武器/战灰解锁）
      SummonRandomizerEngine.cs  召唤物完整随机化（移植 Summon Randomizer v1.0.8）
      SummonSettings.cs          召唤物设置 + 六套预设 + 原版设置串互通
      RandomizerSettings.cs      全部设置/报告 DTO
      Permute.cs                 置换算法（洗牌/顺移/倒序）与种子
      SafetyAuditor.cs           关键道具保护自检
      SoulsFormats\              内嵌的 SoulsFormats 源码（含 ZSTD/ZstdSharp.Port 支持）

  src\ErringulatorCN.App         WPF 图形界面（发布名 ErringulatorCN.exe）
  src\ErringulatorCN.Cli         命令行诊断工具（发布名 ErringulatorCN.Cli.exe）
  tools\                         开发期辅助工具（分析 regulation.bin / 校验召唤物数据）

二、构建方法
----------------------------------------------------
  图形界面（自包含单文件）：
    cd src\ErringulatorCN.App
    dotnet publish -c Release -r win-x64 --self-contained true ^
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
      -p:IncludeNativeLibrariesForSelfExtract=true -o ..\..\_publish

  命令行工具：
    cd src\ErringulatorCN.Cli
    dotnet publish -c Release -r win-x64 --self-contained true ^
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ..\..\_publish_cli

  运行需要 .NET 8 SDK。paramdefs（194 个 xml）会随 App 工程自动复制到输出目录。

三、关键实现备注
----------------------------------------------------
  * regulation.bin 为 DCX_ZSTD 压缩 + AES-CBC 加密（随机 IV 存于文件头 16 字节），
    因此相同内容的两次输出整体哈希不同，属正常现象（解密后逐表一致）。
  * PARAM 行按列表顺序写出，插入克隆行后需按 ID 排序再写（引擎已处理）。
  * 召唤物随机化不需要原版工具的 Resources\EnemyInfoResource.txt——
    v1.0.8 里那份文件是死代码；敌人池在运行时从 NpcParam + NpcThinkParam 构建。
  * 克隆行名字统一以 "buddyRando " 开头，作为"上一轮生成物"的清理标记
    （与原版工具兼容），重新运行时自动清除后重建。
  * 召唤物使用独立随机序列（种子 + "|summon" 派生），
    因此召唤结果不受其它随机化开关影响，保证可复现。

四、命令行诊断常用命令
----------------------------------------------------
  ErringulatorCN.Cli.exe randomize --input <目录> --output <目录> --seed <种子> [选项…]
  ErringulatorCN.Cli.exe summons  --input <原始> --output <生成>   （召唤物专项校验）
  ErringulatorCN.Cli.exe safetycheck --input <原始> --output <生成>（关键道具自检）
  ErringulatorCN.Cli.exe compare --input <A> --output <B>        （逐表差异）
  ErringulatorCN.Cli.exe verify  --input <原始> --output <生成>  （改动范围汇总）
