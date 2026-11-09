namespace VedaCA.Diagnostics.Core;

public sealed class BoundedBuffer<T> where T : struct
{
    private readonly object gate = new();
    private readonly T[] items;
    private int head;
    private int count;

    public BoundedBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.items = new T[capacity];
    }

    public int Capacity => this.items.Length;

    public int Count
    {
        get
        {
            lock (this.gate)
                return this.count;
        }
    }

    public void Enqueue(T item)
    {
        lock (this.gate)
        {
            if (this.count == this.items.Length)
            {
                this.items[this.head] = item;
                this.head = (this.head + 1) % this.items.Length;
            }
            else
            {
                this.items[(this.head + this.count) % this.items.Length] = item;
                this.count++;
            }
        }
    }

    // Independent, oldest-to-newest snapshot; no lock escapes to the UI.
    public T[] Snapshot()
    {
        lock (this.gate)
        {
            var result = new T[this.count];
            for (var i = 0; i < this.count; i++)
                result[i] = this.items[(this.head + i) % this.items.Length];
            return result;
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            Array.Clear(this.items);
            this.head = 0;
            this.count = 0;
        }
    }
}
