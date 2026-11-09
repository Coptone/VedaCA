using VedaCA.Diagnostics.Core;

namespace VedaCA.Diagnostics.Tests;

internal static class Program
{
    private static int Main()
    {
        (string Name, Action Run)[] tests =
        [
            ("0x00: no candidate bits", () => Flags(0x00, false, false)),
            ("0x20: candidate critical", () => Flags(0x20, true, false)),
            ("0x40: candidate direct", () => Flags(0x40, false, true)),
            ("0x60: both candidate bits", () => Flags(0x60, true, true)),
            ("unrelated bits coexist", OtherBits),
            ("all 256 inputs", AllBytes),
            ("invalid capacities", InvalidCapacities),
            ("capacity, overwrite and FIFO order", BufferOrder),
            ("capacity one", CapacityOne),
            ("snapshot is independent", IndependentSnapshot),
            ("clear and reuse", ClearAndReuse),
            ("concurrent producers and snapshots", ConcurrentAccess),
        ];

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed; synthetic core tests only.");
        return failures == 0 ? 0 : 1;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Flags(byte value, bool critical, bool direct)
    {
        var actual = CandidateFlagDecoder.Decode(value);
        Require(actual.Critical == critical && actual.Direct == direct,
            $"0x{value:X2}: expected ({critical}, {direct}), got ({actual.Critical}, {actual.Direct})");
    }

    private static void OtherBits()
    {
        Flags(0x9F, false, false);
        Flags(0xBF, true, false);
        Flags(0xDF, false, true);
        Flags(0xFF, true, true);
    }

    private static void AllBytes()
    {
        for (var value = 0; value <= 255; value++)
            Flags((byte)value, (value / 32) % 2 == 1, (value / 64) % 2 == 1);
    }

    private static void InvalidCapacities()
    {
        foreach (var capacity in new[] { 0, -1 })
        {
            var threw = false;
            try
            {
                _ = new BoundedBuffer<int>(capacity);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }
            Require(threw, $"capacity {capacity} must throw");
        }
    }

    private static void BufferOrder()
    {
        var buffer = new BoundedBuffer<int>(3);
        Require(buffer.Capacity == 3 && buffer.Count == 0, "initial state");
        Require(buffer.Snapshot().Length == 0, "initial snapshot must be empty");
        buffer.Enqueue(10);
        buffer.Enqueue(20);
        Require(buffer.Snapshot().SequenceEqual(new[] { 10, 20 }), "partial FIFO order");
        buffer.Enqueue(30);
        Require(buffer.Count == 3, "exact capacity");
        Require(buffer.Snapshot().SequenceEqual(new[] { 10, 20, 30 }), "full FIFO order");
        buffer.Enqueue(40);
        buffer.Enqueue(50);
        Require(buffer.Count == 3, "bounded count");
        Require(buffer.Snapshot().SequenceEqual(new[] { 30, 40, 50 }), "oldest entries must be overwritten");
        for (var i = 0; i < 100; i++)
            buffer.Enqueue(i);
        Require(buffer.Snapshot().SequenceEqual(new[] { 97, 98, 99 }), "repeated wraparound");
    }

    private static void CapacityOne()
    {
        var buffer = new BoundedBuffer<int>(1);
        buffer.Enqueue(1);
        buffer.Enqueue(2);
        Require(buffer.Count == 1 && buffer.Snapshot().SequenceEqual(new[] { 2 }), "single-slot overwrite");
    }

    private static void IndependentSnapshot()
    {
        var buffer = new BoundedBuffer<int>(2);
        buffer.Enqueue(7);
        var snapshot = buffer.Snapshot();
        snapshot[0] = 99;
        Require(buffer.Snapshot()[0] == 7, "snapshot mutation must not change buffer");
        buffer.Enqueue(8);
        buffer.Enqueue(9);
        Require(snapshot.SequenceEqual(new[] { 99 }), "later writes must not change earlier snapshot");
    }

    private static void ClearAndReuse()
    {
        var buffer = new BoundedBuffer<int>(2);
        buffer.Enqueue(1);
        buffer.Enqueue(2);
        buffer.Enqueue(3);
        buffer.Clear();
        Require(buffer.Count == 0 && buffer.Snapshot().Length == 0, "clear after wraparound");
        buffer.Enqueue(4);
        buffer.Enqueue(5);
        Require(buffer.Snapshot().SequenceEqual(new[] { 4, 5 }), "reuse after clear");
    }

    private static void ConcurrentAccess()
    {
        var buffer = new BoundedBuffer<int>(64);
        var producers = Enumerable.Range(0, 4).Select(producer => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
                buffer.Enqueue(producer * 2000 + i);
        })).ToArray();

        for (var i = 0; i < 1000; i++)
        {
            var snapshot = buffer.Snapshot();
            Require(snapshot.Length <= 64, "snapshot exceeded capacity");
            Require(snapshot.Distinct().Count() == snapshot.Length, "duplicate or torn snapshot");
            Require(snapshot.All(value => value >= 0 && value < 8000), "unexpected value");
        }

        Task.WaitAll(producers);
        var final = buffer.Snapshot();
        Require(buffer.Count == 64 && final.Length == 64, "final bounded count");
        Require(final.Distinct().Count() == 64, "final values must be unique");
        foreach (var producer in Enumerable.Range(0, 4))
        {
            var values = final.Where(value => value / 2000 == producer).ToArray();
            Require(values.SequenceEqual(values.OrderBy(value => value)), "per-producer order must be preserved");
        }
    }
}
