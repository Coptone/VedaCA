namespace VedaCA.Diagnostics.Core;

public readonly record struct CandidateFlags(bool Critical, bool Direct);

public static class CandidateFlagDecoder
{
    // INTENTIONALLY WRONG. Red-stage demonstration only. Never publish this assembly.
    public static CandidateFlags Decode(byte param0) => new(false, false);
}
