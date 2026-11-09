using System.Text.Json.Serialization;

namespace RevitMcp.Core.Models;

/// <summary>
/// Coordenadas espaciales estrictamente en METROS.
/// </summary>
public class Point3D
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }

    public Point3D() { }

    public Point3D(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})m";
}

/// <summary>
/// Parámetros para la creación de un elemento en Revit.
/// Todas las distancias, alturas y coordenadas están expresadas estrictamente en METROS.
/// </summary>
public class CreateModelElementRequest
{
    /// <summary>
    /// Tipo de elemento a crear: "Wall", "FamilyInstance", "Column", "Desk", etc.
    /// </summary>
    [JsonPropertyName("elementType")]
    public string ElementType { get; set; } = "Wall";

    /// <summary>
    /// Nombre del nivel al que se asociará el elemento (ej. "Level 1", "Nivel 1").
    /// </summary>
    [JsonPropertyName("levelName")]
    public string LevelName { get; set; } = string.Empty;

    /// <summary>
    /// Punto inicial para elementos lineales (Muro) en METROS.
    /// </summary>
    [JsonPropertyName("startPoint")]
    public Point3D? StartPoint { get; set; }

    /// <summary>
    /// Punto final para elementos lineales (Muro) en METROS.
    /// </summary>
    [JsonPropertyName("endPoint")]
    public Point3D? EndPoint { get; set; }

    /// <summary>
    /// Punto de inserción para elementos puntuales (FamilyInstance) en METROS.
    /// </summary>
    [JsonPropertyName("location")]
    public Point3D? Location { get; set; }

    /// <summary>
    /// Altura no conectada (Unconnected Height) en METROS (opcional para muros).
    /// </summary>
    [JsonPropertyName("height")]
    public double? Height { get; set; }

    /// <summary>
    /// Nombre de la familia a instanciar (para FamilyInstance).
    /// </summary>
    [JsonPropertyName("familyName")]
    public string? FamilyName { get; set; }

    /// <summary>
    /// Nombre del tipo de familia a instanciar (para FamilyInstance).
    /// </summary>
    [JsonPropertyName("typeName")]
    public string? TypeName { get; set; }
}

/// <summary>
/// Resultado de la creación de un elemento en Revit.
/// </summary>
public class CreateModelElementResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("elementId")]
    public long ElementId { get; set; }

    [JsonPropertyName("elementType")]
    public string ElementType { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
