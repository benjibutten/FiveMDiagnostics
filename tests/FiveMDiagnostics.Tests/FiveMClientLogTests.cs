namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The file that held the answer nobody read.
/// </summary>
/// <remarks>
/// On 20 September a texture budget was raised a step because MLOs would not load, the raise did not
/// fix them, and the investigation had no way to say whether the slider was even the right control. The
/// client's own log from 12 September — collected by hand, to date a crash, and then left alone — says
/// it outright: <c>ResourceCacheDevice reporting failure downloading k4mb1_post.ytyp: Failed to connect
/// to … port 30120</c>. A <c>.ytyp</c> is an MLO's type definition, and it never arrived over the
/// network. No texture budget holds a file the client does not have.
///
/// Every line below is copied verbatim out of that log, CRLF and colour codes included, because the
/// two things this parser can get wrong are line endings and the client's own formatting.
/// </remarks>
public sealed class FiveMClientLogTests
{
    /// <summary>The log's banner, which is the only wall clock in the file.</summary>
    private const string Banner =
        "[        47] [         FiveM]             MainThrd/ --- BEGIN LOGGING AT Sat Sep 12 21:01:04 2026 ---";

    /// <summary>
    /// A download that timed out is a download that timed out, and the line says the budget is the wrong
    /// lever rather than leaving the reader to work it out from a file extension.
    /// </summary>
    [Fact]
    public void AnAssetThatNeverArrivedIsNotAVideoMemoryProblem()
    {
        var log = Parse(
            Banner,
            "[   1226859] [b3407_GTAProce]                24180/ ^3ResourceCacheDevice reporting failure downloading k4mb1_post.ytyp: Failed to connect to 135.125.160.15 port 30120 after 21029 ms: Timeout was reached^7",
            "[   1349211] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"");

        var streaming = log.DescribeStreaming();

        Assert.Contains("1 kunde inte laddas ned från servern (k4mb1_post.ytyp)", streaming, StringComparison.Ordinal);
        Assert.Contains("1 begärdes men kom aldrig (v_9_kitchen_unit)", streaming, StringComparison.Ordinal);
        Assert.Contains("Det är nedladdning, inte videominne", streaming, StringComparison.Ordinal);
        Assert.Contains(".ytyp", streaming, StringComparison.Ordinal);

        // And the colour codes stay on the console where they belong.
        Assert.DoesNotContain("^3", streaming, StringComparison.Ordinal);
        Assert.DoesNotContain("^7", streaming, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pool running dry is the other mechanism, and it is the one the slider actually governs. The
    /// line has to say which of the two it saw.
    /// </summary>
    /// <remarks>
    /// No log in this investigation has ever carried such a line, so its wording is a guess — which is
    /// why anything coloured as an error and matched by nothing is still kept. See
    /// <see cref="AnUnrecognisedErrorIsKeptRatherThanFiltered"/>.
    /// </remarks>
    [Fact]
    public void APoolRunningDryIsToldApartFromADownloadThatFailed()
    {
        var log = Parse(
            Banner,
            "[   1226859] [b3407_GTAProce]             MainThrd/ ^1Streaming memory ran out while loading module store^7");

        var streaming = log.DescribeStreaming();

        Assert.Contains("rapporterades som slut på strömningsminne", streaming, StringComparison.Ordinal);
        Assert.Contains("Där är Extended Texture Budget rätt reglage", streaming, StringComparison.Ordinal);
        Assert.DoesNotContain("Det är nedladdning", streaming, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Lua stack overflow is not an exhausted streaming pool.
    /// </summary>
    /// <remarks>
    /// The pool vocabulary matched "overflow" and "ran out of" anywhere in any line, ahead of the
    /// SCRIPT ERROR check and outside the colour gate. An ordinary <c>SCRIPT ERROR: … stack overflow</c>
    /// was therefore filed as an exhausted pool, and the streaming line then said "Där är Extended
    /// Texture Budget rätt reglage" — the exact wrong-lever advice this class exists to prevent.
    /// </remarks>
    [Fact]
    public void ALuaStackOverflowIsNotAnExhaustedPool()
    {
        var log = Parse(
            Banner,
            "[   1226859] [b3407_GTAProce]             MainThrd/ ^1SCRIPT ERROR: @tp-hud/client/main.lua:88: stack overflow^7",
            "[   1226860] [b3407_GTAProce]             MainThrd/ ^1Error: ran out of patience waiting for something^7");

        Assert.Empty(log.StreamingFailures);
        Assert.DoesNotContain("Extended Texture Budget rätt reglage", log.DescribeStreaming(), StringComparison.Ordinal);
        Assert.Contains("inga misslyckade nedladdningar", log.DescribeStreaming(), StringComparison.Ordinal);

        // Neither line is lost; they are simply the errors they are.
        Assert.Equal(2, log.Errors.Count);
        Assert.Contains(log.Errors, entry => entry.Kind == FiveMClientLogKind.ScriptError);
    }

    /// <summary>
    /// A quiet log says it is quiet, because "the app printed nothing" and "the client reported nothing"
    /// are different facts and only one of them is evidence.
    /// </summary>
    [Fact]
    public void AQuietLogSaysSoRatherThanSayingNothing()
    {
        var log = Parse(Banner, "[      1312] [   ROSLauncher]             MainThrd/ Found launcher!");

        Assert.Empty(log.StreamingFailures);
        Assert.Contains("inga misslyckade nedladdningar", log.DescribeStreaming(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The server's resource list, and the diff that answers the question this machine cannot measure.
    /// </summary>
    [Fact]
    public void TheServersResourceListIsComparedAgainstTheLastSession()
    {
        var log = Parse(
            Banner,
            "[     92578] [b3407_GTAProce]  UV loop: httpClient/ Required resources: sessionmanager spawnmanager prison-mlo casino-interior vegetation");

        Assert.Equal(5, log.Resources.Count);

        var previous = new HashSet<string>(
            ["sessionmanager", "spawnmanager", "vegetation", "gammal_resurs"],
            StringComparer.OrdinalIgnoreCase);

        var line = log.DescribeResources(previous);

        Assert.Contains("2 tillkom (prison-mlo, casino-interior)", line, StringComparison.Ordinal);
        Assert.Contains("1 föll bort (gammal_resurs)", line, StringComparison.Ordinal);

        // An unchanged server says so too, or "no diff line" and "no diff" read the same.
        var unchanged = log.DescribeResources(new HashSet<string>(log.Resources, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("Exakt samma lista som förra sessionen", unchanged, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure wordings above were read off one evening's log. A failure this parser has never seen
    /// must reach the session log anyway.
    /// </summary>
    [Fact]
    public void AnUnrecognisedErrorIsKeptRatherThanFiltered()
    {
        var log = Parse(
            Banner,
            "[   1226859] [b3407_GTAProce]             MainThrd/ ^1Something nobody has written a pattern for^7",
            "[   1226860] [b3407_GTAProce]             MainThrd/ ordinary chatter nobody coloured");

        var entry = Assert.Single(log.Errors);
        Assert.Equal(FiveMClientLogKind.Other, entry.Kind);
        Assert.Equal("Something nobody has written a pattern for", entry.Text);

        Assert.Contains("Something nobody has written a pattern for", log.DescribeErrors()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A script error's stack is not six more errors.
    /// </summary>
    /// <remarks>
    /// One failing resource on 12 September wrote a message and five frames, all coloured red. Counted
    /// separately they filled the line and pushed twenty other variants off it.
    /// </remarks>
    [Fact]
    public void AScriptErrorsStackIsNotCountedAsMoreErrors()
    {
        var log = Parse(
            Banner,
            "[   1226859] [b3407_GTAProce]             MainThrd/ ^1SCRIPT ERROR: @vehicles/client/functions.lua:13: statebag timed out while awaiting entity creation!^7",
            "[   1226860] [b3407_GTAProce]             MainThrd/ ^1> waitFor (@ox_lib/imports/waitFor/client.lua:25)^7",
            "[   1226861] [b3407_GTAProce]             MainThrd/ ^1> ref (@vehicles/client/functions.lua:29)^7");

        var entry = Assert.Single(log.Errors);
        Assert.Equal(FiveMClientLogKind.ScriptError, entry.Kind);
    }

    /// <summary>
    /// Every line carries milliseconds since the client started and nothing else, so the banner is what
    /// makes a line comparable with a hitch.
    /// </summary>
    /// <remarks>
    /// The banner is UTC: the 12 September log opens at 21:01:04 and the app's own capture line puts the
    /// same moment at 23:01:19 local.
    /// </remarks>
    [Fact]
    public void TheBannerTurnsOffsetsIntoWallClock()
    {
        var log = Parse(
            Banner,
            "[    600000] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"1:v_9_kitchen_unit\"");

        Assert.Equal(new DateTimeOffset(2026, 9, 12, 21, 1, 4, TimeSpan.Zero), log.StartedAtUtc);

        var entry = Assert.Single(log.StreamingFailures);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 21, 11, 4, TimeSpan.Zero), entry.At);
    }

    /// <summary>
    /// Without the banner the offsets are all there is, and a line says so rather than inventing a date.
    /// </summary>
    [Fact]
    public void WithoutABannerTheOffsetIsReportedAsAnOffset()
    {
        var log = Parse(
            "[    600000] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"1:v_9_kitchen_unit\"");

        Assert.Null(log.StartedAtUtc);

        var entry = Assert.Single(log.StreamingFailures);
        Assert.Null(entry.At);
        Assert.StartsWith("+00:10:00", entry.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The client writes CRLF, and a parser tested only against LF is a parser that reads nothing on the
    /// machine it was written for. <c>FiveMClientConfigReader</c> shipped exactly that bug.
    /// </summary>
    [Fact]
    public void WindowsLineEndingsAreRead()
    {
        var text = Banner + "\r\n"
            + "[   1226859] [b3407_GTAProce]                24180/ ^3ResourceCacheDevice reporting failure downloading k4mb1_post.ytyp: Failed to connect^7\r\n";

        var log = FiveMClientLog.Parse("CitizenFX_log.log", text.Split('\n').Select(line => line.TrimEnd('\r')));

        Assert.NotNull(log.StartedAtUtc);
        Assert.Single(log.StreamingFailures);
    }

    private static FiveMClientLog Parse(params string[] lines) =>
        FiveMClientLog.Parse("CitizenFX_log_2026-09-12T210103.log", lines);
}
