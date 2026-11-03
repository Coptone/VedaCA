using CritAudio.Diagnostics.Core;

static class Program
{
    static int count;
    static int failures;
    static Effect Sample() => new(1, 100, 10, 10, 123, 7, 20, 0, 500,
        true, true, EffectKind.Damage, true, true, true, true, Compatibility.SyntheticVerified);
    static void Check(string name, Action test)
    {
        count++;
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); }
    }
    static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    static void Main()
    {
        ModelTests(); FilterTests(); IdentityTests(); RingTests();
        Console.WriteLine($"RESULT tests={count} failures={failures}; synthetic anonymous events only");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
    static void RingTests()
    {
        Check("ring capacity boundaries", () => {
            foreach (var bad in new[] { -1, 0, DiagnosticRing.MaxCapacity + 1 }) {
                try { _ = new DiagnosticRing(bad); }
                catch (ArgumentOutOfRangeException) { continue; }
                throw new Exception("Expected capacity rejection");
            }
            Assert(new DiagnosticRing(DiagnosticRing.MaxCapacity).Count == 0);
        });
        Check("ring empty and insertion order", () => {
            var ring = new DiagnosticRing(3);
            Assert(ring.Count == 0 && ring.Snapshot().Length == 0);
            ring.Add(new(Sample(), Rejection.None));
            ring.Add(new(Sample() with { GlobalSequence = 8 }, Rejection.CritOnly));
            Assert(ring.Count == 2);
            Assert(ring.Snapshot().Select(x => x.Effect.GlobalSequence).SequenceEqual(new uint[] { 7, 8 }));
            Assert(ring.Snapshot()[1].Reason == Rejection.CritOnly);
        });
        Check("ring wraps and overwrites oldest", () => {
            var ring = new DiagnosticRing(3);
            for (uint i = 1; i <= 10; i++) ring.Add(new(Sample() with { GlobalSequence = i }, Rejection.None));
            Assert(ring.Count == 3);
            Assert(ring.Snapshot().Select(x => x.Effect.GlobalSequence).SequenceEqual(new uint[] { 8, 9, 10 }));
        });
        Check("capacity one and snapshot isolation", () => {
            var ring = new DiagnosticRing(1);
            ring.Add(new(Sample(), Rejection.None));
            var copy = ring.Snapshot();
            copy[0] = new(Sample() with { GlobalSequence = 99 }, Rejection.Ordinary);
            Assert(ring.Snapshot()[0].Effect.GlobalSequence == 7);
            ring.Add(new(Sample() with { GlobalSequence = 8 }, Rejection.None));
            Assert(ring.Count == 1 && ring.Snapshot()[0].Effect.GlobalSequence == 8);
        });
        Check("ring rejects invalid entries", () => {
            var ring = new DiagnosticRing(1);
            foreach (var bad in new[] { new DiagnosticEntry(Sample() with { Session = 0 }, Rejection.None),
                new DiagnosticEntry(Sample(), (Rejection)99), new DiagnosticEntry(null!, Rejection.None), null! }) {
                try { ring.Add(bad); }
                catch (ArgumentException) { continue; }
                throw new Exception("Expected entry rejection");
            }
            Assert(ring.Count == 0);
        });
    }
    static void IdentityTests()
    {
        Check("identity equality ignores target effect and observation", () => {
            var e = Sample();
            var first = CastIdentity.From(e);
            var second = CastIdentity.From(e with { Target = 21, EffectIndex = 1, ObservedTicks = 101 });
            Assert(first == second);
            Assert(first.GetHashCode() == second.GetHashCode());
        });
        Check("identity differs for every identity component", () => {
            var e = Sample();
            foreach (var other in new[] { e with { Session = 2 }, e with { Caster = 11 },
                e with { GlobalSequence = 8 }, e with { ActionId = 124 } })
                Assert(CastIdentity.From(e) != CastIdentity.From(other));
        });
        Check("identity rejects invalid values", () => {
            try { CastIdentity.From(Sample() with { Session = 0 }); }
            catch (ArgumentException) { return; }
            throw new Exception("Expected ArgumentException");
        });
    }
    static void FilterTests()
    {
        var verified = CompatibilityContext.CreateOfflineSynthetic();
        void Expect(string name, Effect e, Rejection reason, CompatibilityContext? context = null)
            => Check(name, () => Assert(CandidateFilter.Evaluate(e, context ?? verified).Reason == reason));
        var e = Sample();
        Check("null effect fails closed", () => {
            var decision = CandidateFilter.Evaluate(null!, verified);
            Assert(decision.Reason == Rejection.InvalidValues && !decision.IsCandidate);
        });
        Check("null compatibility context fails closed", () => {
            var decision = CandidateFilter.Evaluate(e, null!);
            Assert(decision.Reason == Rejection.Incompatible && !decision.IsCandidate);
        });
        Expect("same effect both flags", e, Rejection.None);
        Expect("ordinary", e with { Crit = false, Direct = false }, Rejection.Ordinary);
        Expect("crit only", e with { Direct = false }, Rejection.CritOnly);
        Expect("direct only", e with { Crit = false }, Rejection.DirectOnly);
        Expect("zero damage", e with { Damage = 0 }, Rejection.NonPositiveDamage);
        Expect("negative damage", e with { Damage = -1 }, Rejection.NonPositiveDamage);
        foreach (var kind in new[] { EffectKind.Unknown, EffectKind.Healing, EffectKind.DamageOverTime, EffectKind.Reflection })
            Expect("reject effect " + kind, e with { Kind = kind }, Rejection.NotDirectDamage);
        Expect("other player", e with { Caster = 11 }, Rejection.OtherCaster);
        Expect("pet", e with { IsPlayerBody = false }, Rejection.NotPlayerBody);
        Expect("pvp", e with { IsPve = false }, Rejection.NotPve);
        Expect("friendly target", e with { IsEnemy = false }, Rejection.NotEnemy);
        Expect("unselected job action", e with { SelectedForCurrentJob = false }, Rejection.NotSelected);
        Expect("unknown compatibility", e with { Compatibility = Compatibility.Unknown }, Rejection.Incompatible);
        Expect("unverified compatibility", e with { Compatibility = Compatibility.Unverified }, Rejection.Incompatible);
        Expect("unknown context", e, Rejection.Incompatible, CompatibilityContext.Unknown);
        Expect("unverified real version", e, Rejection.Incompatible, CompatibilityContext.Unverified("unverified-client"));
        Expect("invalid input", e with { ActionId = 0 }, Rejection.InvalidValues);
        Check("flags never combined across targets or effects", () => {
            Assert(!CandidateFilter.Evaluate(e with { Direct = false }, verified).IsCandidate);
            Assert(!CandidateFilter.Evaluate(e with { Crit = false, Target = 21, EffectIndex = 1 }, verified).IsCandidate);
        });
        Expect("large ordinary damage does not infer flags", e with { Damage = Effect.MaxDamage, Crit = false, Direct = false }, Rejection.Ordinary);
    }
    static void ModelTests()
    {
        Check("valid bounded values", () => Assert(Sample().IsValid()));
        Check("invalid boundaries", () => {
            var e = Sample();
            foreach (var bad in new[] { e with { Session = 0 }, e with { ObservedTicks = -1 },
                e with { Caster = 0 }, e with { LocalPlayer = 0 }, e with { ActionId = 0 },
                e with { Target = 0 }, e with { EffectIndex = -1 }, e with { EffectIndex = 64 },
                e with { Damage = Effect.MaxDamage + 1 }, e with { Damage = -Effect.MaxDamage - 1 },
                e with { Kind = (EffectKind)99 }, e with { Compatibility = (Compatibility)99 } })
                Assert(!bad.IsValid());
        });
        Check("inclusive boundaries and sequence wrap", () => Assert((Sample() with {
            ObservedTicks = 0, GlobalSequence = 0, EffectIndex = 63, Damage = Effect.MaxDamage }).IsValid()
            && (Sample() with { Damage = -Effect.MaxDamage }).IsValid()));
    }
}



