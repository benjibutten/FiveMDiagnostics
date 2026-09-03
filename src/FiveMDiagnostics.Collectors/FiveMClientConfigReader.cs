namespace FiveMDiagnostics.Collectors;

using System.Globalization;
using System.Text.RegularExpressions;

using FiveMDiagnostics.Core;

/// <summary>
/// Reads <c>fivem.cfg</c>, which holds the one graphics setting that is not in the game's settings file.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GameGraphicsSettingsReader"/> said in its own remarks that Extended Texture Budget "is
/// FiveM's own setting and is not in this file", and left it there. It is in this one, as
/// <c>seta "vid_budgetScale" "11"</c>, in the same directory as the settings file the reader was
/// already looking at. Twelve sessions asked which graphics settings the game was running; the answer
/// needed two files side by side and the app read neither.
/// </para>
/// <para>
/// Parsed with a regular expression rather than a config grammar on purpose. The file is several
/// hundred lines of key bindings written by the client, the app needs one line out of it, and a parser
/// for the rest would be code with no reader. A line that does not match is a line this class has no
/// opinion about.
/// </para>
/// </remarks>
public static class FiveMClientConfigReader
{
    /// <summary>
    /// <c>seta "vid_budgetScale" "11"</c>, allowing for quoting the client does not promise to keep.
    /// </summary>
    /// <remarks>
    /// The carriage return before the anchor is the whole of why this class read nothing on a real
    /// machine. <see cref="RegexOptions.Multiline"/> puts <c>$</c> immediately before a line feed and
    /// nowhere else, so on a file with Windows line endings the carriage return sat between the closing
    /// quote and the anchor and the line never matched — every reading fell back to "no such line,
    /// therefore the default budget", which is the wrong budget stated as a fact. Every
    /// <c>fivem.cfg</c> the client writes has CRLF; the only ones without it were the fixtures the
    /// tests were written against, which is the shape of defect a test suite hides rather than catches.
    /// </remarks>
    private static readonly Regex BudgetScalePattern = new(
        """^[ \t]*seta?[ \t]+"?vid_budgetScale"?[ \t]+"?(?<value>-?\d+)"?[ \t]*\r?$""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Reads the newest readable <c>fivem.cfg</c>, or null when there is none.
    /// </summary>
    /// <param name="candidatePaths">
    /// Where to look. Defaults to the machine's own candidates; supplied by tests so they do not read
    /// the configuration of a real install that happens to be on the build machine.
    /// </param>
    public static FiveMClientConfig? Read(IEnumerable<string>? candidatePaths = null)
    {
        var existing = (candidatePaths ?? CandidatePaths())
            .Select(TryStat)
            .OfType<FileInfo>()
            .DistinctBy(info => info.FullName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToArray();

        foreach (var info in existing)
        {
            if (TryRead(info) is { } config)
            {
                return config;
            }
        }

        return null;
    }

    private static FileInfo? TryStat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static FiveMClientConfig? TryRead(FileInfo info)
    {
        string text;
        try
        {
            text = File.ReadAllText(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        int? scale = null;
        try
        {
            if (BudgetScalePattern.Match(text) is { Success: true } match
                && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                scale = value;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological file is the same as no file. The line is a convenience, not a measurement.
            return null;
        }

        return new FiveMClientConfig(
            info.FullName,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            scale);
    }

    /// <summary>Where the client keeps its configuration.</summary>
    private static IEnumerable<string> CandidatePaths()
    {
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            yield return Path.Combine(roamingAppData, "CitizenFX", "fivem.cfg");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "FiveM", "FiveM.app", "fivem.cfg");
        }
    }
}
