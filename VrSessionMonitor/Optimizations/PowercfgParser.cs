// VrSessionMonitor/Optimizations/PowercfgParser.cs
using System.Text.RegularExpressions;

namespace VrSessionMonitor.Optimizations;

/// <summary>Pure text parsing for powercfg.exe's stdout formats — kept separate from the process
/// invocation (PowercfgRunner) so the parsing logic is unit-testable without shelling out.</summary>
public static partial class PowercfgParser
{
    [GeneratedRegex(@"Power Scheme GUID:\s*([0-9a-fA-F-]{36})\s*\(([^)]+)\)")]
    private static partial Regex SchemeLineRegex();

    [GeneratedRegex(@"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)")]
    private static partial Regex AcValueIndexRegex();

    /// <summary>Parses `powercfg /getactivescheme` output.</summary>
    public static string? ParseActiveSchemeGuid(string getActiveSchemeOutput)
    {
        var m = SchemeLineRegex().Match(getActiveSchemeOutput);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Parses `powercfg /list` output, matching a scheme by its display name (e.g.
    /// "Ultimate Performance", "AMD Ryzen Balanced"), case-insensitively.</summary>
    public static string? FindSchemeGuidByName(string listOutput, string schemeName)
    {
        foreach (Match m in SchemeLineRegex().Matches(listOutput))
        {
            if (string.Equals(m.Groups[2].Value.Trim(), schemeName, StringComparison.OrdinalIgnoreCase))
                return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Parses `powercfg /q SCHEME_CURRENT &lt;subgroup&gt; &lt;setting&gt;` output for the
    /// "Current AC Power Setting Index" line.</summary>
    public static int? ParseCurrentAcValueIndex(string queryOutput)
    {
        var m = AcValueIndexRegex().Match(queryOutput);
        return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : null;
    }
}
