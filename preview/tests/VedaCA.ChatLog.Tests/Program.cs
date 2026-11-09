using VedaCA.Diagnostics.Core;

namespace VedaCA.ChatLog.Tests;

internal static class Program
{
    private sealed class Source : IChatLogObservationSource
    {
        public readonly List<Action<int, int>> Active = new();
        public readonly List<Action<int, int>> History = new();
        public bool FailAttach;
        public bool FailDetach;
        public int AttachCalls;

        public void Attach(Action<int, int> callback)
        {
            this.AttachCalls++;
            this.Active.Add(callback);
            this.History.Add(callback);
            if (this.FailAttach)
                throw new InvalidOperationException("synthetic attach failure");
        }

        public void Detach(Action<int, int> callback)
        {
            if (this.FailDetach)
                throw new InvalidOperationException("synthetic detach failure");
            this.Active.Remove(callback);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        (string Name, Action Run)[] tests =
        [
            ("default off, narrow filter and independent numeric relations", Filter),
            ("512 limit, retained counts and snapshot isolation", Bounded),
            ("clear, stop and disposal reject stale generations", Generations),
            ("partial subscription rollback and removal retry", Rollback),
            ("concurrent clear invalidates producer", Concurrent),
            ("fly text and log sessions remain independent", Independent),
        ];
        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed; synthetic numeric managed sources only.");
        return failed == 0 ? 0 : 1;
    }

    private static void Filter()
    {
        var source = new Source();
        using var session = new ChatLogObservation(source);
        Require(!session.Running && source.AttachCalls == 0, "default subscription");
        Require(session.Start() && session.Start() && source.AttachCalls == 1, "single subscription");
        var emit = source.History.Single();
        foreach (var kind in new[] { int.MinValue, -1, 0, 7, 40, 42, 44, 45, int.MaxValue })
            emit(kind, 1);
        emit(41, 1);
        emit(43, 4);
        emit(41, 8);
        emit(43, 999);
        var expected = new[]
        {
            new ChatLogObservationRecord(1, 41, 1),
            new ChatLogObservationRecord(2, 43, 4),
            new ChatLogObservationRecord(3, 41, 8),
            new ChatLogObservationRecord(4, 43, 999),
        };
        Require(session.Snapshot().SequenceEqual(expected), "only allowlisted logs; relations are raw enum values");
    }

    private static void Bounded()
    {
        var source = new Source();
        using var session = new ChatLogObservation(source);
        session.Start();
        var emit = source.History.Single();
        for (var i = 1; i <= 600; i++)
            emit(i % 2 == 0 ? 41 : 43, i % 2 == 0 ? 1 : 4);
        var rows = session.Snapshot();
        Require(rows.Length == 512 && session.Dropped == 88, "bounded storage");
        Require(rows[0].Sequence == 89 && rows[^1].Sequence == 600, "drop oldest");
        Require(rows.Count(row => row.LogKind == 41) == 256 && rows.Count(row => row.LogKind == 43) == 256, "log counts in retained snapshot");
        Require(rows.Count(row => row.SourceKind == 1) == 256 && rows.Count(row => row.SourceKind == 4) == 256, "relation counts in retained snapshot");
        rows[0] = default;
        Require(session.Snapshot()[0].Sequence == 89, "snapshot owns its array");
        session.Clear();
        Require(session.Snapshot().Length == 0 && session.Dropped == 0, "clear storage and counts");
        foreach (var capacity in new[] { 0, -1, 513 })
        {
            var rejected = false;
            try { using var invalid = new ChatLogObservation(new Source(), capacity); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "capacity restriction");
        }
    }

    private static void Generations()
    {
        var source = new Source();
        using var session = new ChatLogObservation(source);
        session.Start();
        var old = source.History.Last();
        old(41, 1);
        session.Stop();
        old(43, 4);
        Require(session.Snapshot().Length == 1 && source.Active.Count == 0, "stop retains rows and rejects callbacks");
        session.Start();
        var next = source.History.Last();
        old(41, 1);
        next(43, 4);
        Require(session.Snapshot().Length == 2, "old delegate never redirected");
        session.Clear();
        old(41, 1);
        next(43, 4);
        Require(!session.Running && session.Snapshot().Length == 0, "clear stops");
        session.Start();
        var current = source.History.Last();
        current(41, 1);
        Require(session.Snapshot().Single().Sequence == 3, "sequence monotonic across clear");
        session.Dispose();
        current(41, 1);
        Require(!session.Start() && session.Snapshot().Length == 0 && source.Active.Count == 0, "terminal disposal");
    }

    private static void Rollback()
    {
        var source = new Source { FailAttach = true, FailDetach = true };
        var session = new ChatLogObservation(source);
        Require(!session.Start() && session.Faulted && session.CleanupPending, "partial attach retains rollback ownership");
        source.History.Single()(41, 1);
        Require(session.Snapshot().Length == 0 && !session.Start() && source.AttachCalls == 1, "invalid epoch and no duplicate subscription");
        source.FailDetach = false;
        session.Stop();
        source.FailAttach = false;
        Require(!session.CleanupPending && session.Start(), "explicit retry permits restart");
        var current = source.History.Last();
        source.FailDetach = true;
        session.Dispose();
        current(41, 1);
        Require(session.CleanupPending && session.Snapshot().Length == 0, "failed disposal is still closed");
        source.FailDetach = false;
        session.Dispose();
        Require(!session.CleanupPending && source.Active.Count == 0 && !session.Start(), "repeated disposal retries removal");
    }

    private static void Concurrent()
    {
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var source = new Source();
            using var session = new ChatLogObservation(source);
            session.Start();
            var old = source.History.Single();
            using var start = new ManualResetEventSlim(false);
            var producer = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 200; i++)
                    old(41, 1);
            });
            start.Set();
            session.Clear();
            Require(producer.Wait(TimeSpan.FromSeconds(10)), "producer timeout");
            Require(session.Snapshot().Length == 0 && !session.Running, "clear invalidates in-flight producer");
            Parallel.For(0, 20, _ => session.Start());
            Require(source.Active.Count == 1, "serialized start");
            old(43, 4);
            Require(session.Snapshot().Length == 0, "restart rejects stale generation");
            Parallel.For(0, 20, _ => session.Stop());
            Require(source.Active.Count == 0, "serialized stop");
        }
    }

    private sealed class FlySource : IFlyTextObservationSource
    {
        public Action<int, int>? Callback;
        public void Attach(Action<int, int> callback) => this.Callback = callback;
        public void Detach(Action<int, int> callback)
        {
            if (ReferenceEquals(this.Callback, callback))
                this.Callback = null;
        }
    }

    private static void Independent()
    {
        var flySource = new FlySource();
        var logSource = new Source();
        using var fly = new FlyTextObservation(flySource, 512);
        using var log = new ChatLogObservation(logSource);
        fly.Start();
        Require(logSource.AttachCalls == 0, "fly start does not start logs");
        log.Start();
        flySource.Callback!(7, 90822);
        logSource.History.Single()(41, 1);
        log.Clear();
        Require(fly.Running && fly.Snapshot().Single().Val1 == 90822, "log clear does not affect fly data");
        Require(log.Snapshot().Length == 0, "no shared buffer");
    }
}
