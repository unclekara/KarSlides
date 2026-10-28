using KarSlides.Ndi.Interop;

namespace KarSlides.Ndi;

/// <summary>Public entry for the start-up environment check.</summary>
public static class NdiRuntime
{
    public const string EnvironmentVariable = NdiNative.RuntimeEnvVar;

    /// <summary>Loads Processing.NDI.Lib.x64.dll from NDI_RUNTIME_DIR_V6; throws with a clear message if missing.</summary>
    public static void EnsureLoaded() => NdiNative.EnsureLoaded();

    public static string Version => NdiNative.Version;

    public static string? LoadedFrom => NdiNative.LoadedFrom;
}
