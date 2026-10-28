using System.Reflection;
using System.Text.RegularExpressions;

namespace KarSlides.Core;

/// <summary>
/// Product version and authorship, from the assembly attributes that Directory.Build.props sets for
/// every project: one version for the whole product, the git commit appended by the SDK.
/// </summary>
public sealed record ProductInfo(string Version, string Commit, string Author, string Email, string Copyright)
{
    public static ProductInfo Current { get; } = Read(typeof(ProductInfo).Assembly);

    /// <summary>"0.4.0+abc1234", or just "0.4.0" when built outside git.</summary>
    public string Display => Commit.Length > 0 ? $"{Version}+{Commit}" : Version;

    /// <summary>
    /// Required by the NDI SDK license (SDK documentation, section 3) in the About box and wherever NDI is used,
    /// together with a link to <see cref="NdiUrl"/>.
    /// </summary>
    public string NdiNotice => "NDI® is a registered trademark of Vizrt NDI AB";

    public string NdiUrl => "https://ndi.video/";

    public static ProductInfo Read(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = info.IndexOf('+');
        var version = plus < 0 ? info : info[..plus];
        var commit = plus < 0 ? "" : info[(plus + 1)..];
        if (commit.Length > 7) commit = commit[..7];
        var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
        var email = Regex.Match(copyright, @"<([^>]+)>").Groups[1].Value;
        var author = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";
        return new ProductInfo(version, commit, author, email, copyright);
    }
}
