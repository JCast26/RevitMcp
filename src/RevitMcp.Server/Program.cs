using System.Text;
using System.Text.Json;
using RevitMcp.Server.IpcClient;
using RevitMcp.Server.Mcp;

namespace RevitMcp.Server;

public class Program
{
    private static readonly RevitNamedPipeClient IpcClient = new();

    public static async Task Main(string[] args)
    {
        // Configurar codificación UTF-8 para stdio
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);

        // Los mensajes de log SIEMPRE van a stderr para no contaminar el canal JSON-RPC stdio
        await Console.Error.WriteLineAsync("[Revit MCP Server] Servidor de consola iniciado por stdio.");

        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
                break; // Fin del flujo stdio (el cliente cerró la sesión)

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var rpcRequest = JsonSerializer.Deserialize<JsonRpcRequest>(line);
                if (rpcRequest == null)
                    continue;

                var rpcResponse = await HandleRpcRequestAsync(rpcRequest).ConfigureAwait(false);

                // Si no devuelve respuesta (ej. notificaciones JSON-RPC), no escribir en stdout
                if (rpcResponse != null)
                {
                    var responseJson = JsonSerializer.Serialize(rpcResponse, new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    });
                    await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[Revit MCP Server Error]: {ex.Message}");
                var errorResponse = JsonRpcResponse.FromError(null, -32603, $"Internal error: {ex.Message}");
                await writer.WriteLineAsync(JsonSerializer.Serialize(errorResponse)).ConfigureAwait(false);
            }
        }

        await Console.Error.WriteLineAsync("[Revit MCP Server] Sesión finalizada.");
    }

    private static async Task<JsonRpcResponse?> HandleRpcRequestAsync(JsonRpcRequest request)
    {
        switch (request.Method)
        {
            case "initialize":
            {
                var initResult = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new
                    {
                        tools = new { }
                    },
                    serverInfo = new
                    {
                        name = "revit-mcp-server",
                        version = "1.0.0"
                    }
                };
                return JsonRpcResponse.Success(request.Id, initResult);
            }

            case "notifications/initialized":
            {
                await Console.Error.WriteLineAsync("[Revit MCP Server] Notificación 'initialized' recibida del LLM.");
                return null; // Notificaciones no devuelven respuesta
            }

            case "ping":
            {
                return JsonRpcResponse.Success(request.Id, new { });
            }

            case "tools/list":
            {
                var toolsResult = new
                {
                    tools = McpToolDefinitions.Tools
                };
                return JsonRpcResponse.Success(request.Id, toolsResult);
            }

            case "tools/call":
            {
                return await HandleToolCallAsync(request).ConfigureAwait(false);
            }

            default:
            {
                // Si tiene id es una petición; si no, es una notificación desconocida
                if (request.Id != null)
                {
                    return JsonRpcResponse.FromError(request.Id, -32601, $"Método '{request.Method}' no encontrado.");
                }
                return null;
            }
        }
    }

    private static async Task<JsonRpcResponse> HandleToolCallAsync(JsonRpcRequest request)
    {
        if (!request.Params.HasValue)
        {
            return JsonRpcResponse.FromError(request.Id, -32602, "Parámetros ausentes para tools/call.");
        }

        var paramsObj = request.Params.Value;
        if (!paramsObj.TryGetProperty("name", out var toolNameElement))
        {
            return JsonRpcResponse.FromError(request.Id, -32602, "Nombre de la herramienta no especificado.");
        }

        string toolName = toolNameElement.GetString() ?? string.Empty;
        string argsJson = paramsObj.TryGetProperty("arguments", out var argsElement)
            ? argsElement.GetRawText()
            : "{}";

        await Console.Error.WriteLineAsync($"[Revit MCP Server] Invocando herramienta '{toolName}' con payload: {argsJson}");

        // Reenviar al Add-in de Revit vía IPC (Named Pipes)
        var ipcResponse = await IpcClient.SendCommandAsync(toolName, argsJson).ConfigureAwait(false);

        if (ipcResponse.Success)
        {
            var successResult = new
            {
                content = new object[]
                {
                    new
                    {
                        type = "text",
                        text = ipcResponse.Result ?? "{}"
                    }
                }
            };
            return JsonRpcResponse.Success(request.Id, successResult);
        }
        else
        {
            var errorResult = new
            {
                content = new object[]
                {
                    new
                    {
                        type = "text",
                        text = $"Error en Revit: {ipcResponse.Error}"
                    }
                },
                isError = true
            };
            return JsonRpcResponse.Success(request.Id, errorResult);
        }
    }
}
