namespace VedaCA.Diagnostics.Core;

// These references must identify reviewed evidence for the exact running game
// distribution/build and the exact SDK/backend artifacts. Names, size checks,
// API numbers and user assertions alone are not evidence.
public sealed record VerifiedBuildEvidence(
    string ExactClientBuild,
    string Distribution,
    string Architecture,
    string RuntimeVersionSourceEvidence,
    string SdkArtifactIdentity,
    string ReceiveAddressAndAbiEvidence,
    string HeaderLayoutEvidence,
    string EffectLayoutEvidence,
    string TargetArrayExtentAndLifetimeEvidence,
    string NativeHookLifecycleEvidence);

public static class CaptureSupport
{
    // Intentionally empty. Entries require source review; no config/UI insertion.
    // No current build, SDK version or API number is assumed to be supported.
    private static readonly VerifiedBuildEvidence[] Allowlist = [];

    public static int VerifiedBuildCount => Allowlist.Length;

    // An allowlist entry alone cannot unlock this build: no reviewed native
    // adapter or native lifecycle implementation exists here.
    public static bool CanObserve(string? trustedRuntimeBuild) => false;

    public static string Explain(string? trustedRuntimeBuild)
    {
        if (string.IsNullOrWhiteSpace(trustedRuntimeBuild))
            return "禁止监听：缺少可信运行版本来源；证据 allowlist 为空；原生监听实现未获验证。";
        return "禁止监听：本构建没有受支持的原生监听实现，且证据 allowlist 为空。传入版本字符串不会授权采集。";
    }
}
