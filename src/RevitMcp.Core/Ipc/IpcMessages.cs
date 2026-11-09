using System.Text.Json.Serialization;

namespace RevitMcp.Core.Ipc;

/// <summary>
/// Constantes globales para la comunicación IPC entre el Servidor MCP y el Add-in de Revit.
/// </summary>
public static class IpcConstants
{
    public const string PipeName = "RevitMcp_Ipc_Pipe";
    public const int DefaultTimeoutMs = 60000; // 60 segundos para operaciones pesadas de Revit
}

/// <summary>
/// Mensaje de solicitud IPC enviado desde el Servidor MCP hacia Revit.
/// </summary>
public class IpcRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;
}

/// <summary>
/// Mensaje de respuesta IPC devuelto por Revit hacia el Servidor MCP.
/// </summary>
public class IpcResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static IpcResponse Ok(string id, string resultJson) =>
        new() { Id = id, Success = true, Result = resultJson };

    public static IpcResponse Fail(string id, string errorMessage) =>
        new() { Id = id, Success = false, Error = errorMessage };
}
