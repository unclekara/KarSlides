using System.Xml.Linq;
using KarSlides.Core.Model;

namespace KarSlides.Vmix;

public sealed record VmixInput(int Number, string Key, string Type, string Title);

public sealed record VmixState(string Version, IReadOnlyList<VmixInput> Inputs);

/// <summary>Pure parsing of vMix TCP and XML API answers (unit-tested).</summary>
public static class VmixProtocol
{
    /// <summary>
    /// "TALLY OK 0121" -> per input (1-based, index 0 = input 1): '0' off, '1' program, '2' preview.
    /// Null if the line is not a tally line.
    /// </summary>
    public static string? ParseTally(string line)
    {
        const string prefix = "TALLY OK ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var digits = line[prefix.Length..].Trim();
        return digits.All(c => c is >= '0' and <= '2') ? digits : null;
    }

    public static Tally TallyOf(string tally, int inputNumber)
    {
        if (inputNumber < 1 || inputNumber > tally.Length) return Tally.None;
        return tally[inputNumber - 1] switch
        {
            '1' => new Tally(OnProgram: true, OnPreview: false),
            '2' => new Tally(OnProgram: false, OnPreview: true),
            _ => Tally.None,
        };
    }

    /// <summary>Parses /api XML. Throws <see cref="FormatException"/> if the answer is not vMix.</summary>
    public static VmixState ParseState(string xml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new FormatException($"Not vMix XML: {Snippet(xml)}", ex);
        }
        if (root.Name.LocalName != "vmix" || root.Element("version") is null)
            throw new FormatException($"Not vMix XML (root <{root.Name.LocalName}>): {Snippet(xml)}");

        var inputs = root.Element("inputs")?.Elements("input").Select(e => new VmixInput(
            int.TryParse((string?)e.Attribute("number"), out var n) ? n : 0,
            (string?)e.Attribute("key") ?? "",
            (string?)e.Attribute("type") ?? "",
            (string?)e.Attribute("title") ?? e.Value)).ToArray() ?? [];
        return new VmixState(root.Element("version")!.Value, inputs);
    }

    /// <summary>
    /// The vMix input that shows our NDI source: type NDI and the title contains the source name, which vMix
    /// shows as "NDI MACHINE (KarSlides - Slot 1)". The first one wins if the source is added twice.
    /// </summary>
    public static VmixInput? FindNdiInput(VmixState state, string ndiName) =>
        state.Inputs.Where(i => i.Type.Equals("NDI", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(i => i.Title.Contains($"({ndiName})", StringComparison.OrdinalIgnoreCase)
                                 || i.Title.EndsWith(ndiName, StringComparison.OrdinalIgnoreCase));

    private static string Snippet(string s) => s.Length <= 80 ? s : s[..80] + "...";
}
