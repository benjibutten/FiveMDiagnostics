namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Collectors;

/// <summary>
/// Six review notes carried "look in Loggboken around 20:01:45" as an action and none of them did it.
/// The app runs on that machine and knows the second, so the lookup belongs here.
/// </summary>
/// <remarks>
/// These read the machine's real System log, which is the only thing worth testing: the query string
/// and the permission behaviour are where this breaks, and a fake log would exercise neither. What is
/// asserted is therefore the contract rather than the contents — a line comes back, it names what was
/// asked about, and nothing throws into a running session.
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
}
