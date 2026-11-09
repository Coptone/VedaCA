using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
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
    private int disposed;
    private bool windowVisible;
    private bool uiFault;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IPlayerState playerState,
        IFlyTextGui flyTextGui)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.playerState = playerState;
        // Construction does not subscribe to FlyTextCreated.
        this.observation = new FlyTextObservation(new DalamudFlyTextSource(flyTextGui), 512);

        // No native address resolution, Hook construction or pointer reads.
        var ownsCommand = false;
        try
        {
            this.registrations.Register(() => { }, this.observation.Dispose);
            this.registrations.Register(
                () =>
                {
                    if (!this.commands.AddHandler("/vedaca", new CommandInfo(this.OnCommand)
                    {
                        HelpMessage = "打开 VedaCA 只读诊断窗口；飞字观察须主动启用。",
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

    // Retained future-adapter predicate; never invoked by fly-text observation.
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

    private void Draw()
    {
        if (Volatile.Read(ref this.disposed) != 0 || !this.windowVisible)
            return;
        try
        {
            ImGui.SetNextWindowSize(new Vector2(900, 650), ImGuiCond.FirstUseEver);
            var expanded = ImGui.Begin("VedaCA — 只读飞字观察辅助", ref this.windowVisible);
            try
            {
                if (expanded)
                    this.DrawContents();
            }
            finally
            {
                ImGui.End();
            }
            // Closing the window also stops the subscription.
            if (!this.windowVisible)
                this.observation.Stop();
        }
        catch
        {
            this.uiFault = true;
            this.observation.Stop();
        }
    }

    private void DrawContents()
    {
        ImGui.TextWrapped("原生 ActionEffect：失败关闭；未创建 Hook；不读取未知内存。运行客户端精准版本未知。");
        ImGui.TextWrapped($"受支持构建证据条目：{CaptureSupport.VerifiedBuildCount}。国服 ABI、运行环境与 7.58 的 Dalamud 支持均未核实。");
        ImGui.TextWrapped("截至 2026-11-09，7.57 当前在线，目标国服 7.58 尚未上线。公告目标客户端号 2026.11.03.0000.0000 仅为计划信息，不是运行版本或 ABI 证据。");
        ImGui.TextWrapped("SDK/Dalamud 15.0.3.9、API 15 仅为独立开发参考，不证明卫月兼容；调用官方服务也不能独立保证框架原生层安全。");
        ImGui.TextWrapped(CaptureSupport.Explain(null));
        ImGui.Separator();
        ImGui.TextWrapped("飞字观察辅助只接收官方 IFlyTextGui.FlyTextCreated 受管事件中可观察到的飞字数据。它不是原始战斗事件，可能不报告全部战斗数据；没有 ActionId、施法者或目标识别，也不能保证某条属于本人输出。尚未完整实现原诊断目标。");
        ImGui.TextWrapped("仅采样 kind 0–7 与 val1 原始数值、本地序号。标签只是参考框架枚举名，需实机对照；不据此断言玩家攻击、伤害数值或准确暴击/直击。禁止读取或复制 text1/text2，不显示姓名、目标或身份 ID。");
        ImGui.TextWrapped("默认停用。暂停、停止、清空、关闭窗口及卸载都会请求撤销订阅并使旧代次失效；失败时显示待重试。清空后须再次主动启用。只在内存查看，无音效、联网、记录文件或复制功能；不调用 AddFlyText，不修改 ref 参数或 handled。");

        if (this.uiFault)
            ImGui.TextWrapped("窗口操作发生异常，观察已停止；未记录异常内容。");
        if (this.observation.Faulted)
            ImGui.TextWrapped("订阅或撤销发生异常；未记录异常内容。若撤销待重试，禁止再次订阅。");
        if (this.observation.CleanupPending)
            ImGui.TextWrapped("撤销待重试：旧代次已经拒绝写入，但无法宣称外部事件解绑成功。请使用停止/重试撤销。");

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
        ImGui.TextUnformatted($"状态：{(this.observation.Running ? "观察中" : "已停止")}；内存 {rows.Length}/{this.observation.Capacity}；溢出丢弃 {this.observation.Dropped} 条。");
        ImGui.TextWrapped("溢出策略：丢弃最旧记录，保留最新 512 条。序号为本次插件实例的本地计数，清空不重置；不对应游戏事件 ID。");
        ImGui.Separator();
        foreach (var row in rows)
            ImGui.TextUnformatted($"本地序号 {row.Sequence} | kind(raw) {row.Kind} | {KindLabel(row.Kind)} | val1(raw) {row.Val1}");
    }

    public void Dispose()
    {
        // Retry a previously failed fly-text removal even on repeated Dispose.
        Interlocked.Exchange(ref this.disposed, 1);
        this.observation.Dispose();
        try
        {
            this.registrations.Dispose();
        }
        catch
        {
            // ResourceScope already attempted every release. Do not propagate
            // cleanup exceptions to the framework or log their contents.
        }
    }
}
