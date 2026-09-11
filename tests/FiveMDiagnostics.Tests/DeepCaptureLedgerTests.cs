namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The capture that was written, cost a budget slot, and reached nothing.
/// </summary>
/// <remarks>
/// 8 September, 23:15:41–23:16:16: 903 MB covering the evening's second worst frame, a 297 ms hitch. The
/// incident it was taken for carried no attachment, no trace row and a verdict of external process
/// interference, and was counted among the ones that "had no trace". The file held the wait chain that
/// explained it. Nothing in the session said so — the gap was found by listing the directory.
/// </remarks>
public sealed class DeepCaptureLedgerTests
{
    /// <summary>When the captures landed; the end-of-session summary reconciles regardless of when.</summary>
    private static readonly DateTimeOffset Written = new(2026, 9, 8, 23, 16, 16, TimeSpan.Zero);

    [Fact]
    public void ACaptureThatReachedNoIncidentIsNamed()
    {
        var ledger = new DeepCaptureLedger();

        ledger.RecordWritten(@"D:\Traces\deep_20260908_211525_644caf7c.etl", Written);
        ledger.RecordWritten(@"D:\Traces\deep_20260908_214538_e6672ada.etl", Written);
        ledger.RecordReachedAnIncident(@"D:\Traces\deep_20260908_214538_e6672ada.etl");

        var report = ledger.Summary();

        Assert.NotNull(report);
        Assert.True(report.HasOrphans);
        Assert.Equal(2, report.CapturesWritten);
        Assert.Equal(["deep_20260908_211525_644caf7c.etl"], report.OrphanedCaptures);
        Assert.Contains("importera den för hand", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary evening, which has to say so out loud. A silent reconciliation is indistinguishable
    /// from one that was never run, and that is the state the ledger exists to end.
    /// </summary>
    [Fact]
    public void AnEveningWhereEveryCaptureLandedSaysSo()
    {
        var ledger = new DeepCaptureLedger();

        foreach (var path in new[] { @"C:\t\a.etl", @"C:\t\b.etl", @"C:\t\c.etl" })
        {
            ledger.RecordWritten(path, Written);
            ledger.RecordReachedAnIncident(path);
        }

        var report = ledger.Summary();

        Assert.NotNull(report);
        Assert.False(report.HasOrphans);
        Assert.Contains("Ingen trace ligger oanalyserad", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same trace attached twice — the direct path and the pending one both run — is one capture,
    /// and a trace imported by hand was never one of this session's writes.
    /// </summary>
    [Fact]
    public void RepeatsAndImportsDoNotMoveTheCount()
    {
        var ledger = new DeepCaptureLedger();

        ledger.RecordWritten(@"C:\t\a.etl", Written);
        ledger.RecordWritten(@"C:\t\a.etl", Written);
        ledger.RecordReachedAnIncident(@"C:\t\a.etl");
        ledger.RecordReachedAnIncident(@"C:\t\a.etl");
        ledger.RecordReachedAnIncident(@"C:\downloads\someone-elses.etl");

        var report = ledger.Summary();

        Assert.NotNull(report);
        Assert.Equal(1, report.CapturesWritten);
        Assert.False(report.HasOrphans);
    }

    /// <summary>A session that took no captures has nothing to reconcile and says nothing.</summary>
    [Fact]
    public void ASessionWithoutCapturesIsSilent()
    {
        Assert.Null(new DeepCaptureLedger().Summary());
    }

    /// <summary>
    /// The evening of 10 September, which never reached its end: the machine went down mid-write, the
    /// session was never stopped, and a reconciliation that only ran on an orderly shutdown was never
    /// written at all. The interim form has to work, and it has to not call a capture written seconds
    /// ago an orphan.
    /// </summary>
    [Fact]
    public void AnInterimReconciliationLeavesTheFreshestCaptureOut()
    {
        var ledger = new DeepCaptureLedger();
        var now = Written.AddMinutes(30);

        ledger.RecordWritten(@"C:	\settled.etl", now.AddMinutes(-20));
        ledger.RecordReachedAnIncident(@"C:	\settled.etl");
        ledger.RecordWritten(@"C:	\just-now.etl", now.AddSeconds(-5));

        var report = ledger.Summary(now, TimeSpan.FromMinutes(2));

        Assert.NotNull(report);
        Assert.False(report.HasOrphans);
        Assert.Equal(1, report.CapturesWritten);
        Assert.Equal(1, report.CapturesPending);
        Assert.Contains("skrevs nyss", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capture that has had its chance and still reached nothing is an orphan on the quarter-hour too;
    /// waiting for a session end that may never come is how the gap went unreported.
    /// </summary>
    [Fact]
    public void AnInterimReconciliationStillNamesASettledOrphan()
    {
        var ledger = new DeepCaptureLedger();
        var now = Written.AddMinutes(30);

        ledger.RecordWritten(@"C:	\orphan.etl", now.AddMinutes(-20));

        var report = ledger.Summary(now, TimeSpan.FromMinutes(2));

        Assert.NotNull(report);
        Assert.True(report.HasOrphans);
        Assert.Equal(["orphan.etl"], report.OrphanedCaptures);
    }

    /// <summary>Nothing has settled yet, so there is nothing to reconcile and no line worth writing.</summary>
    [Fact]
    public void AnInterimReconciliationWithNothingSettledIsSilent()
    {
        var ledger = new DeepCaptureLedger();
        var now = Written.AddMinutes(30);

        ledger.RecordWritten(@"C:	\just-now.etl", now.AddSeconds(-5));

        Assert.Null(ledger.Summary(now, TimeSpan.FromMinutes(2)));
    }
}
