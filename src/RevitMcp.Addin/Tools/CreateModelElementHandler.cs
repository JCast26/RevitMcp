using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Manejador de la herramienta CreateModelElement:
/// Convierte coordenadas y dimensiones estrictamente en METROS a pies decimales,
/// ejecuta una transacción en el modelo activo y crea elementos nativos o ejemplares de familia.
/// </summary>
public static class CreateModelElementHandler
{
    public static CreateModelElementResponse Execute(Document doc, CreateModelElementRequest request)
    {
        if (doc == null)
            throw new InvalidOperationException("No hay ningún documento activo en Autodesk Revit.");

        if (doc.IsReadOnly)
            throw new InvalidOperationException("El documento activo de Revit está en modo de sólo lectura.");

        // 1. Resolver el Nivel solicitado
        var level = FindLevel(doc, request.LevelName);
        if (level == null)
            throw new InvalidOperationException($"No se encontró el nivel '{request.LevelName}' en el proyecto.");

        long createdElementId;
        string resultMessage;

        // 2. Iniciar Transacción en el hilo principal de Revit
        using (var trans = new Transaction(doc, $"MCP: Crear {request.ElementType}"))
        {
            var status = trans.Start();
            if (status != TransactionStatus.Started)
                throw new InvalidOperationException($"No se pudo iniciar la transacción en Revit: {status}");

            if (request.ElementType.Equals("Wall", StringComparison.OrdinalIgnoreCase) ||
                request.ElementType.Equals("Muro", StringComparison.OrdinalIgnoreCase))
            {
                // Crear Muro Lineal
                if (request.StartPoint == null || request.EndPoint == null)
                    throw new ArgumentException("Para crear un muro se requieren 'startPoint' y 'endPoint' en metros.");

                // Conversión estricta: METROS -> PIES DECIMALES
                var startFeet = new XYZ(
                    UnitConverter.MetersToFeet(request.StartPoint.X),
                    UnitConverter.MetersToFeet(request.StartPoint.Y),
                    UnitConverter.MetersToFeet(request.StartPoint.Z));

                var endFeet = new XYZ(
                    UnitConverter.MetersToFeet(request.EndPoint.X),
                    UnitConverter.MetersToFeet(request.EndPoint.Y),
                    UnitConverter.MetersToFeet(request.EndPoint.Z));

                var curve = Line.CreateBound(startFeet, endFeet);
                var wall = Wall.Create(doc, curve, level.Id, false);

                // Si se especifica altura (en metros), convertir y asignar
                if (request.Height.HasValue && request.Height.Value > 0)
                {
                    double heightFeet = UnitConverter.MetersToFeet(request.Height.Value);
                    var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    heightParam?.Set(heightFeet);
                }

                createdElementId = wall.Id.Value;
                resultMessage = $"Muro creado exitosamente (Id: {createdElementId}) desde {request.StartPoint} hasta {request.EndPoint} en el nivel '{level.Name}'.";
            }
            else
            {
                // Crear FamilyInstance (ej. Column, Furniture, Desk, etc.)
                if (request.Location == null)
                    throw new ArgumentException("Para crear una instancia de familia se requiere 'location' en metros.");

                var symbol = FindFamilySymbol(doc, request.FamilyName, request.TypeName);
                if (symbol == null)
                    throw new InvalidOperationException($"No se encontró un tipo de familia adecuado para '{request.FamilyName}' / '{request.TypeName}'.");

                if (!symbol.IsActive)
                    symbol.Activate();

                // Conversión estricta: METROS -> PIES DECIMALES
                var locFeet = new XYZ(
                    UnitConverter.MetersToFeet(request.Location.X),
                    UnitConverter.MetersToFeet(request.Location.Y),
                    UnitConverter.MetersToFeet(request.Location.Z));

                var structuralType = request.ElementType.Contains("Column", StringComparison.OrdinalIgnoreCase)
                    ? StructuralType.Column
                    : StructuralType.NonStructural;

                var instance = doc.Create.NewFamilyInstance(locFeet, symbol, level, structuralType);
                createdElementId = instance.Id.Value;
                resultMessage = $"Elemento {request.ElementType} instanciado exitosamente (Id: {createdElementId}) en {request.Location} en el nivel '{level.Name}'.";
            }

            var commitStatus = trans.Commit();
            if (commitStatus != TransactionStatus.Committed)
                throw new InvalidOperationException($"La transacción no pudo completarse: {commitStatus}");
        }

        return new CreateModelElementResponse
        {
            Success = true,
            ElementId = createdElementId,
            ElementType = request.ElementType,
            Message = resultMessage
        };
    }

    private static Level? FindLevel(Document doc, string? levelName)
    {
        var collector = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>();

        if (!string.IsNullOrWhiteSpace(levelName))
        {
            var match = collector.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        // Fallback al primer nivel disponible si no se indicó o no coincidió exactamente
        return collector.FirstOrDefault();
    }

    private static FamilySymbol? FindFamilySymbol(Document doc, string? familyName, string? typeName)
    {
        var symbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>();

        if (!string.IsNullOrWhiteSpace(familyName) && !string.IsNullOrWhiteSpace(typeName))
        {
            var exact = symbols.FirstOrDefault(s =>
                s.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase) &&
                s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        if (!string.IsNullOrWhiteSpace(familyName))
        {
            var byFamily = symbols.FirstOrDefault(s => s.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase));
            if (byFamily != null) return byFamily;
        }

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var byType = symbols.FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (byType != null) return byType;
        }

        return symbols.FirstOrDefault();
    }
}
