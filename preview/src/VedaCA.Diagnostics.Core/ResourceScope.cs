namespace VedaCA.Diagnostics.Core;

// Registration rollback for managed resources. Detach must be idempotent and
// safe after an attempted attach, including a partially completed attach.
public sealed class ResourceScope : IDisposable
{
    private readonly object gate = new();
    private readonly Stack<Action> releases = new();
    private bool disposed;

    public void Register(Action attach, Action detach)
    {
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentNullException.ThrowIfNull(detach);
        lock (this.gate)
        {
            if (this.disposed)
                throw new ObjectDisposedException(nameof(ResourceScope));
            // Record rollback before invoking an operation that can fail.
            this.releases.Push(detach);
            attach();
        }
    }

    public void Dispose()
    {
        Action[] pending;
        lock (this.gate)
        {
            if (this.disposed)
                return;
            this.disposed = true;
            pending = this.releases.ToArray();
            this.releases.Clear();
        }

        List<Exception>? errors = null;
        foreach (var release in pending)
        {
            try
            {
                release();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
        if (errors != null)
            throw new AggregateException("资源清理存在失败；已尝试全部逆序清理。", errors);
    }
}
