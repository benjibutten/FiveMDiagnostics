namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Collectors;

/// <summary>
/// Six review notes carried "look in Loggboken around 20:01:45" as an action and none of them did it,
/// and on 20 September the game vanished with nobody asking the Application log why. The app runs on
/// that machine and knows the second, so both lookups belong here.
/// </summary>
/// <remarks>
/// These read the machine's real System and Application logs, which is the only thing worth testing:
/// the query string and the permission behaviour are where this breaks, and a fake log would exercise
/// neither. What is asserted is therefore the contract rather than the contents — a line comes back, it
/// names what was asked about, and nothing throws into a running session.
/// </remarks>
public sealed class WindowsEventLogReaderTests
{
    /// <summary>
    /// An empty window says it is empty. The assertion is the word "inga poster" rather than the label,
    /// because every return path here carries the label — including the two that mean the log could not
    /// be read, which is the opposite conclusion and would pass a laxer test.
    /// </summary>
    [Fact]
    public void AWindowWithNothingInItSaysSoRatherThanSayingNothing()
    {
        // A window entirely in the past, so nothing is cut short, and far enough back that a machine
        // has no storage entries in those four minutes.
        var line = WindowsEventLogReader.Describe(
            "F: på 2 406 ms kl. 20:01:45",
            DateTimeOffset.UtcNow.AddYears(-20));

        Assert.Contains("F: på 2 406 ms", line, StringComparison.Ordinal);
        Assert.Contains("inga poster", line, StringComparison.Ordinal);
        Assert.Contains("uppvarvning", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A window whose second half has not happened yet must not report the silence as an answer.
    /// </summary>
    /// <remarks>
    /// The case is a volume stalling as the game closes: the session writes its summaries a second
    /// later, and the entry that would settle the question arrives half a minute after the lookup.
    /// </remarks>
    [Fact]
    public void AWindowThatRunsIntoTheFutureSaysItWasCutShort()
    {
        var line = WindowsEventLogReader.Describe("F: på 2 406 ms", DateTimeOffset.UtcNow);

        Assert.Contains("hann aldrig skrivas", line, StringComparison.Ordinal);
        Assert.DoesNotContain("uppvarvning är då", line, StringComparison.Ordinal);
        Assert.False(WindowsEventLogReader.WindowHasElapsed(DateTimeOffset.UtcNow));
        Assert.True(WindowsEventLogReader.WindowHasElapsed(DateTimeOffset.UtcNow.AddMinutes(-3)));
    }

    /// <summary>
    /// A window over real entries must not throw, whatever the machine's log happens to hold.
    /// </summary>
    /// <remarks>
    /// A day is wide enough that an ordinary machine has storage entries in it and narrow enough that
    /// the query stays cheap. Nothing is asserted about how many there are, because that is the
    /// machine's business and would make the test a weather report.
    /// </remarks>
    [Fact]
    public void ARealWindowIsReadWithoutThrowing()
    {
        var line = WindowsEventLogReader.Describe(
            "senaste dygnet",
            DateTimeOffset.UtcNow.AddHours(-13),
            TimeSpan.FromHours(12));

        Assert.Contains("Windows händelselogg", line, StringComparison.Ordinal);
        Assert.DoesNotContain("kunde inte läsas", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A process that went away with nothing in the Application log says so, and says what that does
    /// and does not rule out.
    /// </summary>
    /// <remarks>
    /// The whole point of the line on 20 September would have been the empty answer: the game vanished,
    /// FiveM wrote no dump, and whether Windows had caught a fault of its own was the only remaining
    /// question. "Nothing there" is worth writing only if it is not read as "it closed normally", so
    /// the caveat is asserted alongside it.
    /// </remarks>
    [Fact]
    public void AnExitWithNothingInTheLogSaysWindowsCaughtNoFault()
    {
        var line = WindowsEventLogReader.DescribeProcessExit(
            "FiveM_b3407_GTAProcess",
            13280,
            DateTimeOffset.UtcNow.AddYears(-20));

        Assert.Contains("FiveM_b3407_GTAProcess (PID 13280)", line, StringComparison.Ordinal);
        Assert.Contains("Applikationsloggen", line, StringComparison.Ordinal);
        Assert.Contains("Systemloggen", line, StringComparison.Ordinal);
        Assert.Contains("Windows fångade alltså inget programfel i den här processen", line, StringComparison.Ordinal);
        Assert.Contains("TerminateProcess", line, StringComparison.Ordinal);
        Assert.DoesNotContain("kunde inte läsas", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The lookup made at the exit itself must not settle the question, because Windows files its error
    /// report seconds after the process dies.
    /// </summary>
    [Fact]
    public void AnExitLookedUpImmediatelyDoesNotClaimWindowsSawNothing()
    {
        var now = DateTimeOffset.UtcNow;
        var line = WindowsEventLogReader.DescribeProcessExit("FiveM_b3407_GTAProcess", 13280, now);

        Assert.Contains("hann aldrig skrivas", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows fångade alltså inget programfel", line, StringComparison.Ordinal);
        Assert.False(WindowsEventLogReader.WindowHasElapsed(now));
    }

    /// <summary>
    /// A bus reset is not spin-up, and the summary sentence must not grade it as "nothing to see".
    /// </summary>
    /// <remarks>
    /// The exact entries from 20 September 22:11:09, which the line ended by calling not an error —
    /// after three notes had already written the same volume's stalls off as a drive spinning up. The
    /// entries really are graded Warning; the sentence has to carry what they say, not what they are
    /// graded as.
    /// </remarks>
    [Fact]
    public void ABusResetIsNotDescribedAsSpinUp()
    {
        var at = new DateTimeOffset(2026, 9, 20, 20, 11, 9, TimeSpan.Zero);
        var line = WindowsEventLogReader.DescribeResets(
        [
            new WindowsEventEntry(at, "UASPStor", 129, "Warning",
                @"Återställning till enheten \Device\RaidPort2 utfärdades."),
            new WindowsEventEntry(at, "disk", 153, "Warning",
                "Ett nytt försök att utföra I/O-åtgärden på den logiska blockadressen 0x17e5f2c0 för disk 2 har utförts."),
            new WindowsEventEntry(at, "disk", 153, "Warning",
                "Ett nytt försök att utföra I/O-åtgärden på den logiska blockadressen 0x60fd58 för disk 2 har utförts."),
        ]);

        Assert.NotNull(line);
        Assert.Contains("1 säger att enheten återställdes", line!, StringComparison.Ordinal);
        Assert.Contains("2 att en I/O-åtgärd fick göras om", line, StringComparison.Ordinal);
        Assert.Contains("inte uppvarvning", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordinary storage chatter is still ordinary: the sentence only fires on entries that say the
    /// device went away.
    /// </summary>
    [Fact]
    public void OrdinaryStorageEntriesDoNotClaimAReset()
    {
        var at = DateTimeOffset.UtcNow;

        Assert.Null(WindowsEventLogReader.DescribeResets(
        [
            new WindowsEventEntry(at, "Ntfs", 98, "Information", "Volume D: (\\Device\\HarddiskVolume4) is healthy."),
            new WindowsEventEntry(at, "Microsoft-Windows-Kernel-PnP", 410, "Information", "Device configured."),
        ]));
    }

    /// <summary>
    /// A real error outranks a reset, a reset outranks ordinary chatter, and the exit window grades at
    /// all.
    /// </summary>
    /// <remarks>
    /// The reset sentence used to be appended to the exit line without asking whether anything was
    /// graded as an error, so a window holding a genuine <c>disk</c> fault next to a reset announced
    /// "none of them is graded as an error" about a set containing one — and that line had no way of
    /// saying "at least one is an error" at all.
    /// </remarks>
    [Fact]
    public void ARealErrorOutranksTheResetSentence()
    {
        var at = DateTimeOffset.UtcNow.AddYears(-20);
        var reset = new WindowsEventEntry(at, "UASPStor", 129, "Warning", "Återställning till enheten utfärdades.");
        var fault = new WindowsEventEntry(at, "disk", 7, "Error", "Enheten har ett felaktigt block.");
        var ordinary = new WindowsEventEntry(at, "Ntfs", 98, "Information", "Volume is healthy.");

        // A fault outranks the reset, on both callers.
        foreach (var quiet in new[] { true, false })
        {
            var graded = WindowsEventLogReader.Grade([reset, fault], quiet);
            Assert.Contains("Minst en är ett fel", graded, StringComparison.Ordinal);
            Assert.DoesNotContain("graderad som fel", graded, StringComparison.Ordinal);
        }

        // Without a fault the reset is what there is to say.
        Assert.Contains("inte uppvarvning", WindowsEventLogReader.Grade([reset], quietWhenOrdinary: true), StringComparison.Ordinal);

        // Ordinary chatter is the disk line's answer and the exit line's silence.
        Assert.Equal(" Ingen av dem är ett fel.", WindowsEventLogReader.Grade([ordinary], quietWhenOrdinary: false));
        Assert.Equal(string.Empty, WindowsEventLogReader.Grade([ordinary], quietWhenOrdinary: true));
    }

    /// <summary>
    /// A window wide enough to hold real entries in both logs must not throw, whatever this machine's
    /// logs happen to contain.
    /// </summary>
    [Fact]
    public void ARealExitWindowReadsBothLogsWithoutThrowing()
    {
        var line = WindowsEventLogReader.DescribeProcessExit(
            "FiveM_b3407_GTAProcess",
            13280,
            DateTimeOffset.UtcNow.AddHours(-13),
            TimeSpan.FromHours(12));

        Assert.Contains("Applikationsloggen", line, StringComparison.Ordinal);
        Assert.Contains("Systemloggen", line, StringComparison.Ordinal);
        Assert.DoesNotContain("kunde inte läsas", line, StringComparison.Ordinal);
    }
}
