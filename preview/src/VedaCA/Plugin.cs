using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using VedaCA.Diagnostics.Core;

namespace VedaCA;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IPlayerState playerState;
    private readonly ResourceScope registrations = new();
    private readonly FlyTextObservation observation;
    private readonly ChatLogObservation chatObservation;
    private int disposed;
    private bool windowVisible;
    private bool uiFault;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IPlayerState playerState,
        IFlyTextGui flyTextGui,
        IChatGui chatGui)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.playerState = playerState;
        // Construction subscribes to neither observation event.
        this.observation = new FlyTextObservation(new DalamudFlyTextSource(flyTextGui), 512);
        this.chatObservation = new ChatLogObservation(new DalamudChatLogSource(chatGui), 512);

        // No native address resolution, Hook construction or pointer reads.
        var ownsCommand = false;
        try
        {
            this.registrations.Register(() => { }, this.observation.Dispose);
            this.registrations.Register(() => { }, this.chatObservation.Dispose);
            this.registrations.Register(
                () =>
                {
                    if (!this.commands.AddHandler("/vedaca", new CommandInfo(this.OnCommand)
                    {
                        HelpMessage = "打开 VedaCA 只读诊断窗口；飞字与日志来源实验须分别主动启用。",
                    }))
                        throw new InvalidOperationException("无法注册 /vedaca 命令。");
                    ownsCommand = true;
                },
                () =>
                {
                    if (!ownsCommand)
                        return;
                    ownsCommand = false;
                    this.commands.RemoveHandler("/vedaca");
                });
            this.registrations.Register(
                () => this.pluginInterface.UiBuilder.Draw += this.Draw,
                () => this.pluginInterface.UiBuilder.Draw -= this.Draw);
            this.registrations.Register(
                () => this.pluginInterface.UiBuilder.OpenMainUi += this.OpenWindow,
                () => this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenWindow);
            this.registrations.Register(
                () => this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenWindow,
                () => this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenWindow);
        }
        catch (Exception constructionError)
        {
            Interlocked.Exchange(ref this.disposed, 1);
            this.observation.Dispose();
            this.chatObservation.Dispose();
            try
            {
                this.registrations.Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(constructionError, cleanupError);
            }
            throw;
        }
    }

    // Retained future-adapter predicate; never invoked by either observation.
    // No EntityId is read by this build. Future use requires separate review.
    private bool IsLocalCaster(uint casterEntityId) =>
        this.playerState.IsLoaded && this.playerState.EntityId == casterEntityId;

    private void OnCommand(string command, string arguments) => this.OpenWindow();

    private void OpenWindow()
    {
        if (Volatile.Read(ref this.disposed) == 0)
            this.windowVisible = true;
    }

    private static string KindLabel(int kind) => kind switch
    {
        0 => "AutoAttackOrDot",
        1 => "AutoAttackOrDotDh",
        2 => "AutoAttackOrDotCrit",
        3 => "AutoAttackOrDotCritDh",
        4 => "Damage",
        5 => "DamageDh",
        6 => "DamageCrit",
        7 => "DamageCritDh",
        _ => "Unknown",
    };

    private void StopObservations()
    {
        this.observation.Stop();
        this.chatObservation.Stop();
    }

    private void Draw()
    {
        if (Volatile.Read(ref this.disposed) != 0 || !this.windowVisible)
            return;
        try
        {
            ImGui.SetNextWindowSize(new Vector2(900, 800), ImGuiCond.FirstUseEver);
            var expanded = ImGui.Begin("VedaCA — 只读飞字与日志来源实验", ref this.windowVisible);
            try
            {
                if (expanded)
                    this.DrawContents();
            }
            finally
            {
                ImGui.End();
            }
            if (!this.windowVisible)
                this.StopObservations();
        }
        catch
        {
            this.uiFault = true;
            this.StopObservations();
        }
    }

    private void DrawContents()
    {
        ImGui.TextWrapped("原生 ActionEffect：失败关闭；未创建 Hook；不读取未知内存。运行客户端精准版本未知。");
        ImGui.TextWrapped($"受支持构建证据条目：{CaptureSupport.VerifiedBuildCount}。国服 ABI、运行环境与 7.58 的 Dalamud 支持均未核实。");
        ImGui.TextWrapped("截至 2026-11-09，7.57 当前在线，目标国服 7.58 尚未上线。公告目标客户端号 2026.11.03.0000.0000 仅为计划信息，不是运行版本或 ABI 证据。");
        ImGui.TextWrapped("SDK/Dalamud 15.0.3.9、API 15 仅为独立开发参考，不证明卫月兼容；调用官方服务也不能独立保证框架原生层安全。");
        ImGui.TextWrapped(CaptureSupport.Explain(null));
        if (this.uiFault)
            ImGui.TextWrapped("窗口操作发生异常，两种观察已停止；未记录异常内容。");
        this.DrawChatObservation();
        ImGui.Separator();
        ImGui.TextUnformatted("飞字观察辅助（独立数据区）");
        ImGui.TextWrapped("飞字观察辅助只接收官方 IFlyTextGui.FlyTextCreated 受管事件中可观察到的飞字数据。它不是原始战斗事件，可能不报告全部战斗数据；没有 ActionId、施法者或目标识别，也不能保证某条属于本人输出。尚未完整实现原诊断目标。");
        ImGui.TextWrapped("仅采样 kind 0–7 与 val1 原始数值、本地序号。标签只是参考框架枚举名，需实机对照；不据此断言玩家攻击、伤害数值或准确暴击/直击。禁止读取或复制 text1/text2，不显示姓名、目标或身份 ID。");
        ImGui.TextWrapped("默认停用。暂停、停止、清空、关闭窗口及卸载都会请求撤销订阅并使旧代次失效；失败时显示待重试。清空后须再次主动启用。只在内存查看，无音效、联网、记录文件或复制功能；不调用 AddFlyText，不修改 ref 参数或 handled。");
        ImGui.TextWrapped("已有实机一次 90822!! 与 kind 7、val1 90822 的对照，只是单次现象，不证明事件归属或完整分类；不能与下列日志序号或数值对应。");

        if (this.observation.Faulted)
            ImGui.TextWrapped("飞字订阅或撤销发生异常；未记录异常内容。若撤销待重试，禁止再次订阅。");
        if (this.observation.CleanupPending)
            ImGui.TextWrapped("飞字撤销待重试：旧代次已经拒绝写入，但无法宣称外部事件解绑成功。请使用停止/重试撤销。");
        if (!this.observation.Running && !this.observation.CleanupPending)
        {
            if (ImGui.Button("主动启用飞字观察辅助"))
                this.observation.Start();
        }
        if (ImGui.Button("暂停观察"))
            this.observation.Stop();
        ImGui.SameLine();
        if (ImGui.Button("停止 / 重试撤销"))
            this.observation.Stop();
        ImGui.SameLine();
        if (ImGui.Button("清空并停止"))
            this.observation.Clear();

        var rows = this.observation.Snapshot();
        ImGui.TextUnformatted($"飞字状态：{(this.observation.Running ? "观察中" : "已停止")}；内存 {rows.Length}/{this.observation.Capacity}；溢出丢弃 {this.observation.Dropped} 条。");
        ImGui.TextWrapped("溢出策略：丢弃最旧记录，保留最新 512 条。序号为本次插件实例的飞字本地计数，清空不重置；不对应游戏事件 ID 或日志实验序号。");
        foreach (var row in rows)
            ImGui.TextUnformatted($"飞字本地序号 {row.Sequence} | kind(raw) {row.Kind} | {KindLabel(row.Kind)} | val1(raw) {row.Val1}");
    }

    private void DrawChatObservation()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("日志来源实验（独立数据区；默认不订阅）");
        ImGui.TextWrapped("仅只读观察官方 IChatGui.ChatMessageUnhandled，提前仅接受 Damage(41)、Action(43)。游戏日志设置可能影响可见性；被框架处理的消息或其他战斗类型不在本实验范围，不能保证完整覆盖。");
        ImGui.TextWrapped("仅保存 LogKind、SourceKind 数字和独立本地序号。LocalPlayer 只是框架的发送/行动者关系标记，未经用户实机验证；其他 SourceKind 也是关系类别，不是角色身份。暂时不能确认技能 ID、四种暴击/直击分类及归属的实际准确性。");
        ImGui.TextWrapped("不读取、不显示、不留存文本、聊天 Sender、玩家名、目标名、实体 ID 或位置；回调结束后不持有消息对象。无记录写盘、网络、原生 Hook、消息修改或自动操作。不会据此播放命中声音。");
        ImGui.TextWrapped("日志与飞字分别订阅、计数和显示，不能据时间、数字或序号推断对应。未订阅 LogMessage；LogMessageId 是模板 ID，不是 ActionId。");
        if (this.chatObservation.Faulted)
            ImGui.TextWrapped("日志订阅或撤销异常；未记录异常内容。旧代次已失效。");
        if (this.chatObservation.CleanupPending)
            ImGui.TextWrapped("日志撤销待重试，不能宣称解绑成功；重新订阅已被禁止，请点击停止/重试撤销。");
        if (!this.chatObservation.Running && !this.chatObservation.CleanupPending)
        {
            if (ImGui.Button("主动开始日志来源实验"))
                this.chatObservation.Start();
        }
        if (ImGui.Button("暂停日志实验"))
            this.chatObservation.Stop();
        ImGui.SameLine();
        if (ImGui.Button("停止 / 重试撤销日志"))
            this.chatObservation.Stop();
        ImGui.SameLine();
        if (ImGui.Button("清空日志并停止"))
            this.chatObservation.Clear();

        var rows = this.chatObservation.Snapshot();
        ImGui.TextUnformatted($"日志状态：{(this.chatObservation.Running ? "观察中" : "已停止")}；内存 {rows.Length}/{this.chatObservation.Capacity}；溢出丢弃 {this.chatObservation.Dropped} 条。");
        ImGui.TextWrapped("计数仅统计当前快照中保留的记录，最多 512 条；溢出丢弃最旧记录。暂停/停止保留记录，清空/卸载擦除缓冲；清空不重置本地序号，重新观察须主动开始。");
        foreach (var group in rows.GroupBy(row => row.LogKind).OrderBy(group => group.Key))
            ImGui.TextUnformatted($"LogKind {group.Key}（{(group.Key == 41 ? "Damage" : "Action")}）计数：{group.Count()}");
        foreach (var group in rows.GroupBy(row => row.SourceKind).OrderBy(group => group.Key))
        {
            var label = group.Key == (int)XivChatRelationKind.LocalPlayer
                ? "LocalPlayer：框架标记，未经实机验证"
                : "框架关系类别";
            ImGui.TextUnformatted($"SourceKind {group.Key}（{label}）计数：{group.Count()}");
        }
        foreach (var row in rows)
            ImGui.TextUnformatted($"日志本地序号 {row.Sequence} | LogKind(raw) {row.LogKind} | SourceKind(raw) {row.SourceKind}");
    }

    public void Dispose()
    {
        // Repeated Dispose retries pending event removals for both observers.
        Interlocked.Exchange(ref this.disposed, 1);
        this.observation.Dispose();
        this.chatObservation.Dispose();
        try
        {
            this.registrations.Dispose();
        }
        catch
        {
            // ResourceScope already attempted every release; no contents logged.
        }
    }
}
