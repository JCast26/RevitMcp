using System.Text.Json.Serialization;

namespace RevitMcp.Core.Models;

// =========================================================================
// InspectFamily DTOs
// =========================================================================

/// <summary>
/// Solicitud para inspeccionar el documento de familia (.rfa) activo en Revit.
/// </summary>
public class InspectFamilyRequest
{
    [JsonPropertyName("elementIds")]
    public List<long>? ElementIds { get; set; }

    [JsonPropertyName("selectedOnly")]
    public bool SelectedOnly { get; set; }

    [JsonPropertyName("includeParameters")]
    public bool IncludeParameters { get; set; } = true;

    [JsonPropertyName("includeProfileCurves")]
    public bool IncludeProfileCurves { get; set; } = true;

    [JsonPropertyName("includeReferencePlanes")]
    public bool IncludeReferencePlanes { get; set; } = true;

    [JsonPropertyName("includeDimensions")]
    public bool IncludeDimensions { get; set; } = true;
}

/// <summary>
/// Caja delimitadora (BoundingBox) expresada en METROS.
/// </summary>
public class BoundingBoxMetersDto
{
    [JsonPropertyName("min")]
    public Point3D Min { get; set; } = new();

    [JsonPropertyName("max")]
    public Point3D Max { get; set; } = new();

    [JsonPropertyName("size")]
    public Point3D Size { get; set; } = new();

    [JsonPropertyName("center")]
    public Point3D Center { get; set; } = new();
}

