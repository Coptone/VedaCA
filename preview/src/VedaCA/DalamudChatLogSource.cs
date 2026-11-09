using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using VedaCA.Diagnostics.Core;

namespace VedaCA;

// Owned only by the serialized ChatLogObservation lifecycle.
internal sealed class DalamudChatLogSource : IChatLogObservationSource
{
    private readonly IChatGui service;
    private Action<int, int>? owner;
    private Subscription? subscription;

    public DalamudChatLogSource(IChatGui service) => this.service = service;

    public void Attach(Action<int, int> callback)
    {
        if (this.subscription != null)
            throw new InvalidOperationException("Chat log subscription already owned.");
        var next = new Subscription(callback);
        this.owner = callback;
        this.subscription = next;
        // Method-group conversion uses the event's actual delegate type.
        this.service.ChatMessageUnhandled += next.OnMessage;
    }

    public void Detach(Action<int, int> callback)
    {
        if (!ReferenceEquals(this.owner, callback) || this.subscription == null)
            return;
        this.service.ChatMessageUnhandled -= this.subscription.OnMessage;
        this.subscription = null;
        this.owner = null;
    }

    private sealed class Subscription
    {
        private readonly Action<int, int> callback;

        public Subscription(Action<int, int> callback) => this.callback = callback;

        public void OnMessage(IChatMessage message)
        {
            try
            {
                // Reject unrelated messages before reading SourceKind.
                var kind = message.LogKind;
                if (kind != XivChatType.Damage && kind != XivChatType.Action)
                    return;
                // Read only the framework relation enum, never Sender or text.
                var sourceKind = (int)message.SourceKind;
                this.callback((int)kind, sourceKind);
            }
            catch
            {
                // No exception contents or message data are logged or retained.
            }
        }
    }
}
