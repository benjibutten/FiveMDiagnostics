namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.PresentMon;

/// <summary>
/// The collector used to restart PresentMon after every 15 seconds of silence, forever. An alt-tab, a
/// menu or a loading screen presents nothing either, so a paused game produced a kill-and-respawn every
/// 15 seconds — more ETW churn than the frames it was trying to recover.
/// </summary>
public sealed class PresentMonCaptureHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SilenceThreshold_DoublesAfterEveryRestart()
    {
        var health = new PresentMonCaptureHealth();
        health.OnCaptureStarted(Start);

        Assert.Equal(PresentMonCaptureHealth.BaseSilenceBeforeRestart, health.SilenceThreshold);

        Assert.True(health.TryBeginRestart(Start));
        Assert.Equal(TimeSpan.FromSeconds(30), health.SilenceThreshold);

        health.OnCaptureStarted(Start);
        Assert.True(health.TryBeginRestart(Start + TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromSeconds(60), health.SilenceThreshold);
    }

    [Fact]
    public void SilenceThreshold_IsCapped()
    {
        var health = new PresentMonCaptureHealth();
        var now = Start;

        for (var attempt = 0; attempt < PresentMonCaptureHealth.MaxConsecutiveRestarts; attempt++)
        {
            health.OnCaptureStarted(now);
            Assert.True(health.TryBeginRestart(now));
            now += TimeSpan.FromMinutes(10);
        }

        Assert.True(health.SilenceThreshold <= PresentMonCaptureHealth.MaxSilenceBeforeRestart);
    }

    [Fact]
    public void Restarts_AreSuspendedAfterTheCeilingIsReached()
    {
        var health = new PresentMonCaptureHealth();
        var now = Start;

        for (var attempt = 0; attempt < PresentMonCaptureHealth.MaxConsecutiveRestarts; attempt++)
        {
            health.OnCaptureStarted(now);
            Assert.True(health.TryBeginRestart(now));
            now += TimeSpan.FromMinutes(10);
        }

        Assert.True(health.IsSuspended);
        Assert.False(health.TryBeginRestart(now));
        Assert.False(health.CanRestart(now + TimeSpan.FromHours(1)));
    }

    /// <summary>The backoff, not just the silence window, has to hold a PresentMon that exits instantly.</summary>
    [Fact]
    public void Restart_IsHeldBackUntilTheBackoffElapses()
    {
        var health = new PresentMonCaptureHealth();
        health.OnCaptureStarted(Start);

        Assert.True(health.TryBeginRestart(Start));
        Assert.False(health.TryBeginRestart(Start + TimeSpan.FromSeconds(5)));
        Assert.True(health.TryBeginRestart(Start + TimeSpan.FromSeconds(31)));
        Assert.Equal(2, health.RestartCount);
    }

    [Fact]
    public void Progress_WithinTheSilenceWindow_KeepsTheCaptureAlive()
    {
        var health = new PresentMonCaptureHealth();
        health.OnCaptureStarted(Start);

        Assert.False(health.IsSilent(Start + TimeSpan.FromSeconds(14)));
        health.OnProgress(Start + TimeSpan.FromSeconds(14));

        Assert.False(health.IsSilent(Start + TimeSpan.FromSeconds(28)));
        Assert.True(health.IsSilent(Start + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void StableRun_ClearsTheRestartLadder()
    {
        var health = new PresentMonCaptureHealth();
        health.OnCaptureStarted(Start);
        Assert.True(health.TryBeginRestart(Start));

        var restarted = Start + TimeSpan.FromMinutes(1);
        health.OnCaptureStarted(restarted);

        Assert.False(health.OnProgress(restarted + TimeSpan.FromSeconds(30)));
        Assert.Equal(1, health.RestartCount);

        Assert.True(health.OnProgress(restarted + PresentMonCaptureHealth.StableRunBeforeReset));
        Assert.Equal(0, health.RestartCount);
        Assert.Equal(PresentMonCaptureHealth.BaseSilenceBeforeRestart, health.SilenceThreshold);
    }

    [Fact]
    public void NewTargetProcess_StartsFromACleanLadder()
    {
        var health = new PresentMonCaptureHealth();
        var now = Start;
        health.OnTargetChanged(1234);

        for (var attempt = 0; attempt < PresentMonCaptureHealth.MaxConsecutiveRestarts; attempt++)
        {
            health.OnCaptureStarted(now);
            health.TryBeginRestart(now);
            now += TimeSpan.FromMinutes(10);
        }

        Assert.True(health.IsSuspended);

        health.OnTargetChanged(4321);

        Assert.False(health.IsSuspended);
        Assert.False(health.HasStartedCapture);
        Assert.Equal(0, health.RestartCount);
        Assert.True(health.CanRestart(now));
    }

    [Fact]
    public void Reset_ForgetsEverythingBetweenSessions()
    {
        var health = new PresentMonCaptureHealth();
        health.OnTargetChanged(1234);
        health.OnCaptureStarted(Start);
        health.TryBeginRestart(Start);

        health.Reset();

        Assert.Equal(0, health.TargetProcessId);
        Assert.Equal(0, health.RestartCount);
        Assert.False(health.HasStartedCapture);
    }
}
