using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Addin.Ipc;
using RevitMcp.Core.Ipc;

namespace RevitMcp.Addin;

/// <summary>
/// Punto de entrada del Add-in de Revit (IExternalApplication).
/// Inicia el servidor IPC local al arrancar Revit, inicializa el ExternalEventHandler
/// e integra de forma armoniosa las herramientas en la pestaña personalizada "BIM Suite".
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class App : IExternalApplication
{
    public static App? Instance { get; private set; }
    public RevitExternalEventHandler? ExternalEventHandler { get; private set; }
    public NamedPipeIpcServer? IpcServer { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        Instance = this;

        try
        {
            // 1. Inicializar el manejador de eventos externos (IExternalEventHandler)
            ExternalEventHandler = new RevitExternalEventHandler();
            ExternalEventHandler.Initialize();

            // 2. Iniciar el Servidor IPC basado en Named Pipes
            IpcServer = new NamedPipeIpcServer(ExternalEventHandler);
            IpcServer.Start();

            // 3. Crear / Integrar herramientas en la pestaña "BIM Suite"
            CreateRibbonUi(application);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Revit MCP Error", $"Error al iniciar Revit MCP Add-in:\n{ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            IpcServer?.Stop();
            IpcServer?.Dispose();
            IpcServer = null;

            return Result.Succeeded;
        }
        catch
        {
            return Result.Failed;
        }
    }

    /// <summary>
    /// Configura los paneles y botones en la pestaña "BIM Suite".
    /// Se ejecuta después de que el módulo principal de BIM Suite (SolucionarTag)
    /// haya creado los paneles de "Anotaciones", "Seguridad BIM" y "Acero de Refuerzo" (con "Visibilidad Acero"),
    /// permitiendo que "Estribos en Vigas" se agregue como segundo botón dentro de "Acero de Refuerzo"
    /// sin reemplazar ni interferir con la herramienta existente.
    /// </summary>
    private void CreateRibbonUi(UIControlledApplication application)
    {
        const string tabName = "BIM Suite";

        try
        {
            application.CreateRibbonTab(tabName);
        }
        catch
        {
            // La pestaña ya existe
        }

        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        // 1. Panel "Integración MCP" (Conexión MCP)
        var mcpPanel = GetOrCreatePanel(application, tabName, "Integración MCP");
        var mcpButtonData = new PushButtonData(
            "McpServerStatusBtn",
            "Conexión\nMCP",
            assemblyPath,
            typeof(ShowMcpStatusCmd).FullName)
        {
            ToolTip = "Consulta el estado de conexión del servidor MCP e integración de IA.",
            LongDescription = $"Canal local IPC: {IpcConstants.PipeName}\nPermite al asistente de Inteligencia Artificial conectarse y sincronizarse con el modelo activo de Revit."
        };

        var mcpIcon32 = LoadBitmapImage("Resources/Icons/mcp_icon_32.png");
        if (mcpIcon32 != null) mcpButtonData.LargeImage = mcpIcon32;

        var mcpIcon16 = LoadBitmapImage("Resources/Icons/mcp_icon_16.png");
        if (mcpIcon16 != null) mcpButtonData.Image = mcpIcon16;

        mcpPanel.AddItem(mcpButtonData);

        // 2. Agregar "Estribos en Vigas" al panel "Acero de Refuerzo" AL LADO de "Visibilidad Acero"
        var rebarPanel = GetOrCreatePanel(application, tabName, "Acero de Refuerzo");
        bool hasStirrupsBtn = rebarPanel.GetItems().Any(it => it.Name == "CreateBeamStirrupsBtn");
        if (!hasStirrupsBtn)
        {
            var stirrupButtonData = new PushButtonData(
                "CreateBeamStirrupsBtn",
                "Estribos en\nVigas",
                assemblyPath,
                typeof(Commands.CreateBeamStirrupsCmd).FullName)
            {
                ToolTip = "Crea conjuntos de estribos zonificados para vigas de concreto armado.",
                LongDescription = "Herramienta de BIM Suite para el armado automático de vigas según criterio sismorresistente en 3 conjuntos de distribución (1 a 5 cm, N a 10 cm, resto a 20 cm)."
            };

            var stirrupIcon32 = LoadBitmapImage("Resources/Icons/stirrup_icon_32.png");
            if (stirrupIcon32 != null) stirrupButtonData.LargeImage = stirrupIcon32;

            var stirrupIcon16 = LoadBitmapImage("Resources/Icons/stirrup_icon_16.png");
            if (stirrupIcon16 != null) stirrupButtonData.Image = stirrupIcon16;

            rebarPanel.AddItem(stirrupButtonData);
        }

        // 3. Agregar "Filtro Acero" al panel "Acero de Refuerzo" (Botón On/Off por tipo)
        bool hasFilterBtn = rebarPanel.GetItems().Any(it => it.Name == "ToggleRebarFilterBtn");
        if (!hasFilterBtn)
        {
            var filterButtonData = new PushButtonData(
                "ToggleRebarFilterBtn",
                "Filtro\nAcero",
                assemblyPath,
                typeof(Commands.ToggleRebarFilterCmd).FullName)
            {
                ToolTip = "Activa o desactiva filtros de color por tipo para aceros de refuerzo en la vista activa.",
                LongDescription = "Aplica filtros automáticos de visualización estandarizados por tipo de armadura (diámetro) a la categoría Acero de Refuerzo en la vista activa, cubriendo tanto corte como proyección. Al pulsar nuevamente, desactiva los filtros restaurando la visualización original."
            };

            var filterIcon32 = LoadBitmapImage("Resources/Icons/rebar_filter_icon_32.png");
            if (filterIcon32 != null) filterButtonData.LargeImage = filterIcon32;

            var filterIcon16 = LoadBitmapImage("Resources/Icons/rebar_filter_icon_16.png");
            if (filterIcon16 != null) filterButtonData.Image = filterIcon16;

            rebarPanel.AddItem(filterButtonData);
        }

        // 4. Agregar "Acero en Muros" al panel "Acero de Refuerzo" (Mallas y tipologías)
        bool hasWallRebarBtn = rebarPanel.GetItems().Any(it => it.Name == "CreateWallRebarBtn");
        if (!hasWallRebarBtn)
        {
            var wallRebarButtonData = new PushButtonData(
                "CreateWallRebarBtn",
                "Acero en\nMuros",
                assemblyPath,
                typeof(Commands.CreateWallRebarCmd).FullName)
            {
                ToolTip = "Modela armaduras de refuerzo en muros de concreto (1 o 2 mallas y diversas tipologías).",
                LongDescription = "Herramienta de BIM Suite para el armado automatizado de muros y placas de concreto estructural. Permite configurar una o dos mallas (doble cortina), armadura vertical y horizontal paramétrica, remates con ganchos y elementos de confinamiento de borde (Boundary Elements)."
            };

            var wallRebarIcon32 = LoadBitmapImage("Resources/Icons/wall_rebar_icon_32.png");
            if (wallRebarIcon32 != null) wallRebarButtonData.LargeImage = wallRebarIcon32;

            var wallRebarIcon16 = LoadBitmapImage("Resources/Icons/wall_rebar_icon_16.png");
            if (wallRebarIcon16 != null) wallRebarButtonData.Image = wallRebarIcon16;

            rebarPanel.AddItem(wallRebarButtonData);
        }

        // 5. Agregar "Sardineles y Muretes" al panel "Acero de Refuerzo"
        bool hasCurbRebarBtn = rebarPanel.GetItems().Any(it => it.Name == "CreateCurbRebarBtn");
        if (!hasCurbRebarBtn)
        {
            var curbRebarButtonData = new PushButtonData(
                "CreateCurbRebarBtn",
                "Sardineles y\nMuretes",
                assemblyPath,
                typeof(Commands.CreateCurbRebarCmd).FullName)
            {
                ToolTip = "Modela estribos (barra recta en C con ganchos a 180° o cerrados a 135°) y armadura longitudinal de número fijo en sardineles y muretes bajos.",
                LongDescription = "Herramienta de BIM Suite para el armado automatizado de sardineles, muretes bajos y soleras de confinamiento. Configura estribos en C (barra recta con ganchos a 180°) para elementos delgados o estribos cerrados con ganchos sísmicos a 135° armados como viga (barras en esquinas), refuerzo longitudinal con regla nativa de Número Fijo y ganchos de anclaje hacia el sardinel contiguo en esquinas."
            };

            var curbIcon32 = LoadBitmapImage("Resources/Icons/curb_rebar_icon_32.png");
            if (curbIcon32 != null) curbRebarButtonData.LargeImage = curbIcon32;

            var curbIcon16 = LoadBitmapImage("Resources/Icons/curb_rebar_icon_16.png");
            if (curbIcon16 != null) curbRebarButtonData.Image = curbIcon16;

            rebarPanel.AddItem(curbRebarButtonData);
        }
    }

    private static RibbonPanel GetOrCreatePanel(UIControlledApplication application, string tabName, string panelName)
    {
        var existingPanels = application.GetRibbonPanels(tabName);
        foreach (var p in existingPanels)
        {
            if (p.Name.Equals(panelName, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }
        return application.CreateRibbonPanel(tabName, panelName);
    }

    private static BitmapImage? LoadBitmapImage(string relativePath)
    {
        try
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string fullPath = Path.Combine(assemblyDir, relativePath);

            if (File.Exists(fullPath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return bitmap;
            }
        }
        catch
        {
            // Fallback silencioso si no se encuentra la imagen
        }
        return null;
    }
}

/// <summary>
/// Comando de UI que muestra el estado del servidor MCP y modelo activo.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ShowMcpStatusCmd : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var app = App.Instance;
        bool isRunning = app?.IpcServer?.IsRunning ?? false;
        var doc = commandData.Application.ActiveUIDocument?.Document;

        var dialog = new TaskDialog("BIM Suite - Integración MCP")
        {
            MainInstruction = isRunning ? "Servidor MCP Activo" : "Servidor MCP Inactivo",
            MainContent = $"Canal IPC: {IpcConstants.PipeName}\n" +
                          $"Modelo Activo:\n{(doc != null ? doc.Title : "Ninguno abierto")}\n\n" +
                          (isRunning
                              ? "El servicio está conectado y disponible para interactuar con el modelo."
                              : "El servicio local se encuentra actualmente inactivo."),
            CommonButtons = TaskDialogCommonButtons.Close,
            DefaultButton = TaskDialogResult.Close,
            MainIcon = isRunning ? TaskDialogIcon.TaskDialogIconInformation : TaskDialogIcon.TaskDialogIconWarning
        };

        dialog.Show();
        return Result.Succeeded;
    }
}
