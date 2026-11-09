namespace VedaCA.Diagnostics.Core;

// Only numeric enum values cross this boundary. No message, sender or text.
// Attach/Detach must not wait for callbacks and must support partial rollback.
public interface IChatLogObservationSource
{
    void Attach(Action<int, int> callback);
    void Detach(Action<int, int> callback);
}

public readonly record struct ChatLogObservationRecord(long Sequence, int LogKind, int SourceKind);

public sealed class ChatLogObservation : IDisposable
{
    // Reuse the existing tested managed subscription/epoch/ring protocol in an
    // independent instance. Its private transport codes are NOT fly-text kinds.
    // No fly-text records, subscriptions or sequences are shared.
    private readonly FlyTextObservation session;

    public ChatLogObservation(IChatLogObservationSource source, int capacity = 512)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (capacity <= 0 || capacity > 512)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.session = new FlyTextObservation(new EncodedSource(source), capacity);
    }

    // API15 supplied evidence: Damage=41, Action=43. Keep this allowlist narrow.
    public static bool Accepts(int logKind) => logKind is 41 or 43;

    public bool Running => this.session.Running;
    public bool CleanupPending => this.session.CleanupPending;
    public bool Faulted => this.session.Faulted;
    public int Capacity => this.session.Capacity;
    public long Dropped => this.session.Dropped;
    public bool Start() => this.session.Start();
    public void Stop() => this.session.Stop();
    public void Clear() => this.session.Clear();
    public void Dispose() => this.session.Dispose();

    public ChatLogObservationRecord[] Snapshot()
    {
        var rows = this.session.Snapshot();
        var result = new ChatLogObservationRecord[rows.Length];
        for (var i = 0; i < rows.Length; i++)
            result[i] = new(rows[i].Sequence, rows[i].Kind == 0 ? 41 : 43, rows[i].Val1);
        return result;
    }

    private sealed class EncodedSource : IFlyTextObservationSource
    {
        private readonly IChatLogObservationSource source;
        private Action<int, int>? owner;
        private Action<int, int>? handler;

        public EncodedSource(IChatLogObservationSource source) => this.source = source;

        public void Attach(Action<int, int> callback)
        {
            if (this.handler != null)
                throw new InvalidOperationException("Chat log subscription already owned.");
            Action<int, int> next = (logKind, sourceKind) =>
            {
                if (Accepts(logKind))
                    callback(logKind == 41 ? 0 : 1, sourceKind);
            };
            // Retain rollback ownership before invoking the external accessor.
            this.owner = callback;
            this.handler = next;
            this.source.Attach(next);
        }

        public void Detach(Action<int, int> callback)
        {
            if (!ReferenceEquals(this.owner, callback) || this.handler == null)
                return;
            this.source.Detach(this.handler);
            // A failed removal retains both fields for an explicit retry.
            this.handler = null;
            this.owner = null;
        }
    }
}
