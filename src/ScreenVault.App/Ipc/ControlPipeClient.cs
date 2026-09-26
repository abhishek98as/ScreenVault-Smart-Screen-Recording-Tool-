using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Serilog;

namespace ScreenVault.App.Ipc;

public static class ControlPipeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<IpcResponse?> SendCommandAsync(string command, string? note = null, int timeoutMs = 3000)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", ControlPipeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = new CancellationTokenSource(timeoutMs);

            await client.ConnectAsync(timeoutMs, cts.Token).ConfigureAwait(false);

            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

            var req = new IpcRequest { Cmd = command, Note = note };
            var json = JsonSerializer.Serialize(req, JsonOptions);
            await writer.WriteLineAsync(json).ConfigureAwait(false);

            var responseLine = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(responseLine))
            {
                return JsonSerializer.Deserialize<IpcResponse>(responseLine, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send command '{Cmd}' to running ScreenVault instance.", command);
        }

        return null;
    }
}
