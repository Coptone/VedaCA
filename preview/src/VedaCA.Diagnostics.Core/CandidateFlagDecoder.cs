namespace VedaCA.Diagnostics.Core;

public readonly record struct CandidateFlags(bool Critical, bool Direct);

public static class CandidateFlagDecoder
{
    // Pure interpretation of supplied bits, not a verified game damage decoder.
    public static CandidateFlags Decode(byte param0) =>
        new((param0 & 0x20) != 0, (param0 & 0x40) != 0);
}
