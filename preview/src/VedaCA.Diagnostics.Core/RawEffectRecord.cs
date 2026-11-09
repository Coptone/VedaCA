namespace VedaCA.Diagnostics.Core;

// No actor/object IDs, names, chat, pointers or references to game objects.
public readonly record struct RawEffectRecord(
    uint ActionId,
    uint GlobalSequence,
    byte TargetIndex,
    byte EffectIndex,
    byte Type,
    byte Param0,
    byte Param1,
    byte Param2,
    byte Param3,
    byte Param4,
    ushort Value);
