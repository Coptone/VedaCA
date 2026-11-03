namespace CritAudio.Diagnostics.Core;

// Equality is only a proposed identity comparison, not proven cross-packet deduplication.
public readonly record struct CastIdentity(ulong Session, ulong Caster, uint GlobalSequence, uint ActionId)
{
    public static CastIdentity From(Effect effect) {
        if (effect is null || !effect.IsValid()) throw new ArgumentException("Invalid normalized effect", nameof(effect));
        return new(effect.Session, effect.Caster, effect.GlobalSequence, effect.ActionId);
    }
}

