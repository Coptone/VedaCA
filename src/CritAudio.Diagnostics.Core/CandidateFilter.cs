namespace CritAudio.Diagnostics.Core;

public sealed class CompatibilityContext
{
    public string Version { get; }
    internal bool VerifiedSynthetic { get; }
    private CompatibilityContext(string version, bool verified) { Version = version; VerifiedSynthetic = verified; }
    public static CompatibilityContext Unknown { get; } = new("unknown", false);
    public static CompatibilityContext Unverified(string version) => new(version, false);
    // Offline fixture only. This is deliberately not a game-version approval API.
    public static CompatibilityContext CreateOfflineSynthetic() => new("synthetic-fixture-v1", true);
}
public enum Rejection
{
    None, InvalidValues, Incompatible, NotPve, NotPlayerBody, OtherCaster,
    NotEnemy, NotSelected, NotDirectDamage, NonPositiveDamage,
    Ordinary, CritOnly, DirectOnly
}
public readonly record struct Decision(Rejection Reason)
{
    public bool IsCandidate => Reason == Rejection.None;
}
public static class CandidateFilter
{
    public static Decision Evaluate(Effect effect, CompatibilityContext context) {
        if (effect is null || !effect.IsValid()) return new(Rejection.InvalidValues);
        if (context is null || !context.VerifiedSynthetic || effect.Compatibility != Compatibility.SyntheticVerified)
            return new(Rejection.Incompatible);
        if (!effect.IsPve) return new(Rejection.NotPve);
        if (!effect.IsPlayerBody) return new(Rejection.NotPlayerBody);
        if (effect.Caster != effect.LocalPlayer) return new(Rejection.OtherCaster);
        if (!effect.IsEnemy) return new(Rejection.NotEnemy);
        if (!effect.SelectedForCurrentJob) return new(Rejection.NotSelected);
        if (effect.Kind != EffectKind.Damage) return new(Rejection.NotDirectDamage);
        if (effect.Damage <= 0) return new(Rejection.NonPositiveDamage);
        if (!effect.Crit && !effect.Direct) return new(Rejection.Ordinary);
        if (!effect.Direct) return new(Rejection.CritOnly);
        if (!effect.Crit) return new(Rejection.DirectOnly);
        return new(Rejection.None);
    }
}

