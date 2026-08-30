namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// Regression cover for the way a session lost both of its worst frames: each landed inside a window a
/// trivial frame had opened seconds earlier, and the detector's cooldown discarded them.
/// </summary>
public sealed class IncidentEscalationTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 22, 23, 13, 0, TimeSpan.Zero);

    private static IncidentMaterializer CreateMaterializer()
    {
        var ringBuffer = new TimeWindowRingBuffer<TelemetryEvent>(TimeSpan.FromMinutes(3), item => item.Timestamp);
        return new IncidentMaterializer(ringBuffer, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// The exact shape of the 23:13 loss: a 41 ms frame opens the window, and the 2 846 ms frame nine
    /// seconds later has to end up owning it.
    /// </summary>
    [Fact]
    public void AWorseFrameInsideAnOpenWindowRenamesTheIncident()
    {
        var materializer = CreateMaterializer();
        var opened = materializer.MarkIncident(Start, IncidentSeverity.Normal, "Auto: 41 ms frame", frameTimeMs: 41);

        var outcome = materializer.TryEscalate(
            Start.AddSeconds(9),
            IncidentSeverity.Severe,
            "Auto: 2846 ms frame",
            frameTimeMs: 2846,
            out var escalated);

        Assert.Equal(IncidentEscalation.Escalated, outcome);
        Assert.NotNull(escalated);
        Assert.Equal(opened.Id, escalated!.Id);
        Assert.Equal(IncidentSeverity.Severe, escalated.Severity);
        // Nine seconds from the marker, so the label has to say when the frame it names happened —
        // otherwise a trace taken for the frame that opened the window reads as covering this one.
        Assert.StartsWith("Auto: 2846 ms frame kl. ", escalated.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEscalatedMarkerReachesTheFinishedIncident()
    {
        var materializer = CreateMaterializer();
        var environment = CreateEnvironment();

        materializer.MarkIncident(Start, IncidentSeverity.Normal, "Auto: 41 ms frame", frameTimeMs: 41);
        materializer.TryEscalate(Start.AddSeconds(9), IncidentSeverity.Severe, "Auto: 2846 ms frame", 2846, out _);

        var completed = materializer.FinalizeDue(Start.AddSeconds(120), environment, []);

        var incident = Assert.Single(completed);
        Assert.Equal(IncidentSeverity.Severe, incident.Marker.Severity);
        Assert.StartsWith("Auto: 2846 ms frame kl. ", incident.Marker.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without this a single long freeze would rewrite its own marker on every frame it spans, and each
    /// rewrite would ask the capture budget for another trace.
    /// </summary>
    [Fact]
    public void ASmallerFrameDoesNotEscalate()
    {
        var materializer = CreateMaterializer();
        materializer.MarkIncident(Start, IncidentSeverity.Severe, "Auto: 604 ms frame", frameTimeMs: 604);

        Assert.Equal(
            IncidentEscalation.AlreadyWorse,
            materializer.TryEscalate(Start.AddSeconds(5), IncidentSeverity.Normal, "Auto: 45 ms frame", 45, out _));

        Assert.Equal(
            IncidentEscalation.AlreadyWorse,
            materializer.TryEscalate(Start.AddSeconds(6), IncidentSeverity.Severe, "Auto: 604 ms frame", 604, out _));
    }

    [Fact]
    public void SeverityOnlyEverRises()
    {
        var materializer = CreateMaterializer();
        materializer.MarkIncident(Start, IncidentSeverity.Severe, "Auto: 300 ms frame", frameTimeMs: 300);

        // A larger frame that classifies as Normal — possible once the rolling baseline has drifted up
        // inside a bad patch — must not quietly downgrade an incident already marked severe.
        var outcome = materializer.TryEscalate(Start.AddSeconds(5), IncidentSeverity.Normal, "Auto: 500 ms frame", 500, out var escalated);

        Assert.Equal(IncidentEscalation.Escalated, outcome);
        Assert.Equal(IncidentSeverity.Severe, escalated!.Severity);
        Assert.StartsWith("Auto: 500 ms frame", escalated.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// A frame that escalates from close to the marker leaves the label alone. The time is there to stop
    /// a heading being read against a trace from a different second, and a second or two apart is the
    /// same second for that purpose — while an unconditional timestamp would put one on every label in
    /// the app for no reader's benefit.
    /// </summary>
    [Fact]
    public void AFrameEscalatingFromCloseToTheMarkerKeepsThePlainLabel()
    {
        var materializer = CreateMaterializer();
        materializer.MarkIncident(Start, IncidentSeverity.Normal, "Auto: 41 ms frame", frameTimeMs: 41);

        materializer.TryEscalate(Start.AddSeconds(2), IncidentSeverity.Severe, "Auto: 300 ms frame", 300, out var escalated);

        Assert.Equal("Auto: 300 ms frame", escalated!.Label);
    }

    /// <summary>
    /// The case this exists for, from the 29 August session: a 1 049 ms frame escalating a window that
    /// opened 41 seconds before it, whose attached trace covers the opening and not the frame named.
    /// </summary>
    [Fact]
    public void AFrameEscalatingFromAcrossTheWindowCarriesItsOwnTime()
    {
        var materializer = CreateMaterializer();
        materializer.MarkIncident(Start, IncidentSeverity.Severe, "Auto: 92 ms frame", frameTimeMs: 92);

        var frameAt = Start.AddSeconds(41);
        materializer.TryEscalate(frameAt, IncidentSeverity.Severe, "Auto: 1049 ms frame", 1_049, out var escalated);

        Assert.Equal($"Auto: 1049 ms frame kl. {frameAt.ToLocalTime():HH:mm:ss}", escalated!.Label);
    }

    /// <summary>
    /// The gap the caller has to cover itself: the detector's cooldown outlasts the incident window, so
    /// there is a minute in every cycle with nothing open to absorb a suppressed frame.
    /// </summary>
    [Fact]
    public void AFrameOutsideEveryOpenWindowReportsThatNothingWasOpen()
    {
        var materializer = CreateMaterializer();
        materializer.MarkIncident(Start, IncidentSeverity.Normal, "Auto: 41 ms frame", frameTimeMs: 41);

        // The window ends 60 s after the marker; the detector stays quiet until 120 s.
        Assert.Equal(
            IncidentEscalation.NoOpenIncident,
            materializer.TryEscalate(Start.AddSeconds(90), IncidentSeverity.Severe, "Auto: 2846 ms frame", 2846, out var escalated));

        Assert.Null(escalated);
    }

    [Fact]
    public void EscalationWithNothingOpenReportsThatNothingWasOpen()
    {
        Assert.Equal(
            IncidentEscalation.NoOpenIncident,
            CreateMaterializer().TryEscalate(Start, IncidentSeverity.Severe, "Auto: 2846 ms frame", 2846, out _));
    }

    /// <summary>
    /// A sustained saturation incident says more about its window than any frame inside it can, and it
    /// carries no frame time — so its bar starts at zero and every later frame would clear it. Without
    /// the guard, "FPS-taket nått i 15 min" becomes "Auto: 40 ms frame" on the next suppressed spike.
    /// </summary>
    [Fact]
    public void APacingIncidentIsNotRenamedByAFrame()
    {
        var materializer = CreateMaterializer();
        materializer.MarkIncident(
            Start,
            IncidentSeverity.Severe,
            "Auto: FPS-taket nått i 15 min, 45 fps mot 60",
            frameTimeMs: 0,
            allowFrameEscalation: false);

        Assert.Equal(
            IncidentEscalation.AlreadyWorse,
            materializer.TryEscalate(Start.AddSeconds(5), IncidentSeverity.Normal, "Auto: 40 ms frame", 40, out _));

        // Not even a genuinely catastrophic frame takes the label; the incident is about the window.
        Assert.Equal(
            IncidentEscalation.AlreadyWorse,
            materializer.TryEscalate(Start.AddSeconds(6), IncidentSeverity.Severe, "Auto: 2846 ms frame", 2846, out _));

        var incident = Assert.Single(materializer.FinalizeDue(Start.AddSeconds(120), CreateEnvironment(), []));
        Assert.Equal("Auto: FPS-taket nått i 15 min, 45 fps mot 60", incident.Marker.Label);
    }

    private static EnvironmentMetadata CreateEnvironment()
    {
        return new EnvironmentMetadata(
            "Windows 11",
            "AMD Ryzen 7 5700X",
            32UL * 1024 * 1024 * 1024,
            "RTX 3080",
            "555.12",
            120,
            "Enabled",
            true,
            "Example Server",
            Start.AddSeconds(-30),
            Start.AddSeconds(60));
    }
}
