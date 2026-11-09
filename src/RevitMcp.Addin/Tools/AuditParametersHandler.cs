using System.IO;
using Autodesk.Revit.DB;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Manejador de la herramienta AuditParameters:
/// Filtra elementos por categoría y extrae los parámetros solicitados,
/// convirtiendo rigurosamente todas las longitudes/coordenadas de pies decimales a metros.
/// </summary>
public static class AuditParametersHandler
{
    public static AuditParametersResponse Execute(Document doc, AuditParametersRequest request)
    {
        if (doc == null)
            throw new InvalidOperationException("No hay ningún documento activo en Autodesk Revit.");

        if (string.IsNullOrWhiteSpace(request.CategoryName))
            throw new ArgumentException("El nombre de la categoría es obligatorio.", nameof(request.CategoryName));

        if (!CategoryResolver.TryResolve(doc, request.CategoryName, out var bic))
            throw new InvalidOperationException($"No se pudo resolver la categoría '{request.CategoryName}' en Revit.");

        var collector = new FilteredElementCollector(doc)
            .OfCategory(bic)
            .WhereElementIsNotElementType();

        var response = new AuditParametersResponse
        {
            Category = request.CategoryName
        };

        foreach (var element in collector)
        {
            var auditDto = new ElementAuditDto
            {
                ElementId = element.Id.Value,
                Name = element.Name,
                Category = element.Category?.Name ?? request.CategoryName
            };

            // Obtener el ElementType para parámetros de tipo (fallback si no está en la instancia)
            ElementType? elementType = null;
            var typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                elementType = doc.GetElement(typeId) as ElementType;
            }

            foreach (var paramName in request.ParameterNames)
            {
                var param = element.LookupParameter(paramName) ?? elementType?.LookupParameter(paramName);

                if (param == null || !param.HasValue)
                {
                    auditDto.Parameters[paramName] = null;
                    continue;
                }

                auditDto.Parameters[paramName] = ExtractParameterValue(param);
            }

            response.Elements.Add(auditDto);
        }

        response.Count = response.Elements.Count;
        return response;
    }

    /// <summary>
    /// Extrae el valor de un parámetro asegurando conversión automática a METROS para tipos de longitud/área.
    /// </summary>
    private static object? ExtractParameterValue(Parameter param)
    {
        switch (param.StorageType)
        {
            case StorageType.Double:
                double rawValue = param.AsDouble();
                ForgeTypeId specType = param.Definition.GetDataType();

                // Conversión estricta a unidades métricas (m, m², m³)
                if (specType == SpecTypeId.Length)
                {
                    return Math.Round(UnitConverter.FeetToMeters(rawValue), 4);
                }
                else if (specType == SpecTypeId.Area)
                {
                    return Math.Round(UnitConverter.SquareFeetToSquareMeters(rawValue), 4);
                }
                else if (specType == SpecTypeId.Volume)
                {
                    return Math.Round(UnitConverter.CubicFeetToCubicMeters(rawValue), 4);
                }

                // Fallback para otros doubles o formato de texto si está disponible
                var valueString = param.AsValueString();
                return !string.IsNullOrEmpty(valueString) ? valueString : rawValue;

            case StorageType.Integer:
                return param.AsInteger();

            case StorageType.String:
                return param.AsString();

            case StorageType.ElementId:
                var elemId = param.AsElementId();
                return elemId != null && elemId != ElementId.InvalidElementId ? elemId.Value : null;

            default:
                return param.AsValueString();
        }
    }
}
