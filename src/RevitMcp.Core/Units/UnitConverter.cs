namespace RevitMcp.Core.Units;

/// <summary>
/// Helper de conversión de unidades estrictas entre el sistema de la API interna de Revit
/// (Decimal Feet / Pies Decimales) y el sistema internacional en Metros esperado por el LLM.
/// Factor exacto internacional: 1 pie (ft) = 0.3048 metros (m).
/// </summary>
public static class UnitConverter
{
    public const double MetersPerFoot = 0.3048;
    public const double FeetPerMeter = 1.0 / 0.3048; // ~3.2808398950131233

    /// <summary>
    /// Convierte metros a pies decimales (unidad interna de Revit para longitud).
    /// </summary>
    public static double MetersToFeet(double meters) => meters * FeetPerMeter;

    /// <summary>
    /// Convierte pies decimales a metros.
    /// </summary>
    public static double FeetToMeters(double feet) => feet * MetersPerFoot;

    /// <summary>
    /// Convierte metros cuadrados a pies cuadrados.
    /// </summary>
    public static double SquareMetersToSquareFeet(double sqMeters) => sqMeters * (FeetPerMeter * FeetPerMeter);

    /// <summary>
    /// Convierte pies cuadrados a metros cuadrados.
    /// </summary>
    public static double SquareFeetToSquareMeters(double sqFeet) => sqFeet * (MetersPerFoot * MetersPerFoot);

    /// <summary>
    /// Convierte metros cúbicos a pies cúbicos.
    /// </summary>
    public static double CubicMetersToCubicFeet(double cuMeters) => cuMeters * (FeetPerMeter * FeetPerMeter * FeetPerMeter);

    /// <summary>
    /// Convierte pies cúbicos a metros cúbicos.
    /// </summary>
    public static double CubicFeetToCubicMeters(double cuFeet) => cuFeet * (MetersPerFoot * MetersPerFoot * MetersPerFoot);
}
