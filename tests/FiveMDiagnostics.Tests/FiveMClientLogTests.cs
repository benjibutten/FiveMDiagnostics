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

    /// <summary>
    /// The evening of 21 September: eleven model timeouts, zero downloads missing — and the line said
    /// the client never got the files.
    /// </summary>
    /// <remarks>
    /// <c>downloads</c> was <c>DownloadFailure or ModelTimeout</c>, so a timeout alone asserted
    /// "klienten fick aldrig filerna" nine times that evening about a log with no download failure in
    /// it. That is the one sentence in the app that decides where to look next, and it pointed at the
    /// network when the answer was the cache or the disk. The two 09-12/09-21 logs differ on exactly
    /// this: five download failures then, none now, and the same <c>v_9_kitchen_unit</c> timing out both
    /// times — three times then, seven now.
    /// </remarks>
    [Fact]
    public void AModelThatTimedOutWithNoDownloadFailureIsNotCalledAMissingFile()
    {
        var log = Parse(
            Banner,
            "[   2182734] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"",
            "[   8016016] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"1245315447:v_31_walltext016\"");

        var streaming = log.DescribeStreaming();

        Assert.Contains("2 begärdes men kom aldrig", streaming, StringComparison.Ordinal);

        // The false claim, and the false lever that followed from it.
        Assert.DoesNotContain("klienten fick aldrig", streaming, StringComparison.Ordinal);
        Assert.DoesNotContain("Det är nedladdning", streaming, StringComparison.Ordinal);
        Assert.DoesNotContain("Extended Texture Budget rätt reglage", streaming, StringComparison.Ordinal);

        // What the log actually supports: the files arrived and the model still never became available.
        // This log has nothing else in it, so here the elimination is sound.
        Assert.Contains("varken har nedladdningsfel, monteringsfel eller rader om", streaming, StringComparison.Ordinal);
        Assert.Contains("cachen", streaming, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape the evening of 21 September actually had: timeouts and mount failures, no downloads.
    /// </summary>
    /// <remarks>
    /// The first rewrite of this method still named the cache categorically here, because the only guard
    /// on the eliminative sentence was the download flag — and the very next sentence in the same line
    /// said seven resources failed to mount and pointed at the server. Eleven timeouts and seven mount
    /// failures is the real count from <c>CitizenFX_log_2026-09-21T184909.log</c>.
    /// </remarks>
    [Fact]
    public void TimeoutsBesideMountFailuresDoNotEliminateTheMountFailures()
    {
        var log = Parse(
            Banner,
            "[   2182734] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"",
            "[    816266] [b3407_GTAProce] ResourcePlacementThr/ ^3failed loading resources:/cfx-mxc-fib/[audio]/mxc_fib_game.dat in data file mounter^7");

        var streaming = log.DescribeStreaming();

        Assert.Contains("1 begärdes men kom aldrig", streaming, StringComparison.Ordinal);
        Assert.Contains("1 kunde inte monteras", streaming, StringComparison.Ordinal);

        // Both mechanisms keep their own sentence...
        Assert.Contains("Modelltimeouterna säger", streaming, StringComparison.Ordinal);
        Assert.Contains("som driver servern", streaming, StringComparison.Ordinal);

        // ...and neither is eliminated by the other. The log carries something else that can make a
        // model unavailable, so "what is left is the cache" is not a conclusion this file supports.
        Assert.DoesNotContain("står klientens egen strömningsväg kvar", streaming, StringComparison.Ordinal);
        Assert.Contains("räknas var för sig", streaming, StringComparison.Ordinal);
    }

    /// <summary>
    /// A log with files missing <em>and</em> a pool that ran dry must not print both exclusive verdicts.
    /// </summary>
    /// <remarks>
    /// The old chain had a third arm for exactly this ("Båda formerna finns i samma logg … räknas var
    /// för sig"). The first rewrite dropped it, and the two sentences then read "klienten fick aldrig de
    /// filerna" immediately followed by "filerna kom fram och fick inte plats".
    /// </remarks>
    [Fact]
    public void FilesMissingAndAPoolRunningDryDoNotContradictEachOther()
    {
        var log = Parse(
            Banner,
            "[   1226859] [b3407_GTAProce]                24180/ ^3ResourceCacheDevice reporting failure downloading k4mb1_post.ytyp: Failed to connect to 135.125.160.15 port 30120 after 21029 ms: Timeout was reached^7",
            "[   1226860] [b3407_GTAProce]             MainThrd/ ^1Streaming memory ran out while loading module store^7");

        var streaming = log.DescribeStreaming();

        Assert.Contains("Det är nedladdning, inte videominne", streaming, StringComparison.Ordinal);
        Assert.Contains("Extended Texture Budget rätt reglage", streaming, StringComparison.Ordinal);

        // The sentence that used to sit next to "klienten fick aldrig de filerna" and deny it.
        Assert.DoesNotContain("Det är strömningsminnet, inte nätet", streaming, StringComparison.Ordinal);
        Assert.DoesNotContain("filerna kom fram och fick inte plats. Det", streaming, StringComparison.Ordinal);
    }

    /// <summary>
    /// A timeout beside an exhausted pool must not rule the texture budget out and name it in the next
    /// breath.
    /// </summary>
    [Fact]
    public void ATimeoutBesideAnExhaustedPoolDoesNotRuleOutTheBudgetItThenRecommends()
    {
        var log = Parse(
            Banner,
            "[   2182734] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"",
            "[   1226860] [b3407_GTAProce]             MainThrd/ ^1Streaming memory ran out while loading module store^7");

        var streaming = log.DescribeStreaming();

        Assert.Contains("Extended Texture Budget rätt reglage", streaming, StringComparison.Ordinal);

        // The elimination that contradicted it one sentence earlier.
        Assert.DoesNotContain("varken nätet eller texturbudgeten", streaming, StringComparison.Ordinal);
        Assert.DoesNotContain("står klientens egen strömningsväg kvar", streaming, StringComparison.Ordinal);
    }

    /// <summary>
    /// A log carrying only mount failures used to get no mechanism sentence at all.
    /// </summary>
    /// <remarks>
    /// <c>MountFailure</c> was in neither <c>downloads</c> nor <c>pool</c>, so every arm of the chain
    /// was skipped and the reader got a bare count. The same seven files have failed to mount on both
    /// logs this investigation owns, nine days apart — it is the server's content, and nothing on this
    /// machine fixes it.
    /// </remarks>
    [Fact]
    public void AMountFailureGetsItsOwnMechanismRatherThanSilence()
    {
        var log = Parse(
            Banner,
            "[    816266] [b3407_GTAProce] ResourcePlacementThr/ ^3failed loading resources:/cfx-mxc-fib/[audio]/mxc_fib_game.dat in data file mounter^7");

        var streaming = log.DescribeStreaming();

        Assert.Contains("1 kunde inte monteras", streaming, StringComparison.Ordinal);
        Assert.Contains("gick inte att läsa som", streaming, StringComparison.Ordinal);
        Assert.Contains("som driver servern", streaming, StringComparison.Ordinal);

        // A mount failure means the file *was* received, so the download sentence is wrong about it.
        Assert.DoesNotContain("klienten fick aldrig", streaming, StringComparison.Ordinal);

        // And it does not announce itself as "the third thing" when it is the only thing.
        Assert.DoesNotContain("en tredje sak", streaming, StringComparison.Ordinal);
    }

    /// <summary>
    /// All four kinds in one log each get their sentence, because they have four different levers.
    /// </summary>
    [Fact]
    public void EveryMechanismPresentIsNamedRatherThanTheFirstOneWinning()
    {
        var log = Parse(
            Banner,
            "[   1226859] [b3407_GTAProce]                24180/ ^3ResourceCacheDevice reporting failure downloading k4mb1_post.ytyp: Failed to connect to 135.125.160.15 port 30120 after 21029 ms: Timeout was reached^7",
            "[   2182734] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"",
            "[    816266] [b3407_GTAProce] ResourcePlacementThr/ ^3failed loading resources:/cfx-mxc-fib/[audio]/mxc_fib_game.dat in data file mounter^7",
            "[   1226860] [b3407_GTAProce]             MainThrd/ ^1Streaming memory ran out while loading module store^7");

        var streaming = log.DescribeStreaming();

        Assert.Contains("Det är nedladdning, inte videominne", streaming, StringComparison.Ordinal);
        Assert.Contains("Modelltimeouterna säger", streaming, StringComparison.Ordinal);
        Assert.Contains("gick inte att läsa som", streaming, StringComparison.Ordinal);
        Assert.Contains("Extended Texture Budget rätt reglage", streaming, StringComparison.Ordinal);

        // Four mechanisms in one file: nothing is eliminated, and the line says so rather than picking
        // whichever arm happened to be first.
        Assert.Contains("räknas var för sig", streaming, StringComparison.Ordinal);
        Assert.DoesNotContain("står klientens egen strömningsväg kvar", streaming, StringComparison.Ordinal);
    }

    /// <summary>
    /// Repeated failures on one file are counted as lines and as files, not as files alone.
    /// </summary>
    /// <remarks>
    /// The 09-21 log carried eleven timeouts on three models, and the line read "11 begärdes men kom
    /// aldrig (v_9_kitchen_unit, v_31_walltext016, …)" — eleven, next to three names, which reads as
    /// eleven missing files. The distinction matters for this log in particular: one model failing seven
    /// times and seven models failing once are different problems.
    /// </remarks>
    [Fact]
    public void RepeatedFailuresOnOneFileCountLinesAndFilesSeparately()
    {
        var log = Parse(
            Banner,
            "[   2182734] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"",
            "[   4441047] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"",
            "[   8016016] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"1245315447:v_31_walltext016\"");

        var streaming = log.DescribeStreaming();

        Assert.Contains("2 begärdes men kom aldrig, på 3 rader", streaming, StringComparison.Ordinal);
        Assert.Contains("v_9_kitchen_unit", streaming, StringComparison.Ordinal);
        Assert.Contains("v_31_walltext016", streaming, StringComparison.Ordinal);

        // One line per file stays plain — the extra clause is only there when the counts disagree.
        var once = Parse(
            Banner,
            "[   2182734] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"");

        Assert.Contains("1 begärdes men kom aldrig (v_9_kitchen_unit)", once.DescribeStreaming(), StringComparison.Ordinal);
        Assert.DoesNotContain("på 1 rader", once.DescribeStreaming(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The evening of 22 September: the cache emptied before the game started, 6 500 entries written
    /// back during play, and <c>v_9_kitchen_unit</c> timing out three times anyway.
    /// </summary>
    [Fact]
    public void ATimeoutAfterAnEmptiedCacheRulesTheCacheOut()
    {
        var lines = new List<string>
        {
            Banner,
            "[   2931015] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"",
        };
        lines.AddRange(Enumerable.Repeat(
            "[   2923609] [b3407_GTAProce]                29184/ ResourceCache::AddEntry: Saved cache:v1:83aaf34b1fe17ebd7723923827501b7ec4eaaba0 to the index cache.",
            FiveMClientLog.ColdCacheEntries));

        var log = Parse([.. lines]);
        var streaming = log.DescribeStreaming();

        Assert.True(log.StartedWithColdCache);
        Assert.Contains("cachen är inte orsaken", streaming, StringComparison.Ordinal);
        Assert.Contains("Då står disken spelet läses från kvar", streaming, StringComparison.Ordinal);
        Assert.DoesNotContain("cachen, eller disken", streaming, StringComparison.Ordinal);

        // The cache lines are bookkeeping, not failures.
        Assert.Single(log.StreamingFailures);
        Assert.Empty(log.Errors);
    }

    /// <summary>
    /// With the files delivered and every pool quiet, the request itself is still a candidate: a model
    /// asked for while it is not registered times out with nothing wrong on this machine.
    /// </summary>
    [Fact]
    public void TheEliminationKeepsTheRequestItselfAsACandidate()
    {
        var log = Parse(
            Banner,
            "[   2182734] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"");

        var streaming = log.DescribeStreaming();

        Assert.False(log.StartedWithColdCache);
        Assert.Contains("cachen, eller disken spelet läses från", streaming, StringComparison.Ordinal);
        Assert.Contains("själva begäran", streaming, StringComparison.Ordinal);
    }

    /// <summary>The same model two sessions running is not a random read error.</summary>
    [Fact]
    public void AModelThatTimedOutLastSessionTooIsNamedAsARepeat()
    {
        var log = Parse(
            Banner,
            "[   2182734] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"2003410943:v_9_kitchen_unit\"",
            "[   8016016] [b3407_GTAProce]             MainThrd/ ^1Requesting of a model timed out \"1245315447:v_31_walltext016\"");

        var lastSession = new HashSet<string>(["V_9_KITCHEN_UNIT", "v_31_walltext005"], StringComparer.OrdinalIgnoreCase);
        var repeated = log.DescribeStreaming(lastSession);

        Assert.Contains("v_9_kitchen_unit fastnade också i förra sessionens klientlogg", repeated, StringComparison.Ordinal);
        Assert.DoesNotContain("v_31_walltext016 fastnade", repeated, StringComparison.Ordinal);

        // No earlier list, or an earlier session with none, says nothing about repeats.
        Assert.DoesNotContain("förra sessionens", log.DescribeStreaming(), StringComparison.Ordinal);
        Assert.DoesNotContain("förra sessionens", log.DescribeStreaming(new HashSet<string>()), StringComparison.Ordinal);
    }

    private static FiveMClientLog Parse(params string[] lines) =>
        FiveMClientLog.Parse("CitizenFX_log_2026-09-12T210103.log", lines);
}
