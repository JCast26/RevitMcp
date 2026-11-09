using System;
using System.Collections.Generic;

namespace RevitMcp.Core.Models;

/// <summary>
/// Tipo de estribo transversal para sardineles y muretes bajos.
/// </summary>
public enum CurbStirrupType
{
    /// <summary>
    /// Estribo en "C": barra recta con ganchos a 180° en ambos extremos (muros delgados).
    /// </summary>
    OpenC = 0,

    /// <summary>
    /// Estribo cerrado rectangular con ganchos sísmicos a 135° (muros con ancho suficiente e &gt; 15 cm).
    /// </summary>
    ClosedRectangular = 1
}

/// <summary>
/// Solicitud para generar armaduras en sardineles y muretes bajos de concreto armado.
/// </summary>
public class CreateCurbRebarRequest
{
    /// <summary>
    /// Identificadores de los muros/sardineles anfitriones seleccionados.
    /// </summary>
    public List<long> WallElementIds { get; set; } = new();

    /// <summary>
    /// Tipo de estribo transversal (0: OpenC a 180°, 1: ClosedRectangular a 135°).
    /// </summary>
    public CurbStirrupType StirrupType { get; set; } = CurbStirrupType.OpenC;

    /// <summary>
    /// Nombre del tipo de barra para el estribo transversal (ej. "1/4\"", "3/8\"", "#3").
    /// </summary>
    public string StirrupBarTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Espaciamiento entre estribos en metros (ej. 0.20 m = 20 cm).
    /// </summary>
    public double StirrupSpacingMeters { get; set; } = 0.20;

    /// <summary>
    /// Nombre del gancho para estribo (opcional).
    /// </summary>
    public string StirrupHookTypeName { get; set; } = string.Empty;

    // --- REFUERZO LONGITUDINAL ---
    /// <summary>
    /// Cantidad total de barras longitudinales.
    /// Estribo en C: 2 (superior e inferior) o 3 (superior, media e inferior), ubicadas dentro de los ganchos.
    /// Estribo cerrado: 4 (esquinas) o 6 (esquinas + intermedias), armado tipo viga.
    /// </summary>
    public int LongitudinalBarCount { get; set; } = 2;

    /// <summary>
    /// Nombre del tipo de barra para el refuerzo longitudinal (ej. "3/8\"", "1/2\"", "#4").
    /// </summary>
    public string LongitudinalBarTypeName { get; set; } = string.Empty;

    // --- ENCUENTROS Y ESQUINAS ---
    /// <summary>
    /// Si es verdadero, coloca un gancho en el extremo de las barras longitudinales orientado hacia el sardinel adyacente.
    /// </summary>
    public bool ExtendAtCorners { get; set; } = true;

    /// <summary>
    /// Nombre del tipo de gancho (RebarHookType, estilo Estándar) aplicado en los extremos con encuentro en esquina.
    /// </summary>
    public string CornerHookTypeName { get; set; } = string.Empty;

    // --- RECUBRIMIENTO ---
    /// <summary>
    /// Si es verdadero, obtiene el recubrimiento asignado al muro en Revit.
    /// </summary>
    public bool UseWallCover { get; set; } = true;

    /// <summary>
    /// Recubrimiento manual en metros (si UseWallCover es falso, ej. 0.025 m = 2.5 cm).
    /// </summary>
    public double CoverMeters { get; set; } = 0.025;

    /// <summary>
    /// Retranqueo inicial para el primer estribo en metros (ej. 0.05 m = 5 cm).
    /// </summary>
    public double StartOffsetMeters { get; set; } = 0.05;

    /// <summary>
    /// Retranqueo final para el último estribo en metros (ej. 0.05 m = 5 cm).
    /// </summary>
    public double EndOffsetMeters { get; set; } = 0.05;
}

/// <summary>
/// Resumen del resultado del armado por sardinel.
/// </summary>
public class CurbRebarCreatedSummary
{
    public long WallElementId { get; set; }
    public string Status { get; set; } = "Success";
    public string ErrorMessage { get; set; } = string.Empty;
    public int StirrupsCount { get; set; }
    public int LongitudinalBarsCount { get; set; }
    public int TotalRebarElementsCount { get; set; }
    public List<long> RebarElementIds { get; set; } = new();
}

/// <summary>
/// Respuesta del modelado de sardineles.
/// </summary>
public class CreateCurbRebarResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int TotalCurbsProcessed { get; set; }
    public List<CurbRebarCreatedSummary> Results { get; set; } = new();
}
