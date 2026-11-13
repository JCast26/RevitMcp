using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;
using RevitMcp.Core.Ipc;

namespace RevitMcp.Addin.Ipc;

/// <summary>
/// Servidor IPC basado en Named Pipes de Windows que escucha solicitudes del Servidor MCP.
/// No requiere privilegios de administrador ni reserva de puertos de red.
/// Reenvía de forma asíncrona cada comando al RevitExternalEventHandler para que se ejecute en el hilo principal de Revit.
/// </summary>
public class NamedPipeIpcServer : IDisposable
{
    private readonly RevitExternalEventHandler _eventHandler;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public NamedPipeIpcServer(RevitExternalEventHandler eventHandler)
    {
        _eventHandler = eventHandler ?? throw new ArgumentNullException(nameof(eventHandler));
    }

    /// <summary>
    /// Inicia el bucle de escucha asíncrono en segundo plano.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _cts = new CancellationTokenSource();
        _isRunning = true;
        _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Detiene el servidor IPC limpiamente.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            // Enviar un ping de desbloqueo rápido si el pipe está esperando conexión
            using var dummyClient = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut);
            dummyClient.Connect(200);
        }
        catch
        {
            // Silencioso al cerrar
        }

        try
        {
            _listenerTask?.Wait(2000);
        }
        catch
        {
            // Ignorar cancelación
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                serverStream = new NamedPipeServerStream(
                    IpcConstants.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await serverStream.WaitForConnectionAsync(ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    serverStream.Dispose();
                    break;
                }

                // Manejar cada conexión de cliente en una tarea separada para no bloquear nuevas conexiones
                var localStream = serverStream;
                _ = Task.Run(async () =>
                {
                    using (localStream)
                    {
                        await HandleClientAsync(localStream, ct).ConfigureAwait(false);
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                serverStream?.Dispose();
                break;
            }
            catch (Exception)
            {
                serverStream?.Dispose();
                // Breve pausa para evitar bucles ajustados en caso de error del sistema
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipeStream, CancellationToken ct)
    {
        try
        {
            RevitExternalEventHandler.LogTrace("IPC: Cliente conectado.");
            using var reader = new StreamReader(pipeStream, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipeStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            RevitExternalEventHandler.LogTrace($"IPC: Línea recibida ({requestLine?.Length ?? 0} chars).");
            if (string.IsNullOrWhiteSpace(requestLine))
                return;

            var request = JsonSerializer.Deserialize<IpcRequest>(requestLine);
            if (request == null)
            {
                var badReq = IpcResponse.Fail(string.Empty, "Error deserializando la petición IpcRequest.");
                await writer.WriteLineAsync(JsonSerializer.Serialize(badReq)).ConfigureAwait(false);
                return;
            }

            // Encolar en el ExternalEventHandler de Revit y esperar la ejecución en el hilo principal
            var response = await _eventHandler.EnqueueAsync(request).ConfigureAwait(false);

            var responseJson = JsonSerializer.Serialize(response);
            await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
            RevitExternalEventHandler.LogTrace($"IPC: Respuesta enviada para '{request.Action}' (success={response.Success}).");
        }
        catch (Exception ex)
        {
            RevitExternalEventHandler.LogTrace($"IPC ERROR: {ex.GetType().Name}: {ex.Message}");
            try
            {
                using var errWriter = new StreamWriter(pipeStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var errResponse = IpcResponse.Fail(string.Empty, $"Error en servidor IPC de Revit: {ex.Message}");
                await errWriter.WriteLineAsync(JsonSerializer.Serialize(errResponse)).ConfigureAwait(false);
            }
            catch
            {
                // Conexión ya cerrada por el cliente
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
