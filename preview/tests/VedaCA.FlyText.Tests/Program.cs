using VedaCA.Diagnostics.Core;

namespace VedaCA.FlyText.Tests;

internal static class Program
{
    private sealed class Source : IFlyTextObservationSource
    {
        public readonly List<Action<int, int>> Active = new();
        public readonly List<Action<int, int>> History = new();
        public bool FailBeforeAttach;
        public bool FailAfterAttach;
        public bool FailDetach;
        public int AttachCalls;
        public int DetachCalls;

        public void Attach(Action<int, int> callback)
        {
            this.AttachCalls++;
            if (this.FailBeforeAttach)
                throw new InvalidOperationException("synthetic pre-attach failure");
            this.Active.Add(callback);
            this.History.Add(callback);
            if (this.FailAfterAttach)
                throw new InvalidOperationException("synthetic partial attach failure");
        }

        public void Detach(Action<int, int> callback)
        {
            this.DetachCalls++;
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
            ("only kinds zero through seven and raw values", Filter),
            ("stop and clear revoke stale generations", Generations),
            ("bounded queue drops oldest and snapshots are copies", Bounded),
            ("subscription lifecycle and rollback", Lifecycle),
            ("detach failure retains ownership and rejects callbacks", DetachFailure),
            ("concurrent invalidation and repeated lifecycle", Concurrent),
        ];
        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed; synthetic managed sources only.");
        return failed == 0 ? 0 : 1;
    }

    private static void Filter()
    {
        var source = new Source();
        using var session = new FlyTextObservation(source, 16);
        Require(!session.Running && source.Active.Count == 0, "default must not subscribe");
        Require(session.Start(), "start");
        var callback = source.History.Single();
        foreach (var invalid in new[] { int.MinValue, -1, 8, 9, 255, int.MaxValue })
            callback(invalid, 123);
        for (var kind = 0; kind <= 7; kind++)
            callback(kind, kind == 0 ? int.MinValue : kind == 7 ? int.MaxValue : -kind);
        var rows = session.Snapshot();
        Require(rows.Length == 8, "filter must admit exactly eight selected kinds");
        for (var kind = 0; kind <= 7; kind++)
        {
            var expected = kind == 0 ? int.MinValue : kind == 7 ? int.MaxValue : -kind;
            Require(rows[kind] == new FlyTextObservationRecord(kind + 1, kind, expected), "raw values and local sequence must be preserved");
        }
    }

    private static void Generations()
    {
        var source = new Source();
        using var session = new FlyTextObservation(source, 8);
        session.Start();
        var first = source.History.Last();
        first(0, 10);
        session.Stop();
        Require(source.Active.Count == 0 && !session.Running, "stop detaches");
        first(1, 20);
        session.Start();
        var second = source.History.Last();
        first(2, 30);
        second(3, 40);
        Require(session.Snapshot().Select(x => x.Val1).SequenceEqual(new[] { 10, 40 }), "restart must not revive old callback");
        session.Clear();
        Require(source.Active.Count == 0 && !session.Running, "clear stops and detaches");
        first(0, 50);
        second(0, 60);
        Require(session.Snapshot().Length == 0, "clear must reject all previous generations");
        session.Start();
        var third = source.History.Last();
        second(0, 70);
        third(7, 80);
        Require(session.Snapshot().Single() == new FlyTextObservationRecord(3, 7, 80), "only newly enabled generation may append");
        session.Dispose();
        third(0, 90);
        Require(session.Snapshot().Length == 0 && !session.Start(), "dispose is terminal and clears");
    }

    private static void Bounded()
    {
        var source = new Source();
        using var session = new FlyTextObservation(source, 3);
        session.Start();
        var callback = source.History.Single();
        for (var i = 1; i <= 10; i++)
            callback(4, i);
        var rows = session.Snapshot();
        Require(rows.Select(x => x.Val1).SequenceEqual(new[] { 8, 9, 10 }), "drop oldest in order");
        Require(session.Dropped == 7 && rows.Length == session.Capacity, "bounded count and overflow count");
        rows[0] = default;
        Require(session.Snapshot()[0].Val1 == 8, "snapshot must not expose writable backing storage");
        session.Clear();
        Require(session.Dropped == 0 && session.Snapshot().Length == 0, "clear resets overflow and storage");
    }

