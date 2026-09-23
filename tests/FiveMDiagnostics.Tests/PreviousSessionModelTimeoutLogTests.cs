namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

public sealed class PreviousSessionModelTimeoutLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FiveMDiagnostics.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// An evening with no timeouts is written as an empty list, so the next evening cannot report a
    /// repeat from a list two evenings old.
    /// </summary>
    [Fact]
    public void AnEmptyTimeoutListIsKeptApartFromNoList()
    {
        Assert.Null(PreviousSessionModelTimeoutLog.TryLoad(_directory));

        PreviousSessionModelTimeoutLog.Save(_directory, "CitizenFX_log_2026-09-21T184909.log", ["v_9_kitchen_unit"]);
        Assert.Contains("v_9_kitchen_unit", PreviousSessionModelTimeoutLog.TryLoad(_directory)!.Models);

        PreviousSessionModelTimeoutLog.Save(_directory, "CitizenFX_log_2026-09-22T182648.log", []);
        var empty = PreviousSessionModelTimeoutLog.TryLoad(_directory);
        Assert.NotNull(empty);
        Assert.Empty(empty.Models);
    }

    /// <summary>
    /// The list names the client log it came from, so a second app session on the same game run can see
    /// it is looking at its own evening.
    /// </summary>
    [Fact]
    public void TheListRemembersWhichClientLogItCameFrom()
    {
        PreviousSessionModelTimeoutLog.Save(_directory, "CitizenFX_log_2026-09-22T182648.log", ["v_9_kitchen_unit"]);

        var history = PreviousSessionModelTimeoutLog.TryLoad(_directory)!;

        Assert.Equal("CitizenFX_log_2026-09-22T182648.log", history.LogFile);
        Assert.DoesNotContain("CitizenFX_log_2026-09-22T182648.log", history.Models);
    }
}
