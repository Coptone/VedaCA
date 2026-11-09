using VedaCA.Diagnostics.Core;

namespace VedaCA.Safety.Tests;

internal static class Program
{
    private static readonly RawEffectRecord Sample = new(
        1, 2, 0, 0, 1, 0, 0, 0, 0, 0, 3);

    private static int Main()
    {
        (string Name, Action Run)[] tests =
        [
            ("empty allowlist fails closed", FailClosed),
            ("default managed state is paused", DefaultPaused),
            ("pause invalidates old work across resume", PauseRace),
            ("clear invalidates old work", ClearRace),
            ("drain rejects work without waiting", Drain),
            ("foreign and finished leases are rejected", LeaseOwnership),
            ("commit races with pause and clear", ConcurrentInvalidation),
            ("construction rollback at every registration", ConstructionRollback),
            ("cleanup attempts every resource after error", CleanupFailure),
        ];
        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed; managed logic only; no native race validation.");
        return failures == 0 ? 0 : 1;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static ManagedCaptureBuffer.Lease Begin(ManagedCaptureBuffer buffer) =>
        buffer.TryBegin() ?? throw new InvalidOperationException("expected managed lease");

    private static void FailClosed()
    {
        Require(CaptureSupport.VerifiedBuildCount == 0, "allowlist must be empty");
        foreach (var text in new string?[] { null, "", "any", "15", "15.0.3.9", "user-entered-build" })
            Require(!CaptureSupport.CanObserve(text), "a version string must not enable observation");
    }

    private static void DefaultPaused()
    {
        var buffer = new ManagedCaptureBuffer(2);
        Require(buffer.State == ManagedCaptureState.Paused, "default state");
        Require(buffer.TryBegin() == null && buffer.InFlight == 0, "default rejects work");
        buffer.BeginDrain();
        Require(buffer.State == ManagedCaptureState.Stopped, "empty drain stops immediately");
        Require(!buffer.Resume(), "stopped session cannot resume");
    }

    private static void PauseRace()
    {
        var buffer = new ManagedCaptureBuffer(2);
        Require(buffer.Resume(), "resume");
        var old = Begin(buffer);
        Require(buffer.TryCommit(old, Sample), "initial commit");
        buffer.Pause();
        Require(!buffer.TryCommit(old, Sample), "paused callback cannot append");
        Require(buffer.TryBegin() == null, "pause rejects new work");
        Require(buffer.Resume(), "resume after pause");
        Require(!buffer.TryCommit(old, Sample), "resume must not revive old callback");
        buffer.Finish(old);
        var current = Begin(buffer);
        Require(buffer.TryCommit(current, Sample), "new generation can append");
        buffer.Finish(current);
        Require(buffer.Snapshot().Length == 2 && buffer.InFlight == 0, "pause preserves existing records");
    }

    private static void ClearRace()
    {
        var buffer = new ManagedCaptureBuffer(2);
        buffer.Resume();
        var old = Begin(buffer);
        buffer.TryCommit(old, Sample);
        buffer.Clear();
        Require(!buffer.TryCommit(old, Sample), "clear invalidates an active callback");
        Require(buffer.Snapshot().Length == 0, "old callback cannot refill cleared queue");
        buffer.Finish(old);
        var current = Begin(buffer);
        Require(buffer.TryCommit(current, Sample), "new work after clear is permitted");
        buffer.Finish(current);
        Require(buffer.Snapshot().Length == 1, "new generation recorded once");
    }

    private static void Drain()
    {
        var buffer = new ManagedCaptureBuffer(2);
        buffer.Resume();
        var first = Begin(buffer);
        var second = Begin(buffer);
        buffer.TryCommit(first, Sample);
        buffer.BeginDrain();
        Require(buffer.State == ManagedCaptureState.Draining && buffer.InFlight == 2, "drain returns with active leases");
        Require(buffer.TryBegin() == null && !buffer.Resume(), "drain rejects entry and resume");
        Require(!buffer.TryCommit(first, Sample), "drain rejects old commits");
        buffer.Clear();
        buffer.Pause();
        Require(buffer.State == ManagedCaptureState.Draining, "clear/pause cannot cancel drain");
        buffer.Finish(first);
        buffer.Finish(first);
        Require(buffer.InFlight == 1, "finish is idempotent");
        buffer.Finish(second);
        Require(buffer.State == ManagedCaptureState.Stopped && buffer.InFlight == 0, "last lease completes managed drain");
        buffer.BeginDrain();
        Require(buffer.Snapshot().Length == 0 && !buffer.Resume(), "terminal state stays empty");
    }

    private static void LeaseOwnership()
    {
        var first = new ManagedCaptureBuffer(2);
        var second = new ManagedCaptureBuffer(2);
        first.Resume();
        second.Resume();
        var lease = Begin(first);
        Require(!second.TryCommit(lease, Sample), "foreign lease rejected");
        second.Finish(lease);
        Require(first.InFlight == 1 && second.InFlight == 0, "foreign finish has no effect");
        first.Finish(lease);
        Require(!first.TryCommit(lease, Sample), "finished lease rejected");
    }

    private static void ConcurrentInvalidation()
    {
        // Each iteration starts the producer and invalidator together. A commit
        // linearized before Pause may remain; Clear must remove it. A commit
        // linearized after either invalidation must be rejected.
        for (var iteration = 0; iteration < 200; iteration++)
        {
            foreach (var clear in new[] { false, true })
            {
                var buffer = new ManagedCaptureBuffer(4);
                buffer.Resume();
                var lease = Begin(buffer);
                using var start = new ManualResetEventSlim(false);
                var producer = Task.Run(() =>
                {
                    start.Wait();
                    for (var attempt = 0; attempt < 20; attempt++)
                        buffer.TryCommit(lease, Sample);
                });
                start.Set();
                if (clear)
                    buffer.Clear();
                else
                    buffer.Pause();
                var countAfterInvalidation = buffer.Snapshot().Length;
                Require(!buffer.TryCommit(lease, Sample), "stale commit rejected after invalidation returns");
                Require(producer.Wait(TimeSpan.FromSeconds(10)), "managed producer did not finish");
                Require(buffer.Snapshot().Length == countAfterInvalidation, "old producer appended after invalidation");
                if (clear)
                    Require(countAfterInvalidation == 0, "clear leaves no prior-generation data");
                buffer.Finish(lease);
                buffer.BeginDrain();
                Require(buffer.InFlight == 0 && buffer.State == ManagedCaptureState.Stopped, "managed stress drain");
            }
        }
    }

    private static void ConstructionRollback()
    {
        for (var failingStage = 0; failingStage < 4; failingStage++)
        {
            foreach (var failAfterAttach in new[] { false, true })
            {
                var scope = new ResourceScope();
                var attached = new HashSet<int>();
                var released = new List<int>();
                var failed = false;
                try
                {
                    for (var stage = 0; stage < 4; stage++)
                    {
                        var index = stage;
                        scope.Register(
                            () =>
                            {
                                if (index == failingStage && !failAfterAttach)
                                    throw new InvalidOperationException("synthetic acquisition failure");
                                attached.Add(index);
                                if (index == failingStage)
                                    throw new InvalidOperationException("synthetic partial acquisition failure");
                            },
                            () =>
                            {
                                if (attached.Remove(index))
                                    released.Add(index);
                            });
                    }
                }
                catch (InvalidOperationException)
                {
                    failed = true;
                }
                finally
                {
                    scope.Dispose();
                }
                Require(failed && attached.Count == 0, "construction failure leaked a registration");
                var acquiredCount = failingStage + (failAfterAttach ? 1 : 0);
                Require(released.SequenceEqual(Enumerable.Range(0, acquiredCount).Reverse()), "rollback must be reverse acquisition order");
                scope.Dispose();
                Require(released.Count == acquiredCount, "cleanup must be idempotent");
            }
        }
    }

    private static void CleanupFailure()
    {
        var scope = new ResourceScope();
        var released = new List<int>();
        for (var stage = 0; stage < 3; stage++)
        {
            var index = stage;
            scope.Register(() => { }, () =>
            {
                released.Add(index);
                if (index == 1)
                    throw new InvalidOperationException("synthetic detach failure");
            });
        }
        var reported = false;
        try
        {
            scope.Dispose();
        }
        catch (AggregateException error)
        {
            reported = error.InnerExceptions.Count == 1;
        }
        Require(reported && released.SequenceEqual(new[] { 2, 1, 0 }), "cleanup must attempt remaining releases and report failure");
        scope.Dispose();
        Require(released.Count == 3, "failed cleanup is not repeated implicitly");
    }
}
