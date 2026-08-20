namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.PresentMon;

public sealed class PresentMonStdoutBufferTests
{
    [Fact]
    public void Buffer_IsBoundedAndReportsDroppedLines()
    {
        var buffer = new PresentMonStdoutBuffer(capacity: 2);

        Assert.True(buffer.TryEnqueue("header"));
        Assert.True(buffer.TryEnqueue("row-1"));
        Assert.False(buffer.TryEnqueue("row-2"));
        Assert.Equal(1, buffer.DroppedLineCount);
        Assert.Equal(["header", "row-1"], buffer.Drain());
    }

    [Fact]
    public void RetiredCaptureBuffer_RejectsDelayedCallbacksWithoutAffectingReplacement()
    {
        var retired = new PresentMonStdoutBuffer(capacity: 4);
        var replacement = new PresentMonStdoutBuffer(capacity: 4);
        retired.TryEnqueue("old-header");

        retired.Deactivate();
        Assert.False(retired.TryEnqueue("delayed-old-row"));
        Assert.True(replacement.TryEnqueue("new-header"));

        Assert.Empty(retired.Drain());
        Assert.Equal(["new-header"], replacement.Drain());
    }
}
