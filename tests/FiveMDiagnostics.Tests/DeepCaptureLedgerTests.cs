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
    [Fact]
    public void ACaptureThatReachedNoIncidentIsNamed()
    {
        var ledger = new DeepCaptureLedger();

        ledger.RecordWritten(@"D:\Traces\deep_20260908_211525_644caf7c.etl");
        ledger.RecordWritten(@"D:\Traces\deep_20260908_214538_e6672ada.etl");
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
            ledger.RecordWritten(path);
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

        ledger.RecordWritten(@"C:\t\a.etl");
        ledger.RecordWritten(@"C:\t\a.etl");
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
}
