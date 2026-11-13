using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using RevitMcp.Core.Ipc;

namespace RevitMcp.Server.IpcClient;

/// <summary>
/// Cliente IPC que se conecta al Named Pipe expuesto por el Add-in dentro de Revit.
/// </summary>
public class RevitNamedPipeClient
{
    private readonly int _timeoutMs;

    public RevitNamedPipeClient(int timeoutMs = IpcConstants.DefaultTimeoutMs)
    {
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Envía un comando a Revit y espera la respuesta ejecutada en el hilo principal de la API de Revit.
    /// </summary>
    public async Task<IpcResponse> SendCommandAsync(string action, string payloadJson, CancellationToken ct = default)
    {
        var request = new IpcRequest
        {
            Action = action,
            Payload = payloadJson
        };

        try
        {
            using var pipeClient = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                // Intentar conectar con Revit con timeout de 3 segundos
                await pipeClient.ConnectAsync(3000, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return IpcResponse.Fail(
                    request.Id,
                    "No se pudo conectar con el Add-in de Revit. Asegúrese de que Autodesk Revit esté abierto, el Add-in 'Revit MCP Integration' esté cargado y el proyecto esté activo.");
            }

            using var reader = new StreamReader(pipeClient, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipeClient, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            // Enviar solicitud serializada
            var serializedRequest = JsonSerializer.Serialize(request);
            await writer.WriteLineAsync(serializedRequest).ConfigureAwait(false);

            // Leer respuesta desde Revit (cerrar el pipe al cancelar para evitar bloqueo en ReadLineAsync)
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(_timeoutMs);
            using var cancelReg = readCts.Token.Register(() =>
            {
                try { pipeClient.Dispose(); } catch { }
            });

            var responseLine = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return IpcResponse.Fail(request.Id, "Revit cerró la conexión sin devolver una respuesta.");
            }

            var response = JsonSerializer.Deserialize<IpcResponse>(responseLine);
            return response ?? IpcResponse.Fail(request.Id, "Respuesta devuelta por Revit no pudo ser deserializada.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            return IpcResponse.Fail(request.Id, $"Tiempo de espera agotado ({_timeoutMs / 1000}s) esperando que Revit procese la transacción.");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(request.Id, $"Error de comunicación IPC con Revit: {ex.Message}");
        }
    }
}
