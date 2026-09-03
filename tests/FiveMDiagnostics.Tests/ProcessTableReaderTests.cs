namespace FiveMDiagnostics.Tests;

using System.Diagnostics;

using FiveMDiagnostics.Collectors.Interop;

/// <summary>
/// The system call that took the app's own cost from a third of a core to nothing measurable.
/// </summary>
/// <remarks>
/// <para>
/// The sweep this replaces opened every process on the machine to read four properties off it: 168 ms
/// for 265 processes, every two seconds, which is 8% of a core spent by a tool whose entire purpose is
/// to notice when a game is short of them. The deep captures of 2 September show the app at 0.33–0.40
/// cores all evening, ahead of the compositor, with 95% of it inside <c>ntoskrnl.exe</c>.
/// </para>
/// <para>
/// These tests run against the real process table, because that is the only thing they could
/// meaningfully run against — the whole class is a structure layout agreed with the kernel, and a fake
/// would only assert that the fake was parsed. They assert what holds on any Windows machine rather
/// than anything about this one.
/// </para>
/// </remarks>
public sealed class ProcessTableReaderTests
{
    [Fact]
    public void TheWholeProcessTableComesBackFromOneCall()
    {
        var rows = ProcessTableReader.TryRead(DateTimeOffset.UtcNow);

        Assert.NotNull(rows);

        // Any running Windows machine has far more than this; the point is that it is a table and not
        // a handful of processes that happened to be openable.
        Assert.True(rows!.Count > 20, $"only {rows.Count} processes came back");

        // The idle process is not a process, and its CPU time would own every top list for ever.
        Assert.DoesNotContain(rows, row => row.Snapshot.ProcessId == 0);
    }

    /// <summary>
    /// Names and ids have to line up with what the managed API reports, because every downstream
    /// comparison — the suspect filter, the overlay list, the game's own rows — is written against
    /// <see cref="Process.ProcessName"/> and its extensionless form.
    /// </summary>
    [Fact]
    public void ItAgreesWithTheManagedApiAboutThisProcess()
    {
        using var self = Process.GetCurrentProcess();
        var rows = ProcessTableReader.TryRead(DateTimeOffset.UtcNow);

        var mine = Assert.Single(rows!, row => row.Snapshot.ProcessId == self.Id);

        Assert.Equal(self.ProcessName, mine.Snapshot.ProcessName);
        Assert.Equal(self.SessionId, mine.SessionId);
        Assert.True(mine.Snapshot.TotalProcessorTime > TimeSpan.Zero);
    }

    /// <summary>
    /// The gap the old sweep left: a process it could not open was dropped without a trace, and that is
    /// most of session 0 — including the two services most likely to be behind a stutter.
    /// </summary>
    [Fact]
    public void ServicesAreVisibleWithoutOpeningThem()
    {
        using var self = Process.GetCurrentProcess();
        var rows = ProcessTableReader.TryRead(DateTimeOffset.UtcNow);

        var services = rows!.Where(row => row.SessionId != self.SessionId).ToArray();

        Assert.True(services.Length > 10, $"only {services.Length} processes outside the session");

        // And they are named, which the handle-based path cannot do for a protected service at all.
        Assert.Contains(services, row => row.Snapshot.ProcessName.Length > 0 && !row.Snapshot.ProcessName.StartsWith("pid ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The CPU time has to be the same quantity the managed API reports, because the analysis compares
    /// figures from both: the game's own share comes from <see cref="Process"/> and its neighbours' from
    /// here, and a verdict turns on which of them is larger.
    /// </summary>
    /// <remarks>
    /// Checked as a delta over the same interval rather than as an absolute, and against the managed
    /// API rather than against a number of cores. Asserting "a burn loop reads as one core" measures how
    /// busy the machine was — the first version of this test failed at 27.9% because xUnit was running
    /// three hundred other tests in the same process. Two readings of the same process over the same
    /// interval have to agree whatever else is running, and that is exactly what a wrong field offset
    /// would break.
    /// </remarks>
    [Fact]
    public async Task TheCpuTimeIsTheSameQuantityTheManagedApiReports()
    {
        using var self = Process.GetCurrentProcess();

        self.Refresh();
        var managedBefore = self.TotalProcessorTime;
        var before = ProcessTableReader.TryRead(DateTimeOffset.UtcNow)!.Single(row => row.Snapshot.ProcessId == self.Id);

        using (var stop = new CancellationTokenSource())
        {
            var burn = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    _ = Guid.NewGuid().GetHashCode();
                }
            });

            await Task.Delay(300);
            await stop.CancelAsync();
            await burn.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var after = ProcessTableReader.TryRead(DateTimeOffset.UtcNow)!.Single(row => row.Snapshot.ProcessId == self.Id);
        self.Refresh();
        var managedDelta = self.TotalProcessorTime - managedBefore;
        var readerDelta = after.Snapshot.TotalProcessorTime - before.Snapshot.TotalProcessorTime;

        // Something was burned, so the test is measuring a change rather than two zeroes.
        Assert.True(readerDelta > TimeSpan.FromMilliseconds(100), $"only {readerDelta.TotalMilliseconds:F0} ms of CPU moved");

        // The two readings bracket each other and are taken a few microseconds apart, so a fifth of the
        // interval is ample slack for one to have caught work the other did not.
        var difference = Math.Abs((managedDelta - readerDelta).TotalMilliseconds);
        Assert.True(
            difference < managedDelta.TotalMilliseconds * 0.2 + 50,
            $"reader said {readerDelta.TotalMilliseconds:F0} ms, Process said {managedDelta.TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// The I/O counters are read from the second half of the structure, past twenty pointer-sized
    /// fields, so they are the first thing a layout mistake would corrupt.
    /// </summary>
    [Fact]
    public void IoCountersAreReadFromTheRightPlace()
    {
        var rows = ProcessTableReader.TryRead(DateTimeOffset.UtcNow)!;

        // Nothing negative, nothing absurd: a process that had transferred more than a petabyte would
        // mean the offset landed on a pointer rather than a counter.
        Assert.All(rows, row =>
        {
            Assert.True(row.Snapshot.ReadBytes < 1UL << 50, $"{row.Snapshot.ProcessName} read {row.Snapshot.ReadBytes} bytes");
            Assert.True(row.Snapshot.WriteBytes < 1UL << 50, $"{row.Snapshot.ProcessName} wrote {row.Snapshot.WriteBytes} bytes");
        });

        // And a machine that has been up for a while has moved some, so the fields are not simply zero.
        Assert.Contains(rows, row => row.Snapshot.ReadBytes > 0);
    }
}
