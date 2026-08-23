namespace FiveMDiagnostics.Integrations.Obs;

using FiveMDiagnostics.Core;

/// <summary>
/// Turns an imported OBS log into the render and encoding lag figures the WebSocket keeps not supplying.
/// </summary>
/// <remarks>
/// <para>
/// Registered ahead of the generic log parser, which also claims <c>.txt</c> and would otherwise reduce
/// an OBS log to a keyword grep. <see cref="CanParse"/> is therefore deliberately narrow: it reads the
/// head of the file and requires OBS's own banner, so a file that merely lives in the same folder falls
/// through to the generic parser rather than being misreported as OBS telemetry.
/// </para>
/// <para>
/// This is how the data actually arrives. OBS writes its session totals when an output stops, which in
/// practice is after the diagnostics session has already been stopped — in the session that prompted
/// this, the app stopped at 02:43 and OBS wrote its totals at 02:44:47. Reading the log live would have
/// found nothing; importing it afterwards finds everything.
/// </para>
/// </remarks>
public sealed class ObsLogArtifactParser : IArtifactParser
{
    /// <summary>Lines of the head to inspect for OBS's banner. The version line lands within the first few.</summary>
    private const int BannerLinesToInspect = 40;

    public bool CanParse(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            for (var i = 0; i < BannerLinesToInspect; i++)
            {
                if (reader.ReadLine() is not { } line)
                {
                    return false;
                }

                if (line.Contains("OBS ", StringComparison.Ordinal) && line.Contains("(64-bit,", StringComparison.Ordinal))
                {
                    return true;
                }

                if (line.Contains("[obs-browser]", StringComparison.Ordinal) || line.Contains("[obs-nvenc", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var summary = ObsSessionLogReader.TryReadFile(path);
        var attachment = new ArtifactAttachment(path, ArtifactKind.LogFile, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true);

        if (summary is null)
        {
            // An OBS log with no totals is the normal shape of a log from a stream that was still running,
            // and saying so is more useful than saying nothing was found.
            return Task.FromResult<ArtifactParseResult?>(new ArtifactParseResult(
                attachment,
                [
                    new ArtifactEvidence(
                        DateTimeOffset.UtcNow,
                        ArtifactKind.LogFile,
                        "OBS-logg importerad, men den innehåller inga avslutningssiffror för rendering eller encoding. "
                        + "OBS skriver dem först när utdatan stoppas — importera loggen igen efter att streamen avslutats.",
                        new Dictionary<string, double>(),
                        path),
                ],
                []));
        }

        var metrics = new Dictionary<string, double>();
        if (summary.RenderLagShare is { } render)
        {
            metrics["obsRenderLagPercent"] = render * 100;
        }

        if (summary.EncodingLagShare is { } encoding)
        {
            metrics["obsEncodingLagPercent"] = encoding * 100;
        }

        if (summary.LaggedRenderFrames is { } lagged)
        {
            metrics["obsLaggedRenderFrames"] = lagged;
        }

        if (summary.SkippedEncodingFrames is { } skipped)
        {
            metrics["obsSkippedEncodingFrames"] = skipped;
        }

        return Task.FromResult<ArtifactParseResult?>(new ArtifactParseResult(
            attachment,
            [new ArtifactEvidence(DateTimeOffset.UtcNow, ArtifactKind.LogFile, summary.Describe(), metrics, path)],
            []));
    }
}
