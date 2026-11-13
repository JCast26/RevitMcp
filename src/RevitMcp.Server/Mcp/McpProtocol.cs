using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitMcp.Server.Mcp;

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse Success(object? id, object? result) =>
        new() { Id = id, Result = result };

    public static JsonRpcResponse FromError(object? id, int code, string message) =>
        new() { Id = id, Error = new JsonRpcError { Code = code, Message = message } };
}

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public static class McpToolDefinitions
{
    public static readonly object[] Tools = new object[]
    {
        new
        {
            name = "AuditParameters",
            description = "Filtra elementos del modelo de Revit por categoría y extrae los parámetros solicitados. IMPORTANTE: Todas las dimensiones, longitudes y áreas se devuelven automáticamente convertidas a METROS (m, m²).",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["categoryName"] = new
                    {
                        type = "string",
                        description = "Nombre de la categoría en Revit (ej. 'Doors', 'Walls', 'Windows', 'OST_Doors', 'Puertas', 'Muros', 'Rooms', 'Floors')."
                    },
                    ["parameterNames"] = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Lista de nombres de los parámetros a auditar (ej. ['Height', 'Width', 'Comments', 'Mark', 'Cost', 'Area'])."
                    }
                },
                required = new[] { "categoryName", "parameterNames" }
            }
        },
        new
        {
            name = "CreateModelElement",
            description = "Crea elementos en el modelo activo de Revit (Muros lineales o ejemplares de familias puntuales). IMPORTANTE: Todas las coordenadas (x, y, z), longitudes y alturas deben enviarse estrictamente en METROS; se convierten de forma automática a pies decimales para Revit.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["elementType"] = new
                    {
                        type = "string",
                        description = "Tipo de elemento a crear: 'Wall' (muro) o 'FamilyInstance' / 'Column' (instancia de familia puntual).",
                        @enum = new[] { "Wall", "FamilyInstance", "Column", "Muro" }
                    },
                    ["levelName"] = new
                    {
                        type = "string",
                        description = "Nombre del Nivel de Revit donde se ubicará el elemento (ej. 'Level 1', 'Nivel 1')."
                    },
                    ["startPoint"] = new
                    {
                        type = "object",
                        description = "Punto inicial en METROS para muros lineales.",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number", description = "Coordenada X en metros" },
                            ["y"] = new { type = "number", description = "Coordenada Y en metros" },
                            ["z"] = new { type = "number", description = "Coordenada Z en metros" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["endPoint"] = new
                    {
                        type = "object",
                        description = "Punto final en METROS para muros lineales.",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number", description = "Coordenada X en metros" },
                            ["y"] = new { type = "number", description = "Coordenada Y en metros" },
                            ["z"] = new { type = "number", description = "Coordenada Z en metros" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["location"] = new
                    {
                        type = "object",
                        description = "Punto de inserción en METROS para instancias de familias puntuales.",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number", description = "Coordenada X en metros" },
                            ["y"] = new { type = "number", description = "Coordenada Y en metros" },
                            ["z"] = new { type = "number", description = "Coordenada Z en metros" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["height"] = new
                    {
                        type = "number",
                        description = "Altura no conectada del muro en METROS (opcional)."
                    },
                    ["familyName"] = new
                    {
                        type = "string",
                        description = "Nombre de la familia a instanciar (para FamilyInstance)."
                    },
                    ["typeName"] = new
                    {
                        type = "string",
                        description = "Nombre del tipo o símbolo de la familia a instanciar (para FamilyInstance)."
                    }
                },
                required = new[] { "elementType", "levelName" }
            }
        },
        new
        {
            name = "ManageFamilyCreation",
            description = "Crea una nueva familia a partir de una plantilla .rft, le inyecta parámetros compartidos (Shared Parameters) vía FamilyManager, guarda un .rfa temporal y la carga directamente en el proyecto activo de Revit.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["familyName"] = new
                    {
                        type = "string",
                        description = "Nombre de la familia resultante (ej. 'M_ComponenteEspecial')."
                    },
                    ["templatePath"] = new
                    {
                        type = "string",
                        description = "Ruta absoluta o nombre del archivo de plantilla de familia .rft (ej. 'Metric Generic Model.rft')."
                    },
                    ["sharedParameters"] = new
                    {
                        type = "array",
                        description = "Lista de parámetros compartidos a inyectar en la familia.",
                        items = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["name"] = new { type = "string", description = "Nombre del parámetro compartido" },
                                ["group"] = new { type = "string", description = "Grupo de parámetros (ej. 'Data', 'Identity Data')" },
                                ["isInstance"] = new { type = "boolean", description = "True para parámetro de ejemplar (instancia), False para tipo." }
                            },
                            required = new[] { "name" }
                        }
                    }
                },
                required = new[] { "familyName", "templatePath" }
            }
        },
        new
        {
            name = "InspectFamily",
            description = "Inspecciona el documento de familia (.rfa) abierto actualmente en el Editor de Familias de Revit. Devuelve tipos, parámetros del FamilyManager, formas 3D (Extrusion, Blend, Revolution, Sweep), sus perfiles y bounding boxes en METROS, planos de referencia, cotas, familias anidadas y los ElementId seleccionados en pantalla.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["elementIds"] = new
                    {
                        type = "array",
                        items = new { type = "integer" },
                        description = "Lista opcional de ElementId para filtrar la inspección."
                    },
                    ["selectedOnly"] = new
                    {
                        type = "boolean",
                        description = "Si es true, inspecciona únicamente los elementos seleccionados actualmente en Revit."
                    },
                    ["includeParameters"] = new
                    {
                        type = "boolean",
                        description = "Incluir parámetros del FamilyManager (por defecto true)."
                    },
                    ["includeProfileCurves"] = new
                    {
                        type = "boolean",
                        description = "Incluir los bucles de curvas del croquis/perfil de cada forma (por defecto true)."
                    },
                    ["includeReferencePlanes"] = new
                    {
                        type = "boolean",
                        description = "Incluir planos de referencia (por defecto true)."
                    },
                    ["includeDimensions"] = new
                    {
                        type = "boolean",
                        description = "Incluir cotas/dimensiones de la familia (por defecto true)."
                    }
                }
            }
        },
        new
        {
            name = "EditFamilyGeometry",
            description = "Modifica geométricamente o paramétricamente elementos dentro del documento de familia (.rfa) activo en el Editor de Familias de Revit. Soporta Move, Rotate, Mirror, SetExtrusionBounds, ScaleProfile, OffsetProfile, ReplaceExtrusionProfile, CreateExtrusion, SetFamilyParameter y DeleteElements. IMPORTANTE: Todas las coordenadas, dimensiones y desplazamientos deben enviarse estrictamente en METROS y los ángulos en GRADOS.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["operation"] = new
                    {
                        type = "string",
                        description = "Operación geométrica o paramétrica a ejecutar.",
                        @enum = new[]
                        {
                            "Move",
                            "Rotate",
                            "Mirror",
                            "SetExtrusionBounds",
                            "ScaleProfile",
                            "OffsetProfile",
                            "ReplaceExtrusionProfile",
                            "CreateExtrusion",
                            "SetFamilyParameter",
                            "DeleteElements"
                        }
                    },
                    ["elementId"] = new
                    {
                        type = "integer",
                        description = "ElementId del elemento objetivo dentro de la familia."
                    },
                    ["elementIds"] = new
                    {
                        type = "array",
                        items = new { type = "integer" },
                        description = "Lista de ElementId objetivo (alternativa a elementId para operar sobre varios elementos)."
                    },
                    ["useSelection"] = new
                    {
                        type = "boolean",
                        description = "Si es true y no se indica elementId/elementIds, usa los elementos seleccionados actualmente en Revit."
                    },
                    ["unpinIfNeeded"] = new
                    {
                        type = "boolean",
                        description = "Si es true, desbloquea (Unpin) automáticamente elementos fijados antes de modificarlos."
                    },
                    ["translationMeters"] = new
                    {
                        type = "object",
                        description = "Vector de desplazamiento (x, y, z) en METROS para 'Move'.",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number" },
                            ["y"] = new { type = "number" },
                            ["z"] = new { type = "number" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["axisOriginMeters"] = new
                    {
                        type = "object",
                        description = "Origen del eje de rotación en METROS para 'Rotate' (por defecto centro del bounding box).",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number" },
                            ["y"] = new { type = "number" },
                            ["z"] = new { type = "number" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["axisDirection"] = new
                    {
                        type = "object",
                        description = "Vector director del eje de rotación para 'Rotate' (por defecto Z: {x:0, y:0, z:1}).",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number" },
                            ["y"] = new { type = "number" },
                            ["z"] = new { type = "number" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["angleDegrees"] = new
                    {
                        type = "number",
                        description = "Ángulo de rotación en GRADOS para 'Rotate'."
                    },
                    ["planeOriginMeters"] = new
                    {
                        type = "object",
                        description = "Punto de origen del plano de simetría en METROS para 'Mirror'.",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number" },
                            ["y"] = new { type = "number" },
                            ["z"] = new { type = "number" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["planeNormal"] = new
                    {
                        type = "object",
                        description = "Vector normal del plano de simetría para 'Mirror'.",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number" },
                            ["y"] = new { type = "number" },
                            ["z"] = new { type = "number" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["copy"] = new
                    {
                        type = "boolean",
                        description = "En 'Mirror', true crea una copia reflejada; false refleja el elemento original (por defecto false)."
                    },
                    ["startOffsetMeters"] = new
                    {
                        type = "number",
                        description = "Inicio de extrusión (StartOffset / BottomOffset) en METROS para 'SetExtrusionBounds' o 'CreateExtrusion'."
                    },
                    ["endOffsetMeters"] = new
                    {
                        type = "number",
                        description = "Fin de extrusión (EndOffset / TopOffset) en METROS para 'SetExtrusionBounds' o 'CreateExtrusion'."
                    },
                    ["startAngleDegrees"] = new
                    {
                        type = "number",
                        description = "Ángulo inicial en GRADOS para 'Revolution' en 'SetExtrusionBounds'."
                    },
                    ["endAngleDegrees"] = new
                    {
                        type = "number",
                        description = "Ángulo final en GRADOS para 'Revolution' en 'SetExtrusionBounds'."
                    },
                    ["isSolid"] = new
                    {
                        type = "boolean",
                        description = "True para sólido, false para vacío (Void) en 'SetExtrusionBounds' o 'CreateExtrusion'."
                    },
                    ["scaleX"] = new
                    {
                        type = "number",
                        description = "Factor de escala en el eje X local del croquis para 'ScaleProfile'."
                    },
                    ["scaleY"] = new
                    {
                        type = "number",
                        description = "Factor de escala en el eje Y local del croquis para 'ScaleProfile'."
                    },
                    ["scaleFactor"] = new
                    {
                        type = "number",
                        description = "Factor de escala uniforme para 'ScaleProfile' si no se indican scaleX/scaleY."
                    },
                    ["pivotMeters"] = new
                    {
                        type = "object",
                        description = "Pivote de escalado en METROS para 'ScaleProfile' (por defecto centroide del perfil).",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number" },
                            ["y"] = new { type = "number" },
                            ["z"] = new { type = "number" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["offsetMeters"] = new
                    {
                        type = "number",
                        description = "Desfase en METROS para 'OffsetProfile' (positivo expande hacia afuera, negativo contrae hacia adentro)."
                    },
                    ["profileLoops"] = new
                    {
                        type = "array",
                        description = "Lista de bucles cerrados de puntos 3D en METROS para 'ReplaceExtrusionProfile' o 'CreateExtrusion'.",
                        items = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["x"] = new { type = "number" },
                                    ["y"] = new { type = "number" },
                                    ["z"] = new { type = "number" }
                                },
                                required = new[] { "x", "y", "z" }
                            }
                        }
                    },
                    ["rectangle"] = new
                    {
                        type = "object",
                        description = "Definición rectangular en METROS en coordenadas locales del plano de trabajo para 'ReplaceExtrusionProfile' o 'CreateExtrusion'.",
                        properties = new Dictionary<string, object>
                        {
                            ["centerMeters"] = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["x"] = new { type = "number" },
                                    ["y"] = new { type = "number" },
                                    ["z"] = new { type = "number" }
                                },
                                required = new[] { "x", "y", "z" }
                            },
                            ["widthMeters"] = new { type = "number" },
                            ["depthMeters"] = new { type = "number" },
                            ["minX"] = new { type = "number" },
                            ["minY"] = new { type = "number" },
                            ["maxX"] = new { type = "number" },
                            ["maxY"] = new { type = "number" }
                        }
                    },
                    ["circle"] = new
                    {
                        type = "object",
                        description = "Definición circular en METROS para 'ReplaceExtrusionProfile' o 'CreateExtrusion'.",
                        properties = new Dictionary<string, object>
                        {
                            ["centerMeters"] = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["x"] = new { type = "number" },
                                    ["y"] = new { type = "number" },
                                    ["z"] = new { type = "number" }
                                },
                                required = new[] { "x", "y", "z" }
                            },
                            ["radiusMeters"] = new { type = "number" }
                        },
                        required = new[] { "radiusMeters" }
                    },
                    ["sketchPlaneOriginMeters"] = new
                    {
                        type = "object",
                        description = "Origen del plano de trabajo en METROS para 'CreateExtrusion' (por defecto {0,0,0}).",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number" },
                            ["y"] = new { type = "number" },
                            ["z"] = new { type = "number" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["sketchPlaneNormal"] = new
                    {
                        type = "object",
                        description = "Normal del plano de trabajo para 'CreateExtrusion' (por defecto {0,0,1}).",
                        properties = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "number" },
                            ["y"] = new { type = "number" },
                            ["z"] = new { type = "number" }
                        },
                        required = new[] { "x", "y", "z" }
                    },
                    ["parameterName"] = new
                    {
                        type = "string",
                        description = "Nombre del parámetro a modificar en 'SetFamilyParameter'."
                    },
                    ["familyTypeName"] = new
                    {
                        type = "string",
                        description = "Nombre opcional del tipo de familia a activar antes de modificar el parámetro."
                    },
                    ["numericValue"] = new
                    {
                        type = "number",
                        description = "Valor numérico (en METROS para longitud, m² área, m³ volumen, GRADOS ángulo) para 'SetFamilyParameter'."
                    },
                    ["stringValue"] = new
                    {
                        type = "string",
                        description = "Valor de texto para 'SetFamilyParameter'."
                    },
                    ["integerValue"] = new
                    {
                        type = "integer",
                        description = "Valor entero para 'SetFamilyParameter'."
                    },
                    ["booleanValue"] = new
                    {
                        type = "boolean",
                        description = "Valor booleano (Yes/No) para 'SetFamilyParameter'."
                    },
                    ["formula"] = new
                    {
                        type = "string",
                        description = "Fórmula para asignar o limpiar (cadena vacía) en un FamilyParameter."
                    }
                },
                required = new[] { "operation" }
            }
        },
        new
        {
            name = "CreateBeamStirrups",
            description = "Crea conjuntos de armadura (estribos) para vigas de concreto armado en Autodesk Revit siguiendo la distribución sismorresistente en 3 conjuntos (Confinamiento Inicial, Tramo Central y Confinamiento Final). Todos los espaciamientos y dimensiones deben enviarse en METROS.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["beamElementIds"] = new
                    {
                        type = "array",
                        items = new { type = "integer" },
                        description = "Lista de identificadores (ElementId) de las vigas anfitrionas (Structural Framing)."
                    },
                    ["rebarBarTypeName"] = new
                    {
                        type = "string",
                        description = "Nombre del tipo de barra de refuerzo para el estribo (ej. '3/8\"', '10mm', '#3'). Si se omite, se utiliza el primer tipo disponible."
                    },
                    ["rebarShapeName"] = new
                    {
                        type = "string",
                        description = "Nombre de la forma de estribo (ej. 'T1', '51', 'Stirrup/Tie'). Si se omite, se busca la primera forma cerrada con gancho sísmico."
                    },
                    ["firstStirrupOffsetMeters"] = new
                    {
                        type = "number",
                        description = "Distancia desde la cara del apoyo/columna al primer estribo en METROS (por defecto 0.05m = 5cm)."
                    },
                    ["zone1Count"] = new
                    {
                        type = "integer",
                        description = "Cantidad de estribos en la primera zona de confinamiento (por defecto 6)."
                    },
                    ["zone1SpacingMeters"] = new
                    {
                        type = "number",
                        description = "Espaciamiento entre estribos en la zona 1 en METROS (por defecto 0.10m = 10cm)."
                    },
                    ["zone2MaxSpacingMeters"] = new
                    {
                        type = "number",
                        description = "Espaciamiento máximo en el tramo central (resto) en METROS (por defecto 0.20m = 20cm)."
                    },
                    ["isSymmetric"] = new
                    {
                        type = "boolean",
                        description = "Indica si el extremo final (Zona 3) es simétrico al inicial (por defecto true)."
                    },
                    ["zone3Count"] = new
                    {
                        type = "integer",
                        description = "Cantidad de estribos en la zona 3 si no es simétrica."
                    },
                    ["zone3SpacingMeters"] = new
                    {
                        type = "number",
                        description = "Espaciamiento en la zona 3 en METROS si no es simétrica."
                    },
                    ["includeLongitudinalBars"] = new
                    {
                        type = "boolean",
                        description = "Habilita la base para refuerzo longitudinal complementario (por defecto false)."
                    }
                },
                required = new[] { "beamElementIds" }
            }
        },
        new
        {
            name = "CreateWallRebar",
            description = "Modela armaduras de refuerzo en muros estructurales de concreto armado en Autodesk Revit. Soporta armado con 1 malla (central) o 2 mallas (doble cortina: exterior e interior) y diversas tipologías estructurales: 'StandardMesh' (malla continua recta), 'EndHooks' (malla con ganchos sísmicos de remate en extremos) y 'BoundaryElements' (alma con malla y cabezales confinados con estribos en extremos). Todas las dimensiones y espaciamientos se envían en METROS.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["wallElementIds"] = new
                    {
                        type = "array",
                        items = new { type = "integer" },
                        description = "Lista de identificadores (ElementId) de los muros anfitriones (Walls / OST_Walls)."
                    },
                    ["numberOfMeshes"] = new
                    {
                        type = "integer",
                        description = "Número de mallas o cortinas de refuerzo: 1 (malla central) o 2 (doble malla: exterior e interior). Por defecto 2.",
                        @enum = new[] { 1, 2 }
                    },
                    ["typology"] = new
                    {
                        type = "string",
                        description = "Tipología estructural: 'StandardMesh' (0: continua), 'EndHooks' (1: remate con ganchos sísmicos en extremos), 'BoundaryElements' (2: cabezales confinados de borde con estribos). Por defecto 'StandardMesh'.",
                        @enum = new[] { "StandardMesh", "EndHooks", "BoundaryElements" }
                    },
                    ["verticalBarTypeName"] = new
                    {
                        type = "string",
                        description = "Nombre del tipo de barra para el refuerzo vertical (ej. '1/2\"', '3/8\"', '#4', '12mm')."
                    },
                    ["verticalSpacingMeters"] = new
                    {
                        type = "number",
                        description = "Espaciamiento entre barras verticales en METROS (por defecto 0.20m = 20cm)."
                    },
                    ["horizontalBarTypeName"] = new
                    {
                        type = "string",
                        description = "Nombre del tipo de barra para el refuerzo horizontal (ej. '3/8\"', '1/2\"', '#3', '10mm')."
                    },
                    ["horizontalSpacingMeters"] = new
                    {
                        type = "number",
                        description = "Espaciamiento entre barras horizontales en METROS (por defecto 0.20m = 20cm)."
                    },
                    ["horizontalHookTypeName"] = new
                    {
                        type = "string",
                        description = "Nombre del tipo de gancho para barras horizontales (ej. 'Standard - 90 deg', 'Gancho 90°')."
                    },
                    ["horizontalBarsOutside"] = new
                    {
                        type = "boolean",
                        description = "Indica si las barras horizontales van en la cara exterior respecto a las verticales (por defecto true)."
                    },
                    ["useWallCover"] = new
                    {
                        type = "boolean",
                        description = "Indica si se utiliza automáticamente el recubrimiento asignado al muro en Revit (por defecto true)."
                    },
                    ["coverMeters"] = new
                    {
                        type = "number",
                        description = "Recubrimiento libre de concreto en las caras del muro en METROS si useWallCover es false (por defecto 0.025m = 2.5cm)."
                    },
                    ["includeCrossties"] = new
                    {
                        type = "boolean",
                        description = "Indica si se generan grapas transversales (crossties) vinculando ambas mallas en configuración de 2 mallas (por defecto false)."
                    },
                    ["crosstieSpacingMeters"] = new
                    {
                        type = "number",
                        description = "Espaciamiento de las grapas transversales en METROS (por defecto 0.40m = 40cm)."
                    },
                    ["boundaryLengthMeters"] = new
                    {
                        type = "number",
                        description = "Longitud de confinamiento en cada extremo para BoundaryElements en METROS (por defecto 0.40m = 40cm)."
                    },
                    ["boundaryTieSpacingMeters"] = new
                    {
                        type = "number",
                        description = "Espaciamiento de los estribos de confinamiento en cabezales en METROS (por defecto 0.10m = 10cm)."
                    }
                },
                required = new[] { "wallElementIds" }
            }
        },
        new
        {
            name = "ToggleRebarFilter",
            description = "Alterna (On/Off) filtros de color por tipo para la categoría Acero de Refuerzo (OST_Rebar) en la vista activa de Revit (corte y proyección). Al activarse, asigna colores estandarizados según diámetro; al desactivarse, remueve los filtros.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>()
            }
        }
    };
}
