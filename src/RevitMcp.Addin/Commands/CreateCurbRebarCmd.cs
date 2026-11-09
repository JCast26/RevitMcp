using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMcp.Addin.Tools;
using RevitMcp.Addin.UI;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin.Commands;

/// <summary>
/// Comando Ribbon de Revit para abrir la interfaz interactiva de modelado de sardineles y muretes bajos.
/// Permite configurar estribos en C (180°) o cerrados (135°), armadura longitudinal (Número Fijo) y doblado en esquinas.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class CreateCurbRebarCmd : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("BIM Suite - Sardineles", "No hay ningún documento activo en Revit.");
                return Result.Cancelled;
            }

            var doc = uidoc.Document;

            // Detectar si el usuario ya tenía sardineles seleccionados antes de presionar el botón
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

            CreateCurbRebarRequest? cachedRequest = null;
            try
            {
                cachedRequest = CurbRebarSettingsStore.Load();
            }
            catch { }

            // Bucle de interacción seguro con selección en Revit
            while (true)
            {
                var window = new CurbRebarWindow(uidoc, selectedIds, cachedRequest);
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
                            "Seleccione uno o varios sardineles / muros de concreto y presione Finalizar");

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

                // Caso B: El usuario pulsó "Armar Sardineles"
                if (dialogResult == true && window.BuiltRequest != null)
                {
                    var response = CreateCurbRebarHandler.Execute(doc, window.BuiltRequest);

                    if (response.Success)
                    {
                        TaskDialog.Show("BIM Suite - Sardineles", response.Message);
                        return Result.Succeeded;
                    }
                    else
                    {
                        TaskDialog.Show("BIM Suite - Sardineles (Aviso)", response.Message);
                        return Result.Failed;
                    }
                }

                // Caso C: Cancelar o cerrar la ventana
                return Result.Cancelled;
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("BIM Suite - Error en Sardineles", $"Ocurrió un error inesperado:\n{ex.Message}");
            return Result.Failed;
        }
    }
}
