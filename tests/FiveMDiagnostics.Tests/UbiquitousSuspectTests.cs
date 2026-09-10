namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// A neighbour named on every incident of an evening, which nothing said out loud.
/// </summary>
/// <remarks>
/// 9 September: <c>FiveM_ChromeBrowser</c> was a suspected process in 129 of 149 incidents, and in all 98
/// after midnight. The two evenings before it ran at 46 % and 51 %, which is a busy neighbour and reads
/// as one. The step to every single incident is either the evening's finding or a rule naming whoever is
/// busiest — and from inside one incident the two look identical.
/// </remarks>
public sealed class UbiquitousSuspectTests
{
    [Fact]
    public void ANameOnNearlyEveryIncidentIsReported()
    {
        var tally = new IncidentVerdictTally();

        for (var i = 0; i < 98; i++)
        {
            tally.Record(
                Guid.NewGuid(),
                RootCauseCategory.ExternalProcessInterference,
                suspects: ["FiveM_ChromeBrowser", "StreamDeck"]);
        }

        var report = tally.Summary();

        Assert.NotNull(report);
        Assert.NotNull(report.UbiquitousSuspect);
        Assert.Equal("FiveM_ChromeBrowser", report.UbiquitousSuspect.ProcessName);
        Assert.Equal(1.0, report.UbiquitousSuspect.Share, 2);
        Assert.Contains("läs väntkedjorna", report.UbiquitousSuspect.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two evenings before, where the same process was a frequent neighbour and nothing more.
    /// </summary>
    [Fact]
    public void AFrequentNeighbourIsNotAUbiquitousOne()
    {
        var tally = new IncidentVerdictTally();

        for (var i = 0; i < 116; i++)
        {
            tally.Record(
                Guid.NewGuid(),
                RootCauseCategory.ExternalProcessInterference,
                suspects: i < 53 ? ["FiveM_ChromeBrowser"] : ["StreamDeck"]);
        }

        Assert.Null(tally.Summary()!.UbiquitousSuspect);
    }

    /// <summary>A name on three of three is not a pattern, and a short session must not claim one.</summary>
    [Fact]
    public void AHandfulOfIncidentsClaimsNothing()
    {
        var tally = new IncidentVerdictTally();

        for (var i = 0; i < 3; i++)
        {
            tally.Record(Guid.NewGuid(), RootCauseCategory.ExternalProcessInterference, suspects: ["msedge"]);
        }

        Assert.Null(tally.Summary()!.UbiquitousSuspect);
    }

    /// <summary>
    /// Incidents that named nobody are not counted against the share. An evening of forty quiet incidents
    /// and twenty-five naming the same process is still that process on every incident that named one.
    /// </summary>
    [Fact]
    public void IncidentsThatNamedNobodyDoNotDiluteTheShare()
    {
        var tally = new IncidentVerdictTally();

        for (var i = 0; i < 40; i++)
        {
            tally.Record(Guid.NewGuid(), RootCauseCategory.InsufficientEvidence);
        }

        for (var i = 0; i < 25; i++)
        {
            tally.Record(Guid.NewGuid(), RootCauseCategory.ExternalProcessInterference, suspects: ["FiveM_ChromeBrowser"]);
        }

        var report = tally.Summary();

        Assert.NotNull(report!.UbiquitousSuspect);
        Assert.Equal(25, report.UbiquitousSuspect.IncidentsWithSuspects);
    }
}
