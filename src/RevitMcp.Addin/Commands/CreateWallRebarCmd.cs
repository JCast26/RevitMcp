using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMcp.Addin.Tools;
using RevitMcp.Addin.UI;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin.Commands;

/// <summary>
/// Comando Ribbon de Revit para abrir la interfaz interactiva de modelado de acero en muros.
/// Permite configurar 1 o 2 mallas y diversas tipologías estructurales (estándar, ganchos y confinamiento de borde).
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class CreateWallRebarCmd : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("BIM Suite - Acero en Muros", "No hay ningún documento activo en Revit.");
                return Result.Cancelled;
            }

            var doc = uidoc.Document;

            // Detectar si el usuario ya tenía muros seleccionados antes de presionar el botón
            var currentSelection = uidoc.Selection?.GetElementIds();
            var selectedIds = new List<long>();
            if (currentSelection != null)
            {
                foreach (var id in currentSelection)
                {
                    var elem = doc.GetElement(id);
                    if (elem is Wall)
                    {
                        selectedIds.Add(id.Value);
                    }
                }
            }

            CreateWallRebarRequest? cachedRequest = null;
            try
            {
                cachedRequest = WallRebarSettingsStore.Load();
            }
            catch
            {
                // Si la configuración no existe o está corrupta, usar por defecto
            }

            // Bucle de interacción seguro: desacopla la UI de WPF de las operaciones nativas de Revit
            while (true)
            {
                var window = new WallRebarWindow(uidoc, selectedIds, cachedRequest);
                bool? dialogResult = window.ShowDialog();

                cachedRequest = window.BuiltRequest;

                // Caso A: El usuario pulsó "Seleccionar en Revit"
                if (window.UserWantsToPickWalls)
                {
                    try
                    {
                        var filter = new WallSelectionFilter();
                        var refs = uidoc.Selection?.PickObjects(
                            ObjectType.Element,
                            filter,
                            "Seleccione uno o varios muros de concreto y presione Finalizar en la barra de opciones superior");

                        if (refs != null)
                        {
                            var pickedIds = refs
                                .Select(r => doc.GetElement(r))
                                .Where(e => e != null)
                                .Select(e => e.Id.Value)
                                .Distinct()
                                .ToList();

                            if (pickedIds.Count > 0)
                            {
                                selectedIds = pickedIds;
                            }
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        // Cancelado con Escape: conserva la selección previa
                    }

                    continue;
                }

                // Caso B: El usuario pulsó "Crear Acero en Muros"
                if (dialogResult == true && window.BuiltRequest != null)
                {
                    try
                    {
                        var resp = CreateWallRebarHandler.Execute(doc, window.BuiltRequest);

                        if (resp.Success)
                        {
                            int totalRebars = resp.Results.Sum(r => r.TotalRebarElementsCount);
                            int totalBars = resp.Results.Sum(r => r.VerticalBarsCount + r.HorizontalBarsCount);
                            string tipologyName = window.BuiltRequest.Typology switch
                            {
                                WallRebarTypology.EndHooks => "Malla con Remate de Ganchos (U-Hooks)",
                                WallRebarTypology.BoundaryElements => "Confinamiento de Borde (Boundary Elements)",
                                WallRebarTypology.InclinedStairWall => "Muro Inclinado de Escalera (Varying Rebar Set)",
                                _ => "Malla Continua Estándar"
                            };

                            TaskDialog.Show("BIM Suite - Acero en Muros",
                                $"Operación Exitosa:\n\n" +
                                $"• Muros armados: {resp.TotalWallsProcessed}\n" +
                                $"• Mallas por muro: {window.BuiltRequest.NumberOfMeshes}\n" +
                                $"• Tipología: {tipologyName}\n" +
                                $"• Total de conjuntos generados: {totalRebars}\n" +
                                $"• Total aproximado de varillas: {totalBars}\n\n" +
                                $"El refuerzo en muros ha sido modelado y configurado en 3D correctamente.");
                            return Result.Succeeded;
                        }
                        else
                        {
                            TaskDialog.Show("BIM Suite - Aviso", $"No se pudo completar el armado de muros:\n{resp.Message}");
                            return Result.Failed;
                        }
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("BIM Suite - Error", $"Ocurrió un error al generar las armaduras:\n{ex.Message}\n\nDetalles:\n{ex.StackTrace}");
                        return Result.Failed;
                    }
                }

                return Result.Cancelled;
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("BIM Suite - Error Inesperado", $"Error al inicializar la herramienta de Acero en Muros:\n{ex.Message}\n\nDetalles:\n{ex.StackTrace}");
            return Result.Failed;
        }
    }
}
