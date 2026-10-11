开发期辅助工具（不随发布物分发）
====================================================

这三个小工具用于开发与排障，需要 .NET 8 SDK 才能构建（dotnet run / build）。
它们直接引用 Core 工程，分析《艾尔登法环》的 regulation.bin：

  InspectRegulation
      考察某个 regulation.bin 与 Summon Randomizer 算法所需字段/ID 的兼容性。
      用法：InspectRegulation <regulation.bin> [paramdefs 目录]

  InspectRound2
      验证「对话 / 武器动作」随机化的置换正确性：
      整组搬家命中、多重集不增不减、池外行零改动。
      用法：InspectRound2 <原始 regulation.bin> <生成 regulation.bin> [paramdefs 目录]

  DumpBuddies
      打印 given regulation.bin 的 TalkParam「节奏指纹」与说话动画行分布，
      用于判断该文件是否被旧版对话随机搬乱过。
      用法：DumpBuddies <regulation.bin> [paramdefs 目录] [对照 regulation.bin]

以上工具均可用仓库内 src/ErringulatorCN.App/paramdefs 作为 paramdefs 目录。

常规用户不需要这些工具——日常校验请使用 ErringulatorCN.Cli
（summons / safetycheck / compare / verify 等命令，见使用说明第十三节）。
