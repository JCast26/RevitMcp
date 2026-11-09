using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Gestiona la activación y desactivación de filtros cromáticos por tipo de barra
/// exclusivamente para la categoría Acero de Refuerzo (OST_Rebar) en la vista activa de Revit.
/// Configura modificaciones gráficas idénticas tanto para Proyección como para Corte.
/// </summary>
public static class RebarFilterHandler
{
    public const string FilterPrefix = "Filtro Acero - ";

    public record ToggleResult(bool IsActive, int FilterCount, string Message);

    /// <summary>
    /// Alterna el estado (On / Off) de los filtros por tipo de armadura en la vista activa.
    /// </summary>
    public static ToggleResult Toggle(UIDocument uidoc)
    {
        var doc = uidoc.Document;
        var view = uidoc.ActiveView;

        if (view == null)
        {
            return new ToggleResult(false, 0, "No hay ninguna vista activa seleccionada.");
        }

        if (!view.AreGraphicsOverridesAllowed())
        {
            return new ToggleResult(false, 0,
                $"La vista activa '{view.Name}' no admite filtros de modificación gráfica (ej. planos o tablas de planificación).");
        }

        // 1. Verificar si la vista tiene plantilla asignada que bloquee filtros
        if (view.ViewTemplateId != ElementId.InvalidElementId)
        {
            var template = doc.GetElement(view.ViewTemplateId) as View;
            if (template != null)
            {
                // Comprobamos si la plantilla controla los filtros V/G
                var nonControlled = template.GetNonControlledTemplateParameterIds();
                var vgFiltersParamId = new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS);
                bool filtersControlledByTemplate = !nonControlled.Contains(vgFiltersParamId);

                if (filtersControlledByTemplate)
                {
                    return new ToggleResult(false, 0,
                        $"La vista activa tiene asignada la plantilla '{template.Name}' que controla los filtros de visualización.\n\n" +
                        "Para activar o desactivar Filtro Acero, desactiva la casilla 'Modificaciones de V/G (Filtros)' en las propiedades de la plantilla de vista.");
                }
            }
        }

        // 2. Analizar filtros existentes en la vista activa
        var appliedFilterIds = view.GetFilters();
        var rebarFiltersInView = new List<ElementId>();

        foreach (var filterId in appliedFilterIds)
        {
            var filterElem = doc.GetElement(filterId) as ParameterFilterElement;
            if (filterElem != null && filterElem.Name.StartsWith(FilterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                rebarFiltersInView.Add(filterId);
            }
        }

        // ESTADO ON -> PASAR A OFF (Desactivar filtros en la vista activa)
        if (rebarFiltersInView.Count > 0)
        {
            using (var tx = new Transaction(doc, "BIM Suite - Desactivar Filtro Acero"))
            {
                tx.Start();
                foreach (var filterId in rebarFiltersInView)
                {
                    view.RemoveFilter(filterId);
                }
                tx.Commit();
            }

            return new ToggleResult(false, rebarFiltersInView.Count,
                $"Filtros por tipo de acero DESACTIVADOS en la vista actual ('{view.Name}').\nSe removieron {rebarFiltersInView.Count} filtros de armadura.");
        }

        // ESTADO OFF -> PASAR A ON (Crear y aplicar filtros por tipo en corte y proyección)
        var barTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .OrderBy(b => b.BarModelDiameter)
            .ToList();

        if (barTypes.Count == 0)
        {
            return new ToggleResult(false, 0,
                "No se encontraron tipos de acero de refuerzo (RebarBarType) en el modelo.");
        }

        // Buscar el patrón de relleno sólido independiente del idioma de Revit
        var solidPattern = new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

        // Diccionario de filtros existentes en el documento
        var allDocFilters = new FilteredElementCollector(doc)
            .OfClass(typeof(ParameterFilterElement))
            .Cast<ParameterFilterElement>()
            .ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);

        var rebarCategories = new List<ElementId> { new ElementId(BuiltInCategory.OST_Rebar) };
        int appliedCount = 0;

        using (var tx = new Transaction(doc, "BIM Suite - Activar Filtro Acero"))
        {
            tx.Start();

            foreach (var barType in barTypes)
            {
                string filterName = $"{FilterPrefix}{barType.Name}";
                ParameterFilterElement filterElem;

                if (!allDocFilters.TryGetValue(filterName, out filterElem!))
                {
                    // Regla de igualdad por nombre de tipo (BuiltInParameter.ALL_MODEL_TYPE_NAME)
                    var rule = ParameterFilterRuleFactory.CreateEqualsRule(
                        new ElementId(BuiltInParameter.ALL_MODEL_TYPE_NAME),
                        barType.Name);

                    var elemFilter = new ElementParameterFilter(rule);
                    filterElem = ParameterFilterElement.Create(doc, filterName, rebarCategories, elemFilter);
                    allDocFilters[filterName] = filterElem;
                }

                // Asignar color estándar según diámetro
                var color = RebarColorStandard.GetColorForRebarType(barType);

                // Configurar modificaciones gráficas completas (Corte + Proyección)
                var overrides = new OverrideGraphicSettings();

                // 1. Líneas de Proyección y Superficie
                overrides.SetProjectionLineColor(color);

                // 2. Líneas de Corte
                overrides.SetCutLineColor(color);

                // 3. Patrón de superficie y corte (Sólido)
                if (solidPattern != null)
                {
                    // Proyección / Superficie
                    overrides.SetSurfaceForegroundPatternId(solidPattern.Id);
                    overrides.SetSurfaceForegroundPatternColor(color);
                    overrides.SetSurfaceForegroundPatternVisible(true);

                    // Corte
                    overrides.SetCutForegroundPatternId(solidPattern.Id);
                    overrides.SetCutForegroundPatternColor(color);
                    overrides.SetCutForegroundPatternVisible(true);
                }

                // Aplicar a la vista activa
                if (!view.IsFilterApplied(filterElem.Id))
                {
                    view.AddFilter(filterElem.Id);
                }

                view.SetFilterOverrides(filterElem.Id, overrides);
                view.SetFilterVisibility(filterElem.Id, true);
                appliedCount++;
            }

            tx.Commit();
        }

        return new ToggleResult(true, appliedCount,
            $"Filtros por tipo de acero ACTIVADOS en la vista actual ('{view.Name}').\n" +
            $"Se configuraron {appliedCount} tipos de varilla con color estandarizado en corte y proyección.");
    }
}
