using System.Security.Cryptography;
using System.Text;

namespace KarSlides.Core.Preflight;

/// <summary>What the host reads from an opened presentation.</summary>
public sealed record PresentationInfo(
    int SlideCount,
    double SlideWidth,
    double SlideHeight,
    IReadOnlyList<FontUse> Fonts,
    IReadOnlyList<string> LinkedFiles,
    bool HasVideo,
    bool HasAudio,
    bool HasMorph,
    IReadOnlyList<string>? Notes = null); // speaker notes per slide (index = slide - 1)

public sealed record FontUse(string Name, bool Embedded);

/// <summary>Pre-flight result: warnings never block loading, they are shown in the UI and API.</summary>
public sealed record PreflightReport(
    int SlideCount,
    string AspectRatio,
    bool HasVideo,
    bool HasAudio,
    bool HasMorph,
    IReadOnlyList<string> MissingFonts,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> Warnings);

public static class PreflightEvaluator
{
    /// <param name="installedFonts">Font family and full names installed in Windows (case-insensitive set).</param>
    public static PreflightReport Evaluate(PresentationInfo info, ISet<string> installedFonts, double outputAspect,
        Func<string, bool> fileExists)
    {
        var missingFonts = info.Fonts
            .Where(f => !f.Embedded && !installedFonts.Contains(f.Name))
            .Select(f => f.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingFiles = info.LinkedFiles.Where(f => !fileExists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var aspect = info.SlideHeight > 0 ? info.SlideWidth / info.SlideHeight : 0;
        var warnings = new List<string>();
        if (missingFonts.Count > 0)
            warnings.Add($"Fonts not installed and not embedded: {string.Join(", ", missingFonts)}");
        if (missingFiles.Count > 0)
            warnings.Add($"Linked media files not found: {string.Join(", ", missingFiles)}");
        if (Math.Abs(aspect - outputAspect) > 0.01)
            warnings.Add($"Slide aspect ratio {Describe(aspect)} differs from output {Describe(outputAspect)}: black bars");

        return new PreflightReport(info.SlideCount, Describe(aspect), info.HasVideo, info.HasAudio, info.HasMorph,
            missingFonts, missingFiles, warnings);
    }

    private static string Describe(double aspect) => aspect switch
    {
        _ when Math.Abs(aspect - 16.0 / 9) < 0.01 => "16:9",
        _ when Math.Abs(aspect - 4.0 / 3) < 0.01 => "4:3",
        _ when Math.Abs(aspect - 16.0 / 10) < 0.01 => "16:10",
        _ => aspect.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Local copy of a presentation: protects against a pulled USB stick or network hiccups and drops
/// the Mark-of-the-Web, so PowerPoint does not open it in Protected View. One folder per source path, stable
/// across reloads; thumbnails live next to the copy.
/// </summary>
public sealed class FileCache(string root)
{
    public string Root { get; } = root;

    public string FolderFor(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
        return Path.Combine(Root, hash);
    }

    /// <summary>Copies the file into the cache (overwriting) and returns the cached path.</summary>
    public string Prepare(string sourcePath)
    {
        var folder = FolderFor(sourcePath);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, target, overwrite: true);
        RemoveMarkOfTheWeb(target);
        return target;
    }

    public string ThumbnailFolder(string sourcePath) => Path.Combine(FolderFor(sourcePath), "thumbs");

    private static void RemoveMarkOfTheWeb(string path)
    {
        try
        {
            File.Delete(path + ":Zone.Identifier"); // NTFS alternate data stream; no-op if absent
        }
        catch (IOException)
        {
            // FAT/exFAT or network share without streams: nothing to remove
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
