namespace FiveMDiagnostics.Integrations.PresentMon;

public enum PresentMonDiscoveryKind
{
    Missing,
    Configured,
    AutoDetected,
}

public sealed record PresentMonDiscoveryResult(string? ExecutablePath, PresentMonDiscoveryKind Kind);

public static class PresentMonLocator
{
    private const string ExecutableName = "PresentMon.exe";

    public static PresentMonDiscoveryResult Discover(string? configuredPath)
    {
        return Discover(configuredPath, Environment.GetEnvironmentVariable("PATH"), GetDefaultSearchPaths());
    }

    public static PresentMonDiscoveryResult Discover(string? configuredPath, string? pathEnvironmentVariable, IEnumerable<string> additionalSearchPaths)
    {
        var configuredCandidate = NormalizePath(configuredPath);
        if (!string.IsNullOrWhiteSpace(configuredCandidate) && File.Exists(configuredCandidate))
        {
            return new PresentMonDiscoveryResult(configuredCandidate, PresentMonDiscoveryKind.Configured);
        }

        foreach (var candidate in EnumerateCandidates(pathEnvironmentVariable, additionalSearchPaths))
        {
            if (File.Exists(candidate))
            {
                return new PresentMonDiscoveryResult(candidate, PresentMonDiscoveryKind.AutoDetected);
            }
        }

        return new PresentMonDiscoveryResult(null, PresentMonDiscoveryKind.Missing);
    }

    private static IEnumerable<string> EnumerateCandidates(string? pathEnvironmentVariable, IEnumerable<string> additionalSearchPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumeratePathCandidates(pathEnvironmentVariable).Concat(additionalSearchPaths))
        {
            var normalized = NormalizePath(candidate);
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            yield return normalized;

            // Releases ship as PresentMon-2.4.1-x64.exe rather than a plain PresentMon.exe, so probe
            // the containing directory for a versioned build too.
            foreach (var versioned in EnumerateVersionedCandidates(normalized))
            {
                if (seen.Add(versioned))
                {
                    yield return versioned;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateVersionedCandidates(string candidate)
    {
        string? directory;
        try
        {
            directory = Path.GetDirectoryName(candidate);
        }
        catch
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        string[] matches;
        try
        {
            matches = Directory.GetFiles(directory, "PresentMon*.exe", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        // Highest version name last alphabetically is a good enough proxy for "newest".
        Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
        for (var index = matches.Length - 1; index >= 0; index--)
        {
            yield return matches[index];
        }
    }

    private static IEnumerable<string> EnumeratePathCandidates(string? pathEnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(pathEnvironmentVariable))
        {
            yield break;
        }

        foreach (var segment in pathEnvironmentVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(segment, ExecutableName);
        }
    }

    private static IReadOnlyList<string> GetDefaultSearchPaths()
    {
        var candidates = new List<string>();

        AddCandidate(candidates, Environment.SpecialFolder.ProgramFiles, "PresentMon", ExecutableName);
        AddCandidate(candidates, Environment.SpecialFolder.ProgramFilesX86, "PresentMon", ExecutableName);
        AddCandidate(candidates, Environment.SpecialFolder.LocalApplicationData, "Programs", "PresentMon", ExecutableName);
        AddCandidate(candidates, Environment.SpecialFolder.CommonApplicationData, "PresentMon", ExecutableName);

        foreach (var root in new[] { "C:\\Tools\\PresentMon", "C:\\PresentMon" })
        {
            candidates.Add(Path.Combine(root, ExecutableName));
        }

        return candidates;
    }

    private static void AddCandidate(List<string> candidates, Environment.SpecialFolder rootFolder, params string[] segments)
    {
        var root = Environment.GetFolderPath(rootFolder);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        candidates.Add(Path.Combine([root, .. segments]));
    }

    private static string? NormalizePath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        candidate = candidate.Trim().Trim('"');
        if (candidate.Length == 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return candidate;
        }
    }
}