namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;

/// <summary>
/// The session log has to say when the connection went, and which side of the router it points to.
/// </summary>
public sealed class InternetOutageTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 23, 41, 58, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => Start.AddSeconds(seconds);

    private static string Clock(DateTimeOffset moment) => moment.ToLocalTime().ToString("HH:mm:ss");

    [Fact]
    public void AMissedPingOrTwoSaysNothing()
    {
        var tracker = new InternetOutageTracker("1.1.1.1");

        Assert.Null(tracker.Observe(At(0), internetAnswered: false, gatewayAnswered: true));
        Assert.Null(tracker.Observe(At(2), internetAnswered: false, gatewayAnswered: true));
        Assert.Null(tracker.Observe(At(4), internetAnswered: true, gatewayAnswered: true));

        // The count starts over: these two are not the third and fourth in a row.
        Assert.Null(tracker.Observe(At(6), internetAnswered: false, gatewayAnswered: true));
        Assert.Null(tracker.Observe(At(8), internetAnswered: false, gatewayAnswered: true));
    }

    [Fact]
    public void WithTheGatewayUpItPointsPastTheComputer()
    {
        var notice = StartOutage(new InternetOutageTracker("1.1.1.1"), gatewayAnswered: true);

        Assert.Equal(StatusLevel.Warning, notice.Level);
        Assert.Contains("1.1.1.1", notice.Message, StringComparison.Ordinal);
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.BeyondGateway), notice.Message, StringComparison.Ordinal);

        // When it stopped answering, not when three misses had been counted.
        Assert.Contains($"kl. {Clock(At(0))}", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheGatewayDownToo_ItPointsAtThisSideOfTheRouter()
    {
        var notice = StartOutage(new InternetOutageTracker("1.1.1.1"), gatewayAnswered: false);

        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.AtOrBeforeGateway), notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAGatewayItSaysItCannotTell()
    {
        var notice = StartOutage(new InternetOutageTracker("1.1.1.1"), gatewayAnswered: null);

        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.Unknown), notice.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gateway is asked with a 300 ms timeout, so it loses the odd echo. The first version took one miss
    /// in twenty as the gateway being down and turned a fault at the operator into a fault in the computer.
    /// </summary>
    [Fact]
    public void AStrayGatewayMissDoesNotMoveTheFault()
    {
        var tracker = new InternetOutageTracker("1.1.1.1");
        tracker.Observe(At(0), internetAnswered: false, gatewayAnswered: true);
        tracker.Observe(At(2), internetAnswered: false, gatewayAnswered: true);

        // On the very probe that starts the warning, which must not read the latest answer alone.
        var started = tracker.Observe(At(4), internetAnswered: false, gatewayAnswered: false);
        Assert.NotNull(started);
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.BeyondGateway), started.Message, StringComparison.Ordinal);

        for (var second = 6; second < 40; second += 2)
        {
            tracker.Observe(At(second), internetAnswered: false, gatewayAnswered: true);
        }

        var ended = tracker.Observe(At(40), internetAnswered: true, gatewayAnswered: true);
        Assert.NotNull(ended);
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.BeyondGateway), ended.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutageIsSaidOnceAndItsEndGivesTheLength()
    {
        var tracker = new InternetOutageTracker("1.1.1.1");
        StartOutage(tracker, gatewayAnswered: true);

        Assert.Null(tracker.Observe(At(6), internetAnswered: false, gatewayAnswered: true));

        var ended = tracker.Observe(At(30), internetAnswered: true, gatewayAnswered: true);

        Assert.NotNull(ended);
        Assert.Equal(StatusLevel.Info, ended.Level);
        Assert.Contains($"kl. {Clock(At(30))}", ended.Message, StringComparison.Ordinal);
        Assert.Contains("30 s", ended.Message, StringComparison.Ordinal);
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.BeyondGateway), ended.Message, StringComparison.Ordinal);

        Assert.Null(tracker.Observe(At(32), internetAnswered: true, gatewayAnswered: true));
    }

    /// <summary>
    /// A router restarting looks like this: the gateway answers when the internet goes and then goes too.
    /// The first line can only say what it knew, so the last one has to count the whole outage.
    /// </summary>
    [Fact]
    public void TheEndCountsAGatewayThatWentPartWayThrough()
    {
        var tracker = new InternetOutageTracker("1.1.1.1");
        var started = StartOutage(tracker, gatewayAnswered: true);
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.BeyondGateway), started.Message, StringComparison.Ordinal);

        for (var second = 6; second < 16; second += 2)
        {
            tracker.Observe(At(second), internetAnswered: false, gatewayAnswered: false);
        }

        var ended = tracker.Observe(At(16), internetAnswered: true, gatewayAnswered: true);

        Assert.NotNull(ended);
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.AtOrBeforeGateway), ended.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNextOutageIsCountedFromTheStart()
    {
        var tracker = new InternetOutageTracker("1.1.1.1");
        StartOutage(tracker, gatewayAnswered: false);
        tracker.Observe(At(6), internetAnswered: true, gatewayAnswered: true);

        Assert.Null(tracker.Observe(At(60), internetAnswered: false, gatewayAnswered: true));
        Assert.Null(tracker.Observe(At(62), internetAnswered: false, gatewayAnswered: true));
        var second = tracker.Observe(At(64), internetAnswered: false, gatewayAnswered: true);
        Assert.NotNull(second);
        Assert.Contains($"kl. {Clock(At(60))}", second.Message, StringComparison.Ordinal);

        var ended = tracker.Observe(At(70), internetAnswered: true, gatewayAnswered: true);
        Assert.NotNull(ended);
        Assert.Contains("10 s", ended.Message, StringComparison.Ordinal);

        // The gateway's misses from the first outage are not carried into this one.
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.BeyondGateway), ended.Message, StringComparison.Ordinal);
    }

    private static InternetOutageNotice StartOutage(InternetOutageTracker tracker, bool? gatewayAnswered)
    {
        Assert.Null(tracker.Observe(At(0), internetAnswered: false, gatewayAnswered));
        Assert.Null(tracker.Observe(At(2), internetAnswered: false, gatewayAnswered));

        var notice = tracker.Observe(At(4), internetAnswered: false, gatewayAnswered);
        Assert.NotNull(notice);
        return notice;
    }
}
