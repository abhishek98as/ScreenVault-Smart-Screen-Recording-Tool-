using System.Text.Json.Serialization;

namespace ScreenVault.App.Ipc;

public sealed class IpcRequest
{
    [JsonPropertyName("cmd")]
    public string Cmd { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class IpcResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("elapsed")]
    public string Elapsed { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}
