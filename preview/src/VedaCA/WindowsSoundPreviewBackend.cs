using System;
using System.Runtime.InteropServices;

namespace VedaCA;

/// <summary>OS sound interface only; no game hooks or external sound files.</summary>
internal static class WindowsSoundPreviewBackend
{
    private const uint MbIconAsterisk = 0x00000040;

    // Called only by SoundPreviewController.Preview after an explicit UI click.
    internal static bool Play()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return MessageBeep(MbIconAsterisk);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);
}
