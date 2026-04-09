using System.IO;
using System.IO.Pipes;
using System.Text;

namespace FiveMDiagnostics.App.Wpf;

public sealed class SingleInstanceManager : IAsyncDisposable
{
    private const string MutexName = "FiveMDiagnostics.App.Wpf.SingleInstance";
    private const string PipeName = "FiveMDiagnostics.App.Wpf.Activation";
    private const string ActivationMessage = "ACTIVATE";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenTask;
    private bool _released;

    public SingleInstanceManager()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    public bool IsPrimaryInstance { get; }

    public event EventHandler? ActivationRequested;

    public void StartListening()
    {
        if (!IsPrimaryInstance || _listenTask is not null)
        {
            return;
        }

        _listenTask = Task.Run(() => ListenAsync(_shutdown.Token));
    }

    public static async Task<bool> SignalFirstInstanceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(ActivationMessage + Environment.NewLine);
            await client.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();

        if (IsPrimaryInstance && !_released)
        {
            _mutex.ReleaseMutex();
            _released = true;
        }

        _mutex.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var message = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(message, ActivationMessage, StringComparison.Ordinal))
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore listener errors and keep accepting activation requests.
            }
        }
    }
}