using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.UI;
using RevitMcp.Addin.Tools;
using RevitMcp.Core.Ipc;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin;

/// <summary>
/// Elemento de trabajo encolado para su procesamiento seguro en el hilo principal de Revit.
/// </summary>
public class RevitWorkItem
{
    public IpcRequest Request { get; }
    public TaskCompletionSource<IpcResponse> CompletionSource { get; }

    public RevitWorkItem(IpcRequest request, TaskCompletionSource<IpcResponse> completionSource)
    {
        Request = request;
        CompletionSource = completionSource;
    }
}

/// <summary>
/// Implementación obligatoria de IExternalEventHandler.
/// Garantiza que todas las transacciones, modificaciones y lecturas del modelo de Revit
/// se ejecuten de forma segura en el hilo principal de Revit (Revit API thread context).
/// </summary>
public class RevitExternalEventHandler : IExternalEventHandler
{
    private ExternalEvent? _externalEvent;
    private readonly ConcurrentQueue<RevitWorkItem> _queue = new();

    public bool HasPendingWork => !_queue.IsEmpty;

    /// <summary>
    /// Inicializa la instancia de ExternalEvent. Debe invocarse desde el contexto de la API de Revit (ej. OnStartup).
    /// </summary>
    public void Initialize()
    {
        _externalEvent = ExternalEvent.Create(this);
    }

    /// <summary>
    /// Encola una solicitud recibida desde el servidor IPC y despierta a Revit mediante ExternalEvent.Raise().
    /// Devuelve una tarea asíncrona que se resolverá cuando Revit termine de procesar en su hilo principal.
    /// </summary>
    public Task<IpcResponse> EnqueueAsync(IpcRequest request)
    {
        var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_externalEvent == null)
        {
            LogTrace($"EnqueueAsync('{request.Action}'): _externalEvent es null.");
            tcs.SetResult(IpcResponse.Fail(request.Id, "El manejador de eventos externos de Revit no ha sido inicializado."));
            return tcs.Task;
        }

        _queue.Enqueue(new RevitWorkItem(request, tcs));
        var raiseResult = _externalEvent.Raise();
        LogTrace($"EnqueueAsync('{request.Action}'): Raise() -> {raiseResult}");

        if (raiseResult != ExternalEventRequest.Accepted && raiseResult != ExternalEventRequest.Pending)
        {
            tcs.TrySetResult(IpcResponse.Fail(request.Id, $"Revit rechazó el ExternalEvent ({raiseResult}). Verifica que no haya un diálogo modal abierto."));
        }

