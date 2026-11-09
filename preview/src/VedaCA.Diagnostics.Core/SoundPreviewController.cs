using System;

namespace VedaCA.Diagnostics.Core;

/// <summary>Manual preview only. Construction and status reads have no playback effects.</summary>
public sealed class SoundPreviewController
{
    private readonly Func<bool> play;
    private string status = "尚未试听。仅点击试听按钮才请求播放。";

    /// <summary>The injected backend returns request acceptance, not audibility.</summary>
    public SoundPreviewController(Func<bool> play)
    {
        this.play = play ?? throw new ArgumentNullException(nameof(play));
    }

    public string Status => this.status;

    public bool Preview()
    {
        try
        {
            if (this.play())
            {
                this.status = "系统声音请求已发送；不保证能听到，不代表已实现战斗触发。";
                return true;
            }
        }
        catch (Exception)
        {
            // Do not expose backend exception details or let playback errors reach the UI.
        }

        this.status = "试听失败：系统声音调用失败或当前环境不支持；可检查 Windows 声音设置和输出设备后再次点击试听。";
        return false;
    }
}
