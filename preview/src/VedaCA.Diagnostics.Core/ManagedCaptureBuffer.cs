namespace VedaCA.Diagnostics.Core;

public enum ManagedCaptureState
{
    Paused,
    Running,
    Draining,
    Stopped,
}

// Managed queue protocol only. This does not own native callbacks/trampolines
// and must never be used as proof that a native Hook can be destroyed safely.
// The plugin never resumes this buffer and has no producer.
public sealed class ManagedCaptureBuffer
{
    public sealed class Lease
    {
        internal readonly ManagedCaptureBuffer Owner;
        internal readonly object Epoch;
        internal bool Finished;

        internal Lease(ManagedCaptureBuffer owner, object epoch)
        {
            this.Owner = owner;
            this.Epoch = epoch;
        }
    }

    private readonly object gate = new();
    private readonly BoundedBuffer<RawEffectRecord> buffer;
    private object epoch = new();
    private ManagedCaptureState state = ManagedCaptureState.Paused;
    private int inFlight;

    public ManagedCaptureBuffer(int capacity) => this.buffer = new(capacity);

    public ManagedCaptureState State
    {
        get { lock (this.gate) return this.state; }
    }

    public int InFlight
    {
        get { lock (this.gate) return this.inFlight; }
    }

    // Changes managed queue state only; never authorizes a native producer.
    public bool Resume()
    {
        lock (this.gate)
        {
            if (this.state != ManagedCaptureState.Paused)
                return false;
            this.epoch = new object();
            this.state = ManagedCaptureState.Running;
            return true;
        }
    }

    public Lease? TryBegin()
    {
        lock (this.gate)
        {
            if (this.state != ManagedCaptureState.Running)
                return null;
            var lease = new Lease(this, this.epoch);
            checked { this.inFlight++; }
            return lease;
        }
    }

    public bool TryCommit(Lease lease, RawEffectRecord record)
    {
        lock (this.gate)
        {
            if (!ReferenceEquals(lease.Owner, this) || lease.Finished ||
                this.state != ManagedCaptureState.Running ||
                !ReferenceEquals(lease.Epoch, this.epoch))
                return false;
            this.buffer.Enqueue(record);
            return true;
        }
    }

    public void Finish(Lease lease)
    {
        lock (this.gate)
        {
            if (!ReferenceEquals(lease.Owner, this) || lease.Finished)
                return;
            lease.Finished = true;
            this.inFlight--;
            if (this.state == ManagedCaptureState.Draining && this.inFlight == 0)
                this.state = ManagedCaptureState.Stopped;
        }
    }

    public void Pause()
    {
        lock (this.gate)
        {
            if (this.state != ManagedCaptureState.Running)
                return;
            this.epoch = new object();
            this.state = ManagedCaptureState.Paused;
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.epoch = new object();
            this.buffer.Clear();
        }
    }

    // Rejects new work and invalidates all old leases without waiting for them.
    // No wait, task join, native Disable or native Dispose occurs here.
    public void BeginDrain()
    {
        lock (this.gate)
        {
            this.epoch = new object();
            this.buffer.Clear();
            this.state = this.inFlight == 0
                ? ManagedCaptureState.Stopped
                : ManagedCaptureState.Draining;
        }
    }

    public RawEffectRecord[] Snapshot()
    {
        lock (this.gate)
            return this.buffer.Snapshot();
    }
}