        return tcs.Task;
    }

    /// <summary>
    /// Método invocado por Autodesk Revit en su hilo principal cuando ExternalEvent es activado.
    /// </summary>
    public void Execute(UIApplication uiapp)
    {
        LogTrace($"Execute(UIApplication): Cola con {_queue.Count} elemento(s).");
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                var response = ProcessRequest(uiapp, item.Request);
                item.CompletionSource.TrySetResult(response);
                LogTrace($"Execute('{item.Request.Action}'): completado (success={response.Success}).");
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException != null ? $" -> {ex.InnerException.Message}" : string.Empty;
                var fullError = $"{ex.Message}{innerMsg}\n{ex.StackTrace}";
                LogTrace($"Execute('{item.Request.Action}') ERROR: {ex.Message}");
                item.CompletionSource.TrySetResult(IpcResponse.Fail(item.Request.Id, fullError));
            }
        }
    }

    private static readonly object LogLock = new();
    internal static void LogTrace(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMcp");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "ipc.log");
            lock (LogLock)
            {
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // No interrumpir flujo por errores de log
        }
    }

    /// <summary>
    /// Enruta la acción solicitada por el LLM hacia el manejador de la herramienta correspondiente.
    /// </summary>
    private IpcResponse ProcessRequest(UIApplication uiapp, IpcRequest request)
    {
        var doc = uiapp.ActiveUIDocument?.Document;

        switch (request.Action)
        {
            case "AuditParameters":
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var modelReq = JsonSerializer.Deserialize<AuditParametersRequest>(request.Payload, options)
                    ?? throw new ArgumentException("Payload inválido para AuditParameters");

                if (doc == null)
                    return IpcResponse.Fail(request.Id, "No hay ningún documento activo en Revit.");

                var result = AuditParametersHandler.Execute(doc, modelReq);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return IpcResponse.Ok(request.Id, json);
            }

            case "CreateModelElement":
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var modelReq = JsonSerializer.Deserialize<CreateModelElementRequest>(request.Payload, options)
                    ?? throw new ArgumentException("Payload inválido para CreateModelElement");

                if (doc == null)
                    return IpcResponse.Fail(request.Id, "No hay ningún documento activo en Revit.");

                var result = CreateModelElementHandler.Execute(doc, modelReq);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return IpcResponse.Ok(request.Id, json);
            }

            case "ManageFamilyCreation":
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var modelReq = JsonSerializer.Deserialize<ManageFamilyCreationRequest>(request.Payload, options)
                    ?? throw new ArgumentException("Payload inválido para ManageFamilyCreation");

                var result = ManageFamilyCreationHandler.Execute(uiapp, modelReq);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return IpcResponse.Ok(request.Id, json);
            }

            case "InspectFamily":
            {
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null)
                    return IpcResponse.Fail(request.Id, "No hay ningún documento activo en Revit.");

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var modelReq = string.IsNullOrWhiteSpace(request.Payload)
                    ? new InspectFamilyRequest()
                    : (JsonSerializer.Deserialize<InspectFamilyRequest>(request.Payload, options) ?? new InspectFamilyRequest());

                var result = InspectFamilyHandler.Execute(uidoc, modelReq);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return IpcResponse.Ok(request.Id, json);
            }

            case "EditFamilyGeometry":
            {
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null)
                    return IpcResponse.Fail(request.Id, "No hay ningún documento activo en Revit.");

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var modelReq = JsonSerializer.Deserialize<EditFamilyGeometryRequest>(request.Payload, options)
                    ?? throw new ArgumentException("Payload inválido para EditFamilyGeometry");

                var result = EditFamilyGeometryHandler.Execute(uidoc, modelReq);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return result.Success ? IpcResponse.Ok(request.Id, json) : IpcResponse.Fail(request.Id, json);
            }

            case "CreateBeamStirrups":
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var modelReq = JsonSerializer.Deserialize<CreateBeamStirrupsRequest>(request.Payload, options)
                    ?? throw new ArgumentException("Payload inválido para CreateBeamStirrups");

                if (doc == null)
                    return IpcResponse.Fail(request.Id, "No hay ningún documento activo en Revit.");

                var result = CreateBeamStirrupsHandler.Execute(doc, modelReq);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return result.Success ? IpcResponse.Ok(request.Id, json) : IpcResponse.Fail(request.Id, json);
            }

            case "CreateWallRebar":
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var modelReq = JsonSerializer.Deserialize<CreateWallRebarRequest>(request.Payload, options)
                    ?? throw new ArgumentException("Payload inválido para CreateWallRebar");

                if (doc == null)
                    return IpcResponse.Fail(request.Id, "No hay ningún documento activo en Revit.");

                var result = CreateWallRebarHandler.Execute(doc, modelReq);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return result.Success ? IpcResponse.Ok(request.Id, json) : IpcResponse.Fail(request.Id, json);
            }

            case "CreateCapitalRebar":
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var modelReq = JsonSerializer.Deserialize<CreateCapitalRebarRequest>(request.Payload, options)
                    ?? throw new ArgumentException("Payload inválido para CreateCapitalRebar");

                if (doc == null)
                    return IpcResponse.Fail(request.Id, "No hay ningún documento activo en Revit.");

                var result = CreateCapitalRebarHandler.Execute(doc, modelReq);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return result.Success ? IpcResponse.Ok(request.Id, json) : IpcResponse.Fail(request.Id, json);
            }

            case "ToggleRebarFilter":
            {
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null)
                    return IpcResponse.Fail(request.Id, "No hay ningún documento activo en Revit.");

                var result = RebarFilterHandler.Toggle(uidoc);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return IpcResponse.Ok(request.Id, json);
            }

            case "Ping":
            {
                var statusInfo = new
                {
                    status = "Ready",
                    revitVersion = uiapp.Application.VersionNumber,
                    activeDocument = doc?.Title ?? "None",
                    isDocumentOpen = doc != null
                };
                return IpcResponse.Ok(request.Id, JsonSerializer.Serialize(statusInfo));
            }

            default:
                return IpcResponse.Fail(request.Id, $"Acción desconocida '{request.Action}'. Acciones disponibles: AuditParameters, CreateModelElement, ManageFamilyCreation, InspectFamily, EditFamilyGeometry, CreateBeamStirrups, CreateWallRebar, CreateCapitalRebar, ToggleRebarFilter, Ping.");
        }
    }

    public string GetName() => "RevitMcpExternalEventHandler";
}
