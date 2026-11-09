namespace VedaCA.Diagnostics.Core;

// This interface transports only kind and val1. No text, identity or native data.
// Attach/Detach must not wait for callbacks. Calls are serialized by the session.
// Detach must support rollback after a partially successful Attach.
public interface IFlyTextObservationSource
{
    void Attach(Action<int, int> callback);
    void Detach(Action<int, int> callback);
}

public readonly record struct FlyTextObservationRecord(long Sequence, int Kind, int Val1);

public sealed class FlyTextObservation : IDisposable
{
    private readonly object gate = new();
    private readonly IFlyTextObservationSource source;
    private readonly FlyTextObservationRecord[] buffer;
    private Action<int, int>? registration;
    private object? epoch;
    private int head;
    private int count;
    private long sequence;
    private long dropped;
    private bool running;
    private bool disposed;
    private bool faulted;

    public FlyTextObservation(IFlyTextObservationSource source, int capacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.source = source;
        this.buffer = new FlyTextObservationRecord[capacity];
    }

    public bool Running { get { lock (this.gate) return this.running; } }
    public bool CleanupPending { get { lock (this.gate) return this.registration != null && !this.running; } }
    public bool Faulted { get { lock (this.gate) return this.faulted; } }
    public long Dropped { get { lock (this.gate) return this.dropped; } }
    public int Capacity => this.buffer.Length;

    public static bool Accepts(int kind) => (uint)kind <= 7u;

    public bool Start()
    {
        lock (this.gate)
        {
            if (this.disposed)
                return false;
            if (this.running)
                return true;
            // Never add another subscription while rollback is unresolved.
            if (this.registration != null)
                return false;

            var token = new object();
            Action<int, int> callback = (kind, val1) => this.Observe(token, kind, val1);
            this.epoch = token;
            this.registration = callback;
            this.running = true;
            this.faulted = false;
            try
            {
                this.source.Attach(callback);
                return true;
            }
            catch
            {
                this.faulted = true;
                this.StopLocked();
                this.ClearBufferLocked();
                return false;
            }
        }
    }

    // Pause and stop both revoke the subscription. Existing rows are retained.
    public void Stop()
    {
        lock (this.gate)
            this.StopLocked();
    }

    // Clear also stops. Only a subsequent explicit Start may admit new rows.
    public void Clear()
    {
        lock (this.gate)
        {
            this.StopLocked();
            this.ClearBufferLocked();
        }
    }

    private void StopLocked()
    {
        this.running = false;
        this.epoch = null;
        var callback = this.registration;
        if (callback == null)
            return;
        try
        {
            this.source.Detach(callback);
            this.registration = null;
        }
        catch
        {
            // Retain ownership for an explicit Stop/Clear/Dispose retry.
            // The invalid epoch rejects even an externally retained callback.
            this.faulted = true;
        }
    }

    private void Observe(object token, int kind, int val1)
    {
        // No exception from observation is allowed back into the event source.
        try
        {
            if (!Accepts(kind))
                return;
            lock (this.gate)
            {
                if (this.disposed || !this.running || !ReferenceEquals(token, this.epoch))
                    return;
                if (this.sequence == long.MaxValue)
                    return;
                var record = new FlyTextObservationRecord(++this.sequence, kind, val1);
                if (this.count == this.buffer.Length)
                {
                    this.buffer[this.head] = record;
                    this.head = (this.head + 1) % this.buffer.Length;
                    if (this.dropped != long.MaxValue)
                        this.dropped++;
                }
                else
                {
                    var index = (int)(((long)this.head + this.count) % this.buffer.Length);
                    this.buffer[index] = record;
                    this.count++;
                }
            }
        }
        catch
        {
            // Do not inspect, format or log event data or exceptions.
        }
    }

    public FlyTextObservationRecord[] Snapshot()
    {
        lock (this.gate)
        {
            var result = new FlyTextObservationRecord[this.count];
            for (var i = 0; i < result.Length; i++)
                result[i] = this.buffer[(int)(((long)this.head + i) % this.buffer.Length)];
            return result;
        }
    }

    private void ClearBufferLocked()
    {
        Array.Clear(this.buffer, 0, this.buffer.Length);
        this.head = 0;
        this.count = 0;
        this.dropped = 0;
        // Sequence remains monotonic across clear and restart.
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.disposed = true;
            this.StopLocked();
            this.ClearBufferLocked();
        }
    }
}
