namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The two figures the evening of 10 September was read without.
/// </summary>
/// <remarks>
/// That evening produced 61.8 hitches an hour on settings byte-for-byte identical to an evening that
/// produced 167.4, with the time of day controlled for and video memory accounting for a tenth of the
/// difference. The machine had crashed and been restarted beforehand, which made "a freshly booted
/// machine hitches less" the leading explanation — and nothing in the session recorded how long the
/// machine had been up, so it could not be tested. The game's own age was missing for the same reason:
/// the session was attached to a game that had already been running for an unknown time.
/// </remarks>
public sealed class SessionStartAgeTests
{
    private static readonly DateTimeOffset SessionStart = new(2026, 9, 10, 21, 9, 32, TimeSpan.Zero);

    [Fact]
    public void BothAgesAreNamed()
    {
        var line = SessionStartAge.Describe(
            TimeSpan.FromMinutes(134),
            SessionStart.AddMinutes(-63),
            SessionStart);

        Assert.NotNull(line);
        Assert.Contains("2 h 14 min", line, StringComparison.Ordinal);
        Assert.Contains("1 h 3 min", line, StringComparison.Ordinal);
        Assert.Contains("Fast Startup", line, StringComparison.Ordinal);
    }

    /// <summary>Under the hour the hours are noise, and the session that matters here is minutes old.</summary>
    [Fact]
    public void MinutesAloneBelowTheHour()
    {
        var line = SessionStartAge.Describe(TimeSpan.FromMinutes(12), SessionStart.AddMinutes(-4), SessionStart);

        Assert.NotNull(line);
        Assert.Contains("12 min", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" h ", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The session can be started before the game is, which is the difference between "the game is
    /// young" and "there is no game".
    /// </summary>
    [Fact]
    public void AGameThatIsNotRunningIsSaidSo()
    {
        var line = SessionStartAge.Describe(TimeSpan.FromHours(3), gameStartedAt: null, SessionStart);

        Assert.NotNull(line);
        Assert.Contains("spelet kördes inte", line, StringComparison.Ordinal);
    }

    /// <summary>A game that started after the measurement has no age; a negative one would be a bug on show.</summary>
    [Fact]
    public void AGameStartedAfterTheSessionCarriesNoAge()
    {
        var line = SessionStartAge.Describe(TimeSpan.FromHours(3), SessionStart.AddMinutes(5), SessionStart);

        Assert.NotNull(line);
        Assert.DoesNotContain("spelet har kört", line, StringComparison.Ordinal);
        Assert.DoesNotContain("-", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingReadableMeansNoLine()
    {
        Assert.Null(SessionStartAge.Describe(machineUptime: null, gameStartedAt: SessionStart.AddMinutes(5), SessionStart));
    }
}
