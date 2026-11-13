using System.Collections.Generic;

namespace RevitMcp.Core.Models;

/// <summary>
/// Solicitud para generar armadura típica en capiteles (Structural Columns) de concreto armado:
/// barras verticales perimetrales en las 4 caras con ganchos a 90° en ambos extremos y
/// estribos cerrados horizontales de confinamiento con ganchos sísmicos a 135°.
/// Todas las dimensiones y espaciamientos se expresan en METROS.
/// </summary>
public class CreateCapitalRebarRequest
{
    /// <summary>
    /// Identificadores (ElementId) de los capiteles anfitriones (OST_StructuralColumns).
    /// </summary>
    public List<long> ColumnElementIds { get; set; } = new();

    // --- BARRAS VERTICALES EN CARAS (GANCHOS 90°) ---
    /// <summary>
    /// Nombre del tipo de barra para el refuerzo vertical en las caras (ej. "1/2\"", "5/8\"", "#4").
    /// </summary>
    public string VerticalBarTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Espaciamiento máximo entre barras verticales a lo largo de cada cara en metros (ej. 0.20 m = 20 cm).
    /// </summary>
    public double VerticalSpacingMeters { get; set; } = 0.20;

    /// <summary>
    /// Nombre del tipo de gancho estándar a 90° aplicado en ambos extremos de las barras verticales.
    /// </summary>
    public string VerticalHookTypeName { get; set; } = string.Empty;

    // --- ESTRIBOS DE CONFINAMIENTO (CERRADOS 135°) ---
    /// <summary>
    /// Nombre del tipo de barra para los estribos cerrados (ej. "3/8\"", "1/4\"", "#3").
    /// </summary>
    public string StirrupBarTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Nombre de la forma de estribo cerrado (RebarShape, ej. "T1", "51").
    /// </summary>
    public string StirrupShapeName { get; set; } = string.Empty;

    /// <summary>
    /// Nombre del tipo de gancho sísmico a 135° para el estribo cerrado.
    /// </summary>
    public string StirrupHookTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Espaciamiento máximo vertical entre estribos de confinamiento en metros (ej. 0.15 m = 15 cm).
    /// </summary>
    public double StirrupSpacingMeters { get; set; } = 0.15;

    // --- RECUBRIMIENTO ---
    /// <summary>
    /// Si es verdadero, obtiene el recubrimiento asignado al capitel (Structural Column) en Revit.
    /// </summary>
    public bool UseElementCover { get; set; } = true;

    /// <summary>
    /// Recubrimiento manual de concreto en metros cuando UseElementCover es falso (ej. 0.04 m = 4.0 cm).
    /// </summary>
    public double CoverMeters { get; set; } = 0.04;
}

/// <summary>
/// Resumen individual del armado generado para un capitel específico.
/// </summary>
public class CapitalRebarCreatedSummary
{
    public long ColumnElementId { get; set; }
    public string ColumnName { get; set; } = string.Empty;
    public double WidthMeters { get; set; }
    public double DepthMeters { get; set; }
    public double HeightMeters { get; set; }
    public int VerticalBarsCount { get; set; }
    public int StirrupsCount { get; set; }
    public int TotalRebarElementsCount { get; set; }
    public List<long> RebarElementIds { get; set; } = new();
    public string Status { get; set; } = "Success";
    public string ErrorMessage { get; set; } = string.Empty;
}

/// <summary>
/// Respuesta consolidada del modelado de armaduras en capiteles.
/// </summary>
public class CreateCapitalRebarResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int TotalCapitalsProcessed { get; set; }
    public List<CapitalRebarCreatedSummary> Results { get; set; } = new();
}
