# CritAudio M1a 离线诊断内核

当前仅对合成匿名事件进行离线诊断，实现值模型、候选过滤、施放身份比较和内存诊断环。
**不是可安装插件**。无游戏适配、Hook、监听、音频、界面、职业目录、施放去重或自动启用入口。
真实客户端验证和游戏内验收未完成，游戏在另一台电脑。构建通过不代表国服验收，完整 M1 PoC 尚未完成。
net6.0 是离线验证目标，不是最终 Dalamud 目标框架。

## 离线验证

前置条件：Windows PowerShell、PATH 中可用的 dotnet，以及已安装的 .NET 6 SDK、
net6.0 引用包与运行时。已验证 SDK 6.0.428；其他版本未在本次验证。
两个项目无外部包依赖；NuGet.Config 使用 clear 清空所有包源，无外部包源。
缺少前置条件时停止并报告，不下载或自动安装任何内容。

先在 PowerShell 中进入你自己的仓库根目录，再执行以下命令。
Get-Location 应显示包含 README.md、NuGet.Config、src 和 tests 的目录。
沿用环境提供的 DOTNET_CLI_HOME、NUGET_PACKAGES、TEMP/TMP，它们必须指向允许写入的位置。
本次受限沙箱已将这些位置配置在 .build/runtime 下，无需更改系统、权限或沙箱。
Directory.Build.props 也将中间产物和输出放在 .build/runtime 下。
每条 dotnet 命令后检查 $LASTEXITCODE，非零即停止并记录。

~~~powershell
Get-Location
dotnet --version
dotnet restore ./tests/CritAudio.Diagnostics.Tests/CritAudio.Diagnostics.Tests.csproj --configfile ./NuGet.Config
dotnet build ./src/CritAudio.Diagnostics.Core/CritAudio.Diagnostics.Core.csproj --no-restore
dotnet build ./tests/CritAudio.Diagnostics.Tests/CritAudio.Diagnostics.Tests.csproj --no-restore
dotnet run --project ./tests/CritAudio.Diagnostics.Tests/CritAudio.Diagnostics.Tests.csproj --no-restore --no-build
~~~

测试使用无包控制台运行器，失败返回 1。所有事件为合成匿名值，无游戏身份。
历史状态：此前 README 记载离线构建通过；本说明不推断历史测试总数，也不将其视为游戏验收。
本次 CRIT-AUDIO-PUBLISH-FIX-002（2026-11-03）在仓库根目录使用 SDK 6.0.428 执行上述命令：
清源 restore、内核 build、测试 build、run 退出码均为 0。restore 报告所有项目已是最新状态；
两次 build 均为 0 警告、0 错误；run 最终输出 tests=35、failures=0。
本次未下载依赖；结果仅覆盖合成匿名事件离线诊断。
国服 PoC 范围及待验收步骤见 [运行手册](docs/cn-poc-runbook.md)。

## 后续发布范围

本次任务在本地初始化 Git 并提交以下离线诊断源码，目标为 GitHub 私有源码仓库 VedaCA。
仓库不提供可安装插件或游戏实测；本地提交不代表已在 GitHub 发布。
发布清单仅限以下 12 个文件，构建产物、环境配置和原始证据不在发布范围：

- .gitignore
- README.md
- Directory.Build.props
- NuGet.Config
- src/CritAudio.Diagnostics.Core/CandidateFilter.cs
- src/CritAudio.Diagnostics.Core/CastIdentity.cs
- src/CritAudio.Diagnostics.Core/CritAudio.Diagnostics.Core.csproj
- src/CritAudio.Diagnostics.Core/DiagnosticRing.cs
- src/CritAudio.Diagnostics.Core/Effect.cs
- tests/CritAudio.Diagnostics.Tests/CritAudio.Diagnostics.Tests.csproj
- tests/CritAudio.Diagnostics.Tests/Program.cs
- docs/cn-poc-runbook.md

## 数据边界和解释

Effect 是不可变记录，只保存数值、枚举和布尔值，无指针、名称、账户或网络对象。
Session 使用非零 UInt64 会话代次；未来适配器须在进程重启或切会话时生成新代次，
不同会话的相同序列不能视为同一施放。Caster/LocalPlayer/Target 为非零 UInt64 不透明身份，
不截断为 32 位；这些是离线容器选择，不证明真实字段宽度。
ActionId 为非零 UInt32 实际效果动作 ID；GlobalSequence 为 UInt32，允许 0 和回绕。
真实布局和哨兵值仍待验证。

ObservedTicks 为非负 Int64 单调观察时钟刻度，由未来适配器从同一单调时钟复制，不是墙上时间。
模型仅校验单值范围，不证明输入流单调；当前不排序、不按时间合并。
EffectIndex 为 0..63，仅是内核资源边界，不声称游戏协议最多 64 效果。
Damage 为 Int64，接受 -UInt32.MaxValue..UInt32.MaxValue，保留负值以返回非正伤害拒绝；
这不是已验证国服伤害解码范围。枚举值必须已定义。
IsValid 校验与候选过滤分开，诊断可记录非正伤害等拒绝事件。

Crit/Direct 是已解析输入，未提供 Param 位偏移或伤害解码实现。
SelectedForCurrentJob 表示上游已确认当前职业与实际动作选择；这里不建立职业目录。
筛选按源码顺序返回首个拒绝原因，不承诺同时返回多个原因。
Compatibility 默认 Unknown；即使输入标记 SyntheticVerified，也必须同时传入
CreateOfflineSynthetic() 的合成上下文才可能通过。该工厂仅提供固定 synthetic-fixture-v1，
没有真实客户端批准 API，不可接入实战作为绕过。Unknown/Unverified 均失败关闭。

CastIdentity 仅比较 Session/Caster/GlobalSequence/ActionId；公开值构造不证明事件有效，
From 会验证 Effect。目标、效果索引和观察时间不进入身份。没有 TTL、去重缓存、
动作变体归并或主目标/AOE 选择；身份相等不代表完成跨包去重。

DiagnosticRing 容量为 1..4096，按插入顺序快照，满时覆盖最旧条目。
单线程使用，由调用者负责串行访问；快照数组与内部数组隔离，条目不可变。
每个条目为有界 Effect 和拒绝枚举，不存玩家姓名，不自动导出、不联网、不播放声音。
