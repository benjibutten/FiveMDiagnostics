namespace FiveMDiagnostics.Integrations.PresentMon;

/// <summary>
/// A bounded, capture-owned handoff between Process.OutputDataReceived and the collector loop. Each
/// PresentMon process gets a distinct instance, so a delayed callback can only address its retired
/// buffer and can never append rows to a replacement capture.
/// </summary>
internal sealed class PresentMonStdoutBuffer
{
    public const int DefaultCapacity = 8192;

    private readonly object _sync = new();
    private readonly Queue<string> _lines;
    private readonly int _capacity;
    private bool _active = true;
    private int _droppedLineCount;

    public PresentMonStdoutBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _lines = new Queue<string>(Math.Min(capacity, 1024));
    }

    public int DroppedLineCount
    {
        get
        {
            lock (_sync)
            {
                return _droppedLineCount;
            }
        }
    }

    public bool TryEnqueue(string line)
    {
        lock (_sync)
        {
            if (!_active)
            {
                return false;
            }

            if (_lines.Count >= _capacity)
            {
                _droppedLineCount++;
                return false;
            }

            _lines.Enqueue(line);
            return true;
        }
    }

    public IReadOnlyList<string> Drain()
    {
        lock (_sync)
        {
            if (_lines.Count == 0)
            {
                return [];
            }

            var result = _lines.ToArray();
            _lines.Clear();
            return result;
        }
    }

    public void Deactivate()
    {
        lock (_sync)
        {
            _active = false;
            _lines.Clear();
        }
    }
}
