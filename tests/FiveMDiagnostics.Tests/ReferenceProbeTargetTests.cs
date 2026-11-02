namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Collectors;

/// <summary>
/// A reference host's silence only counts once it has answered, so a router that drops ping cannot read
/// as a network that was down all session.
/// </summary>
public sealed class ReferenceProbeTargetTests
{
    private const int Limit = 3;
    private const int QuietInterval = 4;

    private static ReferenceProbeTarget Target() => new("192.168.1.1", isInternet: false, Limit, QuietInterval);

    [Fact]
    public void ASilentHostIsNotEvidenceAndIsAskedLessOften()
    {
        var target = Target();

        Assert.False(target.Observe(answered: false));
        Assert.False(target.Observe(answered: false));
        Assert.False(target.IsQuiet);
        Assert.True(target.ShouldProbe());

        Assert.False(target.Observe(answered: false));
        Assert.True(target.IsQuiet);

        Assert.False(target.ShouldProbe());
        Assert.False(target.ShouldProbe());
        Assert.False(target.ShouldProbe());
        Assert.True(target.ShouldProbe());
    }

    /// <summary>
    /// A session started during an outage looks exactly like a host that blocks ping, until the connection
    /// comes back. Dropping it for good lost the measurement for the rest of the evening.
    /// </summary>
    [Fact]
    public void AHostThatStartsAnsweringIsMeasuredFromThen()
    {
        var target = Target();
        for (var i = 0; i < Limit; i++)
        {
            target.Observe(answered: false);
        }

        Assert.True(target.Observe(answered: true));
        Assert.False(target.IsQuiet);
        Assert.True(target.ShouldProbe());
        Assert.True(target.Observe(answered: false));
    }

    /// <summary>
    /// The reason it cannot be a plain failure count: this is the outage the probe is there to see, and
    /// it can run for far longer than the limit.
    /// </summary>
    [Fact]
    public void AHostThatHasAnsweredKeepsBeingAsked()
    {
        var target = Target();

        Assert.True(target.Observe(answered: true));

        for (var i = 0; i < Limit * 10; i++)
        {
            Assert.True(target.ShouldProbe());
            Assert.True(target.Observe(answered: false));
        }

        Assert.False(target.IsQuiet);
    }
}
