namespace RevitMcp.Core.Models;

/// <summary>
/// Solicitud para generar estribos de refuerzo zonificados para vigas de concreto armado.
/// Sigue el estándar de tres zonas: confinamiento inicial, tramo central (resto) y confinamiento final.
/// Todas las dimensiones y espaciamientos se expresan en METROS.
/// </summary>
public class CreateBeamStirrupsRequest
{
    /// <summary>
    /// Identificadores (ElementId) de las vigas anfitrionas (Structural Framing).
    /// </summary>
    public List<long> BeamElementIds { get; set; } = new();

    /// <summary>
    /// Nombre del tipo de barra de armadura a emplear para los estribos (ej. "3/8\"", "10mm", "#3").
    /// </summary>
    public string RebarBarTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Nombre de la forma de armadura (RebarShape) del estribo (ej. "T1", "51", "Stirrup/Tie").
    /// </summary>
    public string RebarShapeName { get; set; } = string.Empty;

    /// <summary>
    /// Distancia desde la cara interior del apoyo/columna al primer estribo en metros (típicamente 0.05m = 5cm).
    /// </summary>
    public double FirstStirrupOffsetMeters { get; set; } = 0.05;

    // --- ZONA 1 (Confinamiento Inicial) ---
    /// <summary>
    /// Cantidad de estribos en la primera zona confinada (ej. 6).
    /// Regla: Number with Spacing.
    /// </summary>
    public int Zone1Count { get; set; } = 6;

    /// <summary>
    /// Espaciamiento entre estribos en la zona 1 en metros (ej. 0.10m = 10cm).
    /// </summary>
    public double Zone1SpacingMeters { get; set; } = 0.10;

    // --- ZONA 2 (Tramo Central / "Resto") ---
    /// <summary>
    /// Espaciamiento máximo en el tramo central en metros (ej. 0.20m = 20cm).
    /// Regla: Maximum Spacing.
    /// </summary>
    public double Zone2MaxSpacingMeters { get; set; } = 0.20;

    // --- ZONA 3 (Confinamiento Final) ---
    /// <summary>
    /// Indica si la zona 3 es simétrica a la zona 1.
    /// Si es true, se emplean Zone1Count y Zone1SpacingMeters.
    /// </summary>
    public bool IsSymmetric { get; set; } = true;

    /// <summary>
    /// Cantidad de estribos en la zona confinada final (si no es simétrica).
    /// </summary>
    public int Zone3Count { get; set; } = 6;

    /// <summary>
    /// Espaciamiento en la zona confinada final en metros (si no es simétrica).
    /// </summary>
    public double Zone3SpacingMeters { get; set; } = 0.10;

    // --- PREPARACIÓN PARA REFUERZO LONGITUDINAL ---
    /// <summary>
    /// Habilita la inclusión del refuerzo longitudinal base (superior e inferior).
    /// </summary>
    public bool IncludeLongitudinalBars { get; set; } = false;

    /// <summary>
    /// Nombre del tipo de barra longitudinal (ej. "5/8\"", "1/2\"", "#5").
    /// </summary>
    public string? LongitudinalBarTypeName { get; set; }
}

/// <summary>
/// Resumen individual de los estribos generados para una viga específica.
/// </summary>
public class BeamStirrupsCreatedSummary
{
    public long BeamElementId { get; set; }
    public string BeamName { get; set; } = string.Empty;
    public double ClearSpanMeters { get; set; }
    public int Zone1StirrupsCount { get; set; }
    public int Zone2StirrupsCount { get; set; }
    public int Zone3StirrupsCount { get; set; }
    public int TotalStirrupsCount => Zone1StirrupsCount + Zone2StirrupsCount + Zone3StirrupsCount;
    public List<long> RebarElementIds { get; set; } = new();
    public string Status { get; set; } = "Success";
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Respuesta del procesamiento de estribos para vigas.
/// </summary>
public class CreateBeamStirrupsResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int TotalBeamsProcessed { get; set; }
    public List<BeamStirrupsCreatedSummary> Results { get; set; } = new();
}
