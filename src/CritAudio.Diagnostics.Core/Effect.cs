namespace CritAudio.Diagnostics.Core;

public enum EffectKind { Unknown, Damage, Healing, DamageOverTime, Reflection }
public enum Compatibility { Unknown, Unverified, SyntheticVerified }
public sealed record Effect(
    ulong Session, long ObservedTicks, ulong Caster, ulong LocalPlayer,
    uint ActionId, uint GlobalSequence, ulong Target, int EffectIndex,
    long Damage, bool Crit, bool Direct, EffectKind Kind,
    bool IsPve, bool IsEnemy, bool IsPlayerBody, bool SelectedForCurrentJob,
    Compatibility Compatibility)
{
    public const int MaxEffectIndex = 63;
    public const long MaxDamage = uint.MaxValue;
    public bool IsValid() => Session != 0 && ObservedTicks >= 0 && Caster != 0 && LocalPlayer != 0 && ActionId != 0 && Target != 0 && EffectIndex >= 0 && EffectIndex <= MaxEffectIndex && Damage >= -MaxDamage && Damage <= MaxDamage && Enum.IsDefined(typeof(EffectKind), Kind) && Enum.IsDefined(typeof(Compatibility), Compatibility);
}

