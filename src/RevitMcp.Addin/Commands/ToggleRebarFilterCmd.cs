using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Addin.Tools;

namespace RevitMcp.Addin.Commands;

/// <summary>
/// Comando de la cinta de opciones (Ribbon) de BIM Suite para alternar (On/Off)
/// los filtros cromáticos por tipo en la categoría Acero de Refuerzo.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ToggleRebarFilterCmd : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc == null)
        {
            TaskDialog.Show("BIM Suite - Filtro Acero", "No hay ningún documento abierto en Revit.");
            return Result.Cancelled;
        }

        try
        {
            var result = RebarFilterHandler.Toggle(uidoc);

            var dialog = new TaskDialog("BIM Suite - Filtro Acero")
            {
                MainInstruction = result.IsActive ? "Filtro Acero: ACTIVADO" : "Filtro Acero: DESACTIVADO",
                MainContent = result.Message,
                CommonButtons = TaskDialogCommonButtons.Ok,
                DefaultButton = TaskDialogResult.Ok,
                MainIcon = result.IsActive ? TaskDialogIcon.TaskDialogIconInformation : TaskDialogIcon.TaskDialogIconNone
            };

            dialog.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("BIM Suite - Error Filtro Acero", $"Ocurrió un error al alternar los filtros de acero:\n{ex.Message}");
            return Result.Failed;
        }
    }
}
