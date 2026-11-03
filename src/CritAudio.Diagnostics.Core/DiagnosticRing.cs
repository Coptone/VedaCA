namespace CritAudio.Diagnostics.Core;

public sealed record DiagnosticEntry(Effect Effect, Rejection Reason);
/// <summary>Single-threaded, bounded diagnostic history. Caller serializes access.</summary>
public sealed class DiagnosticRing
{
    public const int MaxCapacity = 4096;
    private readonly DiagnosticEntry[] entries;
    private int next;
    public DiagnosticRing(int capacity)
    {
        if (capacity < 1 || capacity > MaxCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        entries = new DiagnosticEntry[capacity];
    }
    public int Count { get; private set; }
    public void Add(DiagnosticEntry entry)
    {
        if (entry is null || entry.Effect is null || !entry.Effect.IsValid()
            || !Enum.IsDefined(typeof(Rejection), entry.Reason))
            throw new ArgumentException("Invalid diagnostic entry", nameof(entry));
        entries[next] = entry;
        next = (next + 1) % entries.Length;
        Count = Math.Min(Count + 1, entries.Length);
    }
    public DiagnosticEntry[] Snapshot()
    {
        var result = new DiagnosticEntry[Count];
        var start = (next - Count + entries.Length) % entries.Length;
        for (var i = 0; i < Count; i++) result[i] = entries[(start + i) % entries.Length];
        return result;
    }
}
