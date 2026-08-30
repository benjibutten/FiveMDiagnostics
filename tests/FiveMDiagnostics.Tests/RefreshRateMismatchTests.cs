namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The line that would have ended the investigation on its first evening.
/// </summary>
/// <remarks>
/// The machine ran a 120 Hz primary beside a 60 Hz secondary for eight sessions. The session log
/// recorded the primary's rate and nothing else, so the pair — which is the part that matters — was
/// never visible. Syncing the two took the share of frames reaching the screen off cadence from 11.32%
/// to 0.41% and halved the hitch rate at every threshold.
/// </remarks>
public sealed class RefreshRateMismatchTests
{
    /// <summary>The pair the first eight sessions ran on.</summary>
    private static readonly AttachedDisplay[] Mismatched =
    [
        new("Generic PnP Monitor", 120, 2560, 1440, IsPrimary: true),
        new("LG ULTRAWIDE", 60, 2560, 1080, IsPrimary: false),
    ];

    /// <summary>The configuration the first eight sessions ran in.</summary>
    [Fact]
    public void TheHundredAndTwentyBesideSixtyPairIsReported()
    {
        var message = RefreshRateMismatch.Describe(
        [
            new AttachedDisplay("Generic PnP Monitor", 120, 2560, 1440, IsPrimary: true),
            new AttachedDisplay("LG ULTRAWIDE", 60, 2560, 1080, IsPrimary: false),
        ]);

        Assert.NotNull(message);
        Assert.Contains("120 Hz", message, StringComparison.Ordinal);
        Assert.Contains("60 Hz", message, StringComparison.Ordinal);
        Assert.Contains("primär", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state after the fix. The two panels could not be matched exactly — one does 59.94, the other
    /// 60 — and Windows reports those as whole hertz, so the good case arrives here as 59 beside 60. It
    /// measured 0.41% off cadence and must not warn, or the warning means nothing.
    /// </summary>
    [Fact]
    public void ThePairThatCouldOnlyBeSyncedToWithinOneHertzIsNotReported()
    {
        var message = RefreshRateMismatch.Describe(
        [
            new AttachedDisplay("Generic PnP Monitor", 59, 2560, 1440, IsPrimary: true),
            new AttachedDisplay("LG ULTRAWIDE", 60, 2560, 1080, IsPrimary: false),
        ]);

        Assert.Null(message);
    }

    [Fact]
    public void ASingleDisplayIsNeverReported()
    {
        Assert.Null(RefreshRateMismatch.Describe(
            [new AttachedDisplay("Generic PnP Monitor", 120, 2560, 1440, IsPrimary: true)]));
    }

    [Fact]
    public void AMissingInventoryIsNotAFinding()
    {
        Assert.Null(RefreshRateMismatch.Describe(null));
        Assert.Null(RefreshRateMismatch.Describe([]));
    }

    /// <summary>
    /// A display whose mode could not be read contributes nothing rather than reading as 0 Hz and making
    /// every machine look mismatched.
    /// </summary>
    [Fact]
    public void ADisplayWithNoReadableRateIsIgnored()
    {
        Assert.Null(RefreshRateMismatch.Describe(
        [
            new AttachedDisplay("Generic PnP Monitor", 60, 2560, 1440, IsPrimary: true),
            new AttachedDisplay("Unknown", 0, 0, 0, IsPrimary: false),
        ]));
    }

    /// <summary>
    /// A session whose frames never went through the compositor was never resampled, so the warning
    /// written before the first frame existed did not apply to it and is withdrawn.
    /// </summary>
    [Fact]
    public void AWarningIsWithdrawnWhenTheSessionNeverComposed()
    {
        var withdrawal = RefreshRateMismatch.DescribeWithdrawal(Mismatched, composedShare: 0);

        Assert.NotNull(withdrawal);
        Assert.Contains("gäller inte", withdrawal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The configuration the investigation was about: every frame composed, and the warning stands.
    /// </summary>
    [Fact]
    public void AComposedSessionKeepsItsWarning()
    {
        Assert.Null(RefreshRateMismatch.DescribeWithdrawal(Mismatched, composedShare: 1));
    }

    /// <summary>
    /// A session that spent part of the evening composed — which is what alt-tabbing out of exclusive
    /// fullscreen does — paid the mismatch for that part, so there is nothing to withdraw.
    /// </summary>
    [Fact]
    public void APartlyComposedSessionKeepsItsWarning()
    {
        Assert.Null(RefreshRateMismatch.DescribeWithdrawal(Mismatched, composedShare: 0.4));
    }

    /// <summary>Nothing was warned about on a machine whose displays agree, so nothing is withdrawn.</summary>
    [Fact]
    public void MatchedDisplaysHaveNothingToWithdraw()
    {
        Assert.Null(RefreshRateMismatch.DescribeWithdrawal(
            [
                new AttachedDisplay("Generic PnP Monitor", 60, 2560, 1440, IsPrimary: true),
                new AttachedDisplay("LG ULTRAWIDE", 60, 2560, 1080, IsPrimary: false),
            ],
            composedShare: 0));
    }

    /// <summary>Three displays are reported on the widest pair among them, not on adjacent ones.</summary>
    [Fact]
    public void TheWidestPairDecidesIt()
    {
        var message = RefreshRateMismatch.Describe(
        [
            new AttachedDisplay("A", 60, 2560, 1440, IsPrimary: true),
            new AttachedDisplay("B", 62, 1920, 1080, IsPrimary: false),
            new AttachedDisplay("C", 144, 1920, 1080, IsPrimary: false),
        ]);

        Assert.NotNull(message);
        Assert.Contains("144 Hz", message, StringComparison.Ordinal);
    }
}