    private static void Lifecycle()
    {
        var source = new Source();
        var session = new FlyTextObservation(source, 2);
        Require(session.Start() && session.Start() && source.AttachCalls == 1, "repeated start subscribes once");
        session.Stop();
        session.Stop();
        Require(source.DetachCalls == 1, "repeated stop detaches once");
        session.Start();
        session.Dispose();
        session.Dispose();
        Require(source.Active.Count == 0 && source.DetachCalls == 2 && !session.Start(), "dispose detaches once and prevents restart");

        foreach (var partial in new[] { false, true })
        {
            var failing = new Source { FailBeforeAttach = !partial, FailAfterAttach = partial };
            using var candidate = new FlyTextObservation(failing, 2);
            Require(!candidate.Start(), "failed subscription returns false");
            Require(candidate.Faulted && !candidate.Running && !candidate.CleanupPending, "failed start is closed after rollback");
            Require(failing.Active.Count == 0 && failing.DetachCalls == 1, "attempted attach must be rolled back");
            foreach (var stale in failing.History)
                stale(0, 123);
            Require(candidate.Snapshot().Length == 0, "partial subscription cannot append after rollback");
        }
    }

    private static void DetachFailure()
    {
        var source = new Source();
        using var session = new FlyTextObservation(source, 2);
        session.Start();
        var stale = source.History.Single();
        source.FailDetach = true;
        session.Clear();
        stale(0, 100);
        Require(session.CleanupPending && session.Faulted && !session.Running, "report pending removal without throwing");
        Require(!session.Start() && source.AttachCalls == 1, "pending removal blocks resubscription");
        Require(session.Snapshot().Length == 0, "retained external callback is invalidated");
        source.FailDetach = false;
        session.Stop();
        Require(!session.CleanupPending && source.Active.Count == 0, "explicit retry removes subscription");
        Require(session.Start(), "restart only after successful removal");
        stale(0, 200);
        Require(session.Snapshot().Length == 0, "retry and restart never revive stale callback");

        var partial = new Source { FailAfterAttach = true, FailDetach = true };
        var failed = new FlyTextObservation(partial, 2);
        Require(!failed.Start() && failed.CleanupPending, "partial attach with failed rollback retains ownership");
        failed.Dispose();
        partial.History.Single()(0, 300);
        Require(failed.Snapshot().Length == 0 && !failed.Start(), "failed removal after dispose remains closed");
        partial.FailDetach = false;
        failed.Dispose();
        Require(partial.Active.Count == 0 && !failed.CleanupPending, "repeated dispose retries failed removal");
    }

    private static void Concurrent()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            foreach (var clear in new[] { false, true })
            {
                var source = new Source();
                using var session = new FlyTextObservation(source, 8);
                session.Start();
                var stale = source.History.Single();
                using var start = new ManualResetEventSlim(false);
                var producer = Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < 100; i++)
                        stale(4, i);
                });
                start.Set();
                if (clear)
                    session.Clear();
                else
                    session.Stop();
                var after = session.Snapshot();
                stale(4, 999);
                Require(producer.Wait(TimeSpan.FromSeconds(10)), "producer timeout");
                Require(session.Snapshot().SequenceEqual(after), "callback appended after invalidation returned");
                Require(!clear || after.Length == 0, "clear removes earlier commits");
                Parallel.For(0, 20, _ => session.Start());
                Require(source.Active.Count == 1, "concurrent starts must subscribe once");
                Parallel.For(0, 20, _ => session.Stop());
                Require(source.Active.Count == 0 && !session.Running, "concurrent stops revoke subscription");
                Parallel.For(0, 20, i =>
                {
                    if ((i & 1) == 0)
                        session.Start();
                    else
                        session.Dispose();
                });
                session.Dispose();
                Require(source.Active.Count == 0 && !session.Start(), "start/dispose race is terminal without leaked subscription");
            }
        }
    }
}
