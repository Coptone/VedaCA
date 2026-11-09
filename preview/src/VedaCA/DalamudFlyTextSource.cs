using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using VedaCA.Diagnostics.Core;

namespace VedaCA;

// Used only by the serialized FlyTextObservation lifecycle.
// Calling this official service does not independently validate framework natives.
internal sealed class DalamudFlyTextSource : IFlyTextObservationSource
{
    private readonly IFlyTextGui service;
    private Action<int, int>? owner;
    private IFlyTextGui.OnFlyTextCreatedDelegate? handler;

    public DalamudFlyTextSource(IFlyTextGui service) => this.service = service;

    public void Attach(Action<int, int> callback)
    {
        if (this.handler != null)
            throw new InvalidOperationException("FlyText subscription already owned.");

        // Each subscription captures its own sink, including its own epoch.
        // Never redirect a previously retained delegate to a newer sink.
        IFlyTextGui.OnFlyTextCreatedDelegate next = (
            ref FlyTextKind kind,
            ref int val1,
            ref int val2,
            ref SeString text1,
            ref SeString text2,
            ref uint color,
            ref uint icon,
            ref uint damageTypeIcon,
            ref float yOffset,
            ref bool handled) =>
        {
            try
            {
                // text1/text2 and every other ref argument are untouched.
                // Do not read, copy, stringify or retain either SeString.
                var rawKind = (int)kind;
                if (FlyTextObservation.Accepts(rawKind))
                    callback(rawKind, val1);
            }
            catch
            {
                // Never propagate observation exceptions to the framework.
            }
        };

        // Retain rollback ownership before the event accessor is invoked.
        this.owner = callback;
        this.handler = next;
        this.service.FlyTextCreated += next;
    }

    public void Detach(Action<int, int> callback)
    {
        if (!ReferenceEquals(this.owner, callback) || this.handler == null)
            return;
        this.service.FlyTextCreated -= this.handler;
        // On failure retain both fields, allowing a later removal retry.
        this.handler = null;
        this.owner = null;
    }
}
