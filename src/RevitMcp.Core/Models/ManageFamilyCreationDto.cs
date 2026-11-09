using System.Text.Json.Serialization;

namespace RevitMcp.Core.Models;

/// <summary>
/// Definición de un parámetro compartido a inyectar en una familia.
/// </summary>
public class SharedParameterDefinitionDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("group")]
    public string Group { get; set; } = "Data";

    [JsonPropertyName("isInstance")]
    public bool IsInstance { get; set; } = true;

    [JsonPropertyName("dataType")]
    public string? DataType { get; set; } = "Text";
}

/// <summary>
/// Solicitud de creación/modificación de familia y carga en el proyecto activo.
/// </summary>
public class ManageFamilyCreationRequest
{
    [JsonPropertyName("familyName")]
    public string FamilyName { get; set; } = string.Empty;

    [JsonPropertyName("templatePath")]
    public string TemplatePath { get; set; } = string.Empty;

    [JsonPropertyName("sharedParameters")]
    public List<SharedParameterDefinitionDto> SharedParameters { get; set; } = new();
}

/// <summary>
/// Resultado de la operación de gestión y carga de familia.
/// </summary>
public class ManageFamilyCreationResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("familyName")]
    public string FamilyName { get; set; } = string.Empty;

    [JsonPropertyName("familyId")]
    public long? FamilyId { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