/// <summary>
/// Segmento de curva perteneciente a un croquis/perfil de familia (en METROS).
/// </summary>
public class SketchSegmentDto
{
    [JsonPropertyName("curveType")]
    public string CurveType { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public Point3D Start { get; set; } = new();

    [JsonPropertyName("end")]
    public Point3D End { get; set; } = new();

    [JsonPropertyName("midPoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Point3D? MidPoint { get; set; }

    [JsonPropertyName("center")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Point3D? Center { get; set; }

    [JsonPropertyName("radiusMeters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RadiusMeters { get; set; }
}

/// <summary>
/// Representación geométrica de una forma 3D (Extrusion, Blend, Revolution, Sweep, etc.) en la familia.
/// </summary>
public class FamilyFormDto
{
    [JsonPropertyName("elementId")]
    public long ElementId { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isSolid")]
    public bool IsSolid { get; set; }

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; }

    [JsonPropertyName("boundingBox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoundingBoxMetersDto? BoundingBox { get; set; }

    [JsonPropertyName("startOffsetMeters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StartOffsetMeters { get; set; }

    [JsonPropertyName("endOffsetMeters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EndOffsetMeters { get; set; }

    [JsonPropertyName("startAngleDegrees")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StartAngleDegrees { get; set; }

    [JsonPropertyName("endAngleDegrees")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EndAngleDegrees { get; set; }

    [JsonPropertyName("sketchPlaneOrigin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Point3D? SketchPlaneOrigin { get; set; }

    [JsonPropertyName("sketchPlaneNormal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Point3D? SketchPlaneNormal { get; set; }

    [JsonPropertyName("profileLoops")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<List<SketchSegmentDto>>? ProfileLoops { get; set; }

    [JsonPropertyName("topProfileLoops")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<List<SketchSegmentDto>>? TopProfileLoops { get; set; }

    [JsonPropertyName("associatedParameters")]
    public Dictionary<string, string?> AssociatedParameters { get; set; } = new();
}

/// <summary>
/// Parámetro definido en el FamilyManager de la familia activa.
/// </summary>
public class FamilyParameterDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;

    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = string.Empty;

    [JsonPropertyName("storageType")]
    public string StorageType { get; set; } = string.Empty;

    [JsonPropertyName("isInstance")]
    public bool IsInstance { get; set; }

    [JsonPropertyName("isReporting")]
    public bool IsReporting { get; set; }

    [JsonPropertyName("formula")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Formula { get; set; }

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

/// <summary>
/// Plano de referencia en el Editor de Familias.
/// </summary>
public class ReferencePlaneDto
{
    [JsonPropertyName("elementId")]
    public long ElementId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("bubbleEnd")]
    public Point3D BubbleEnd { get; set; } = new();

    [JsonPropertyName("freeEnd")]
    public Point3D FreeEnd { get; set; } = new();

    [JsonPropertyName("normal")]
    public Point3D Normal { get; set; } = new();

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; }
}

/// <summary>
/// Cota / Dimensión dentro de la familia.
/// </summary>
public class FamilyDimensionDto
{
    [JsonPropertyName("elementId")]
    public long ElementId { get; set; }

    [JsonPropertyName("valueMeters")]
    public double? ValueMeters { get; set; }

    [JsonPropertyName("isLocked")]
    public bool? IsLocked { get; set; }

    [JsonPropertyName("labelParameterName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LabelParameterName { get; set; }
}

/// <summary>
/// Familia anidada instanciada dentro de la familia actual.
/// </summary>
public class NestedInstanceDto
{
    [JsonPropertyName("elementId")]
    public long ElementId { get; set; }

    [JsonPropertyName("familyName")]
    public string FamilyName { get; set; } = string.Empty;

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Point3D? Location { get; set; }

    [JsonPropertyName("boundingBox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoundingBoxMetersDto? BoundingBox { get; set; }

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; }
}

/// <summary>
/// Respuesta completa de inspección de la familia activa.
/// </summary>
public class InspectFamilyResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("familyTitle")]
    public string FamilyTitle { get; set; } = string.Empty;

    [JsonPropertyName("familyCategory")]
    public string FamilyCategory { get; set; } = string.Empty;

    [JsonPropertyName("currentTypeName")]
    public string? CurrentTypeName { get; set; }

    [JsonPropertyName("types")]
    public List<string> Types { get; set; } = new();

    [JsonPropertyName("selectedElementIds")]
    public List<long> SelectedElementIds { get; set; } = new();

    [JsonPropertyName("parameters")]
    public List<FamilyParameterDto> Parameters { get; set; } = new();

    [JsonPropertyName("forms")]
    public List<FamilyFormDto> Forms { get; set; } = new();

    [JsonPropertyName("referencePlanes")]
    public List<ReferencePlaneDto> ReferencePlanes { get; set; } = new();

    [JsonPropertyName("dimensions")]
    public List<FamilyDimensionDto> Dimensions { get; set; } = new();

    [JsonPropertyName("nestedInstances")]
    public List<NestedInstanceDto> NestedInstances { get; set; } = new();
}

// =========================================================================
// EditFamilyGeometry DTOs
// =========================================================================

public class RectangleProfileDto
{
    [JsonPropertyName("centerMeters")]
    public Point3D? CenterMeters { get; set; }

    [JsonPropertyName("widthMeters")]
    public double? WidthMeters { get; set; }

    [JsonPropertyName("depthMeters")]
    public double? DepthMeters { get; set; }

    [JsonPropertyName("minX")]
    public double? MinX { get; set; }

    [JsonPropertyName("minY")]
    public double? MinY { get; set; }

    [JsonPropertyName("maxX")]
    public double? MaxX { get; set; }

    [JsonPropertyName("maxY")]
    public double? MaxY { get; set; }
}

public class CircleProfileDto
{
    [JsonPropertyName("centerMeters")]
    public Point3D? CenterMeters { get; set; }

    [JsonPropertyName("radiusMeters")]
    public double RadiusMeters { get; set; }
}

/// <summary>
/// Solicitud unificada para modificar geométricamente o paramétricamente la familia activa (.rfa).
/// Todas las longitudes, desplazamientos y coordenadas se especifican en METROS; los ángulos en GRADOS.
/// </summary>
public class EditFamilyGeometryRequest
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("elementId")]
    public long? ElementId { get; set; }

    [JsonPropertyName("elementIds")]
    public List<long>? ElementIds { get; set; }

    [JsonPropertyName("useSelection")]
    public bool UseSelection { get; set; }

    [JsonPropertyName("unpinIfNeeded")]
    public bool UnpinIfNeeded { get; set; }

    // Move
    [JsonPropertyName("translationMeters")]
    public Point3D? TranslationMeters { get; set; }

    // Rotate
    [JsonPropertyName("axisOriginMeters")]
    public Point3D? AxisOriginMeters { get; set; }

    [JsonPropertyName("axisDirection")]
    public Point3D? AxisDirection { get; set; }

    [JsonPropertyName("angleDegrees")]
    public double? AngleDegrees { get; set; }

    // Mirror
    [JsonPropertyName("planeOriginMeters")]
    public Point3D? PlaneOriginMeters { get; set; }

    [JsonPropertyName("planeNormal")]
    public Point3D? PlaneNormal { get; set; }

    [JsonPropertyName("copy")]
    public bool Copy { get; set; }

    // SetExtrusionBounds / CreateExtrusion
    [JsonPropertyName("startOffsetMeters")]
    public double? StartOffsetMeters { get; set; }

    [JsonPropertyName("endOffsetMeters")]
    public double? EndOffsetMeters { get; set; }

    [JsonPropertyName("startAngleDegrees")]
    public double? StartAngleDegrees { get; set; }

    [JsonPropertyName("endAngleDegrees")]
    public double? EndAngleDegrees { get; set; }

    [JsonPropertyName("isSolid")]
    public bool? IsSolid { get; set; }

    // ScaleProfile
    [JsonPropertyName("scaleX")]
    public double? ScaleX { get; set; }

    [JsonPropertyName("scaleY")]
    public double? ScaleY { get; set; }

    [JsonPropertyName("scaleFactor")]
    public double? ScaleFactor { get; set; }

    [JsonPropertyName("pivotMeters")]
    public Point3D? PivotMeters { get; set; }

    // OffsetProfile
    [JsonPropertyName("offsetMeters")]
    public double? OffsetMeters { get; set; }

    // ReplaceExtrusionProfile / CreateExtrusion
    [JsonPropertyName("profileLoops")]
    public List<List<Point3D>>? ProfileLoops { get; set; }

    [JsonPropertyName("rectangle")]
    public RectangleProfileDto? Rectangle { get; set; }

    [JsonPropertyName("circle")]
    public CircleProfileDto? Circle { get; set; }

    [JsonPropertyName("sketchPlaneOriginMeters")]
    public Point3D? SketchPlaneOriginMeters { get; set; }

    [JsonPropertyName("sketchPlaneNormal")]
    public Point3D? SketchPlaneNormal { get; set; }

    // SetFamilyParameter
    [JsonPropertyName("parameterName")]
    public string? ParameterName { get; set; }

    [JsonPropertyName("familyTypeName")]
    public string? FamilyTypeName { get; set; }

    [JsonPropertyName("numericValue")]
    public double? NumericValue { get; set; }

    [JsonPropertyName("stringValue")]
    public string? StringValue { get; set; }

    [JsonPropertyName("integerValue")]
    public int? IntegerValue { get; set; }

    [JsonPropertyName("booleanValue")]
    public bool? BooleanValue { get; set; }

    [JsonPropertyName("formula")]
    public string? Formula { get; set; }
}

/// <summary>
/// Resultado de una operación de modificación geométrica en la familia activa.
/// </summary>
public class EditFamilyGeometryResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("affectedElementIds")]
    public List<long> AffectedElementIds { get; set; } = new();

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("updatedBoundingBox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoundingBoxMetersDto? UpdatedBoundingBox { get; set; }
}
