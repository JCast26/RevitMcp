using System.Text;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMcp.Addin.Tools;
using RevitMcp.Addin.UI;

namespace RevitMcp.Addin.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class CreateCapitalRebarCmd : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        var uidoc = uiApp.ActiveUIDocument;
        if (uidoc == null)
        {
            message = "No hay ningún documento activo en Revit.";
            return Result.Failed;
        }

        var doc = uidoc.Document;

        var selectedCapitals = uidoc.Selection.GetElementIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilyInstance>()
            .Where(fi => fi.Category != null && fi.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns)
            .ToList();

        while (true)
        {
            var window = new CapitalRebarWindow(doc, selectedCapitals);
            new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;

            bool? dialogResult = window.ShowDialog();

            if (window.UserWantsToPickCapitals)
            {
                try
                {
                    var pickedRefs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        new CapitalSelectionFilter(),
                        "Seleccione los capiteles (Structural Columns) a armar y presione Finalizar.");

                    if (pickedRefs != null && pickedRefs.Count > 0)
                    {
                        selectedCapitals = pickedRefs
                            .Select(r => doc.GetElement(r.ElementId))
                            .OfType<FamilyInstance>()
                            .Where(fi => fi.Category != null && fi.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns)
                            .GroupBy(fi => fi.Id.Value)
                            .Select(g => g.First())
                            .ToList();
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    // El usuario presionó ESC durante la selección; volver a mostrar la ventana.
                }
                continue;
            }

            if (dialogResult != true || window.BuiltRequest == null)
            {
                return Result.Cancelled;
            }

            var response = CreateCapitalRebarHandler.Execute(doc, window.BuiltRequest);

            var sb = new StringBuilder();
            sb.AppendLine(response.Message);
            sb.AppendLine();

            foreach (var r in response.Results)
            {
                if (r.Status == "OK")
                {
                    sb.AppendLine($"• [{r.ColumnElementId}] {r.ColumnName} ({r.WidthMeters:0.00}×{r.DepthMeters:0.00}×{r.HeightMeters:0.00} m): {r.VerticalBarsCount} barras vert. (90°) + {r.StirrupsCount} estribos (135°).");
                }
                else
                {
                    sb.AppendLine($"• [{r.ColumnElementId}] {r.Status}: {r.ErrorMessage}");
                }
            }

            var td = new TaskDialog("BIM Suite — Armado de Capiteles")
            {
                MainInstruction = response.Success
                    ? "Armado de capiteles completado"
                    : "No se pudo completar el armado",
                MainContent = sb.ToString().TrimEnd(),
                MainIcon = response.Success ? TaskDialogIcon.TaskDialogIconInformation : TaskDialogIcon.TaskDialogIconWarning
            };
            td.Show();

            return response.Success ? Result.Succeeded : Result.Failed;
        }
    }
}
