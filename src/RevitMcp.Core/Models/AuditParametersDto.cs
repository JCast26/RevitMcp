using System.Text.Json.Serialization;

namespace RevitMcp.Core.Models;

/// <summary>
/// Solicitud de auditoría de parámetros para una categoría de Revit.
/// </summary>
public class AuditParametersRequest
{
    [JsonPropertyName("categoryName")]
    public string CategoryName { get; set; } = string.Empty;

    [JsonPropertyName("parameterNames")]
    public List<string> ParameterNames { get; set; } = new();
}

/// <summary>
/// Representación de un elemento auditado y sus valores de parámetros.
/// </summary>
public class ElementAuditDto
{
    [JsonPropertyName("elementId")]
    public long ElementId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public Dictionary<string, object?> Parameters { get; set; } = new();
}

/// <summary>
/// Resultado estructurado devuelto al LLM tras auditar parámetros.
/// </summary>
public class AuditParametersResponse
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("elements")]
    public List<ElementAuditDto> Elements { get; set; } = new();
}
