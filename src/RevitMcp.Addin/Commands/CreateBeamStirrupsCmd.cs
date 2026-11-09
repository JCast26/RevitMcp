using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMcp.Addin.Tools;
using RevitMcp.Addin.UI;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin.Commands;

/// <summary>
/// Comando Ribbon de Revit para abrir la interfaz de creación de estribos zonificados en vigas.
/// Gestiona la selección interactiva y garantiza que todas las transacciones de Revit
/// se ejecuten estrictamente en el contexto nativo de la API de Revit para evitar cierres fatales.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class CreateBeamStirrupsCmd : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc == null)
        {
            TaskDialog.Show("BIM Suite - Acero de Refuerzo", "No hay ningún documento activo en Revit.");
            return Result.Cancelled;
        }

        var doc = uidoc.Document;

        // Detectar si el usuario ya tenía vigas seleccionadas antes de pulsar el botón
        var currentSelection = uidoc.Selection.GetElementIds();
        var selectedIds = new List<long>();
        foreach (var id in currentSelection)
        {
            var elem = doc.GetElement(id);
            if (elem is FamilyInstance fi && fi.Category?.Id.Value == (int)BuiltInCategory.OST_StructuralFraming)
            {
                selectedIds.Add(id.Value);
            }
        }

        CreateBeamStirrupsRequest? cachedRequest = BeamStirrupsSettingsStore.Load();

        // Bucle de interacción seguro: desacopla la UI de WPF de las llamadas nativas de Revit
        while (true)
        {
            var window = new BeamStirrupsWindow(uidoc, selectedIds, cachedRequest);
            bool? dialogResult = window.ShowDialog();

            // Guardar valores del formulario por si el usuario cambia la selección
            cachedRequest = window.BuiltRequest;

            // Caso A: El usuario pulsó "Seleccionar en Revit"
            if (window.UserWantsToPickBeams)
            {
                try
                {
                    var filter = new StructuralFramingSelectionFilter();
                    var refs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        filter,
                        "Seleccione una o varias vigas de concreto y presione Finalizar en la barra de opciones superior");

                    var pickedIds = refs.Select(r => doc.GetElement(r).Id.Value).Distinct().ToList();
                    if (pickedIds.Count > 0)
                    {
                        selectedIds = pickedIds;
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    // Cancelado con Escape: conserva la selección previa
                }

                // Vuelve a abrir la ventana en el siguiente ciclo del bucle
                continue;
            }

            // Caso B: El usuario pulsó "Crear Estribos"
            if (dialogResult == true && window.BuiltRequest != null)
            {
                try
                {
                    // Ejecución 100% segura en el hilo nativo de Revit API
                    var resp = CreateBeamStirrupsHandler.Execute(doc, window.BuiltRequest);

                    if (resp.Success)
                    {
                        int totalStirrups = resp.Results.Sum(r => r.TotalStirrupsCount);
                        TaskDialog.Show("BIM Suite - Estribos en Vigas",
                            $"Operación Exitosa:\n\n" +
                            $"• Vigas armadas: {resp.TotalBeamsProcessed}\n" +
                            $"• Total de estribos colocados: {totalStirrups}\n\n" +
                            $"Los 3 conjuntos de estribos han sido generados correctamente.");
                        return Result.Succeeded;
                    }
                    else
                    {
                        TaskDialog.Show("BIM Suite - Aviso", $"No se pudieron crear los estribos:\n{resp.Message}");
                        return Result.Failed;
                    }
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("BIM Suite - Error", $"Ocurrió un error inesperado al procesar las vigas:\n{ex.Message}");
                    message = ex.Message;
                    return Result.Failed;
                }
            }

            // Caso C: El usuario cerró o canceló la ventana
            return Result.Cancelled;
        }
    }
}
