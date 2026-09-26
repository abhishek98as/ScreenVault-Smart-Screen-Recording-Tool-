using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Serilog;

namespace ScreenVault.App.Ipc;

public sealed class ControlPipeServer : IAsyncDisposable
{
    public const string PipeName = "ScreenVault-Control-7C0A45B9-F2D7-4952-B79D-5B6608C42589";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Func<IpcRequest, Task<IpcResponse>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public ControlPipeServer(Func<IpcRequest, Task<IpcResponse>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void Start()
    {
        _listenTask = Task.Run(ListenLoopAsync, CancellationToken.None);
        Log.Information("ControlPipeServer listening on pipe \\\\.\\pipe\\{PipeName}", PipeName);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                // Handle client in background task so server immediately listens for next connection
                _ = Task.Run(() => HandleClientAsync(pipe, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Exception in ControlPipeServer loop.");
                try { await Task.Delay(200, _cts.Token).ConfigureAwait(false); } catch { }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var request = JsonSerializer.Deserialize<IpcRequest>(line, JsonOptions) ?? new IpcRequest();
                    Log.Information("IPC Server received command: {Cmd}", request.Cmd);
                    var response = await _handler(request).ConfigureAwait(false);
                    var responseJson = JsonSerializer.Serialize(response, JsonOptions);
                    await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error processing IPC client connection.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_listenTask != null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore during shutdown
            }
        }
        _cts.Dispose();
    }
}
