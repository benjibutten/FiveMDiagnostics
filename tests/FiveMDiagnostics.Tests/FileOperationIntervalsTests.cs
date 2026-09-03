namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// When the neighbour was over the bar, which is the half of the file system signal that was missing.
/// </summary>
/// <remarks>
/// The parser counted every operation in the retained window and divided by its length. That answers
/// "was somebody hammering the file system while this file was recording", and the analysis was reading
/// it as "was somebody hammering the file system while these frames were lost" — two different
/// questions on a ring buffer that holds tens of seconds against an incident window of ninety. The
/// counts are bucketed by second now, and the seconds over the bar are reported as intervals a consumer
/// can compare against a window.
/// </remarks>
public sealed class FileOperationIntervalsTests
{
    private static readonly DateTime Start = new(2026, 9, 2, 21, 14, 3, DateTimeKind.Utc);

    private const int Indexer = 5672;
    private const int Game = 31076;

    /// <summary>
    /// A burst in the middle of a quiet trace is reported where it happened, not spread over the file.
    /// </summary>
    [Fact]
    public void ABurstIsReportedWhereItHappened()
    {
        // Twenty seconds of ordinary running, five of indexing, twenty more of quiet.
        var attribution = Play(
            (From: 0, Seconds: 45, Process: Game, PerSecond: 2_400),
            (From: 20, Seconds: 5, Process: Indexer, PerSecond: 48_000));

        var summary = attribution.Summarize(id => id == Game, id => id == Indexer ? "SearchIndexer.exe" : "FiveM");

        Assert.NotNull(summary);
        Assert.Equal("SearchIndexer.exe", summary!.BusiestNeighbour?.ProcessName);

        var interval = Assert.Single(summary.NeighbourContendingIntervals);
        Assert.Equal(Start.AddSeconds(20), interval.Start);
        Assert.Equal(Start.AddSeconds(25), interval.End);
        Assert.Equal(48_000, interval.PeakOperationsPerSecond);
    }

    /// <summary>
    /// The average over the whole file is what the analysis used to be handed. A five second burst in a
    /// forty-five second trace comes out under the bar, which is why the average could never have been
    /// asked when the traffic happened — and why it took a per-second count to answer it.
    /// </summary>
    [Fact]
    public void TheAverageOverTheWholeTraceUnderstatesTheBurst()
    {
        var attribution = Play(
            (From: 0, Seconds: 45, Process: Game, PerSecond: 2_400),
            (From: 20, Seconds: 5, Process: Indexer, PerSecond: 48_000));

        var summary = attribution.Summarize(id => id == Game, _ => "SearchIndexer.exe");

        Assert.NotNull(summary);
        Assert.InRange(summary!.BusiestNeighbour!.OperationsPerSecond, 5_000, 6_000);
        Assert.NotEmpty(summary.NeighbourContendingIntervals);
    }

    /// <summary>
    /// A neighbour that used the file system steadily without ever contending gets no intervals, and a
    /// consumer therefore gets nothing to match against a window.
    /// </summary>
    [Fact]
    public void SteadyOrdinaryUseProducesNoIntervals()
    {
        var attribution = Play(
            (From: 0, Seconds: 30, Process: Game, PerSecond: 2_400),
            (From: 0, Seconds: 30, Process: Indexer, PerSecond: 3_185));

        var summary = attribution.Summarize(id => id == Game, _ => "SearchIndexer.exe");

        Assert.NotNull(summary);
        Assert.False(summary!.HasContendingNeighbour);
        Assert.Empty(summary.NeighbourContendingIntervals);
    }

    /// <summary>
    /// Two bursts with a quiet gap between them are two intervals, so a window that contains one of them
    /// is not told about the other.
    /// </summary>
    [Fact]
    public void SeparateBurstsAreSeparateIntervals()
    {
        var attribution = Play(
            (From: 0, Seconds: 60, Process: Game, PerSecond: 2_400),
            (From: 5, Seconds: 3, Process: Indexer, PerSecond: 20_000),
            (From: 40, Seconds: 3, Process: Indexer, PerSecond: 48_000));

        var summary = attribution.Summarize(id => id == Game, _ => "SearchIndexer.exe");

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.NeighbourContendingIntervals.Count);

        // Busiest first, because a consumer that keeps only some of them should keep the worst.
        Assert.Equal(Start.AddSeconds(40), summary.NeighbourContendingIntervals[0].Start);
        Assert.Equal(Start.AddSeconds(5), summary.NeighbourContendingIntervals[1].Start);
    }

    /// <summary>
    /// One quiet second inside a burst is a wave, not two bursts. An indexer drops below the bar for a
    /// second at a time and the interval list has to stay readable.
    /// </summary>
    [Fact]
    public void AOneSecondLullDoesNotSplitABurst()
    {
        var attribution = Play(
            (From: 0, Seconds: 20, Process: Game, PerSecond: 2_400),
            (From: 3, Seconds: 2, Process: Indexer, PerSecond: 30_000),
            (From: 6, Seconds: 2, Process: Indexer, PerSecond: 30_000));

        var summary = attribution.Summarize(id => id == Game, _ => "SearchIndexer.exe");

        Assert.NotNull(summary);
        var interval = Assert.Single(summary!.NeighbourContendingIntervals);
        Assert.Equal(Start.AddSeconds(3), interval.Start);
        Assert.Equal(Start.AddSeconds(8), interval.End);
    }

    /// <summary>
    /// Plays several processes' traffic through the attribution in time order.
    /// </summary>
    /// <remarks>
    /// In time order because an ETL is: the class takes the first event it sees as the start of the
    /// window and the last as its end, so feeding one process's whole stream before another's would
    /// close the window wherever that stream happened to stop.
    /// </remarks>
    private static FileOperationAttribution Play(
        params (int From, int Seconds, int Process, int PerSecond)[] streams)
    {
        var attribution = new FileOperationAttribution();
        var end = streams.Max(stream => stream.From + stream.Seconds);

        for (var second = 0; second < end; second++)
        {
            foreach (var stream in streams)
            {
                if (second < stream.From || second >= stream.From + stream.Seconds)
                {
                    continue;
                }

                for (var operation = 0; operation < stream.PerSecond; operation++)
                {
                    attribution.Record(stream.Process, Start.AddSeconds(second).AddTicks(operation));
                }
            }
        }

        return attribution;
    }
}
