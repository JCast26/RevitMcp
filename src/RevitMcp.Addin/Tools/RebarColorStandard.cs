using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Define y gestiona la paleta cromática estándar para tipos de acero de refuerzo
/// según diámetros nominales estandarizados (ASTM / ACI / Métrico / ISO).
/// </summary>
public static class RebarColorStandard
{
    private record StandardRebarColor(double DiameterMm, string Description, Color Color);

    // Paleta estándar por diámetro nominal (mm)
    private static readonly List<StandardRebarColor> Standards = new()
    {
        new(6.0,   "6 mm / 1/4\" (#2)",       new Color(0, 220, 255)),    // Celeste / Cyan
        new(8.0,   "8 mm / 5/16\"",           new Color(0, 210, 180)),    // Turquesa / Teal
        new(9.525, "9.5 mm / 3/8\" / 10M (#3)", new Color(30, 200, 60)),  // Verde Brillante (Estribos)
        new(12.7,  "12 mm / 1/2\" / 13M (#4)", new Color(255, 205, 0)),  // Amarillo Oro
        new(15.875,"16 mm / 5/8\" / 16M (#5)", new Color(255, 100, 0)),  // Naranja Intenso
        new(19.05, "19 mm / 3/4\" / 19M (#6)", new Color(30, 120, 255)),  // Azul Rey
        new(22.225,"22 mm / 7/8\" / 22M (#7)", new Color(220, 30, 160)),  // Magenta / Fucsia
        new(25.4,  "25 mm / 1\" / 25M (#8)",   new Color(220, 20, 30)),   // Rojo Carmesí
        new(28.65, "28 mm / 1-1/8\" / 30M (#9)", new Color(140, 40, 220)),// Púrpura / Violeta
        new(31.75, "32 mm / 1-1/4\" / 32M (#10)", new Color(15, 60, 145)), // Azul Marino Profundo
        new(35.8,  "36 mm / 1-3/8\" / 35M (#11)", new Color(20, 125, 75)), // Verde Bosque
        new(43.0,  "40-43 mm / 1-3/4\" (#14)",  new Color(145, 65, 30)),  // Marrón / Terracota
        new(57.0,  "57 mm / 2-1/4\" (#18)",   new Color(75, 75, 80))     // Gris Carbón
    };

    // Paleta de respaldo contrastante para tipos personalizados o desconocidos
    private static readonly Color[] FallbackPalette =
    {
        new(0, 150, 136),   // Deep Teal
        new(233, 30, 99),   // Hot Pink
        new(63, 81, 181),   // Indigo
        new(139, 195, 74),  // Lima
        new(121, 85, 72),   // Café Claro
        new(255, 152, 0),   // Naranja Claro
        new(0, 188, 212),   // Cyan Oscuro
        new(156, 39, 176),  // Púrpura Eléctrico
        new(103, 58, 183),  // Deep Violet
        new(255, 87, 34)    // Sunset Red
    };

    /// <summary>
    /// Obtiene el color estandarizado correspondiente a un tipo de barra de acero.
    /// Prioriza la dimensión física nominal de Revit (BarModelDiameter) y recurre
    /// a análisis textual del nombre si el diámetro no está calibrado.
    /// </summary>
    public static Color GetColorForRebarType(RebarBarType barType)
    {
        if (barType == null)
            return new Color(0, 162, 255);

        // 1. Detección por diámetro físico exacto de Revit
        double diamMm = barType.BarModelDiameter * 304.8;
        if (diamMm > 3.0)
        {
            foreach (var std in Standards)
            {
                if (Math.Abs(diamMm - std.DiameterMm) <= 1.4)
                {
                    return std.Color;
                }
            }
        }

        // 2. Detección heurística por texto en el nombre del tipo
        string name = barType.Name.Trim().ToLowerInvariant();

        if (name.Contains("3/8") || name.Contains("#3") || name.Contains("10m") || name.Contains("10mm") || name.Contains("ø10") || name.Contains("d10"))
            return Standards[2].Color;
        if (name.Contains("1/2") || name.Contains("#4") || name.Contains("12mm") || name.Contains("13m") || name.Contains("ø12") || name.Contains("d12"))
            return Standards[3].Color;
        if (name.Contains("5/8") || name.Contains("#5") || name.Contains("16mm") || name.Contains("16m") || name.Contains("ø16") || name.Contains("d16"))
            return Standards[4].Color;
        if (name.Contains("3/4") || name.Contains("#6") || name.Contains("19mm") || name.Contains("20mm") || name.Contains("19m") || name.Contains("20m") || name.Contains("ø20") || name.Contains("d20"))
            return Standards[5].Color;
        if (name.Contains("7/8") || name.Contains("#7") || name.Contains("22mm") || name.Contains("22m") || name.Contains("ø22") || name.Contains("d22"))
            return Standards[6].Color;
        if (name.Contains("1\"") || name.Contains("1 pulg") || name.Contains("#8") || name.Contains("25mm") || name.Contains("25m") || name.Contains("ø25") || name.Contains("d25"))
            return Standards[7].Color;
        if (name.Contains("1 1/8") || name.Contains("1-1/8") || name.Contains("#9") || name.Contains("28mm") || name.Contains("30m"))
            return Standards[8].Color;
        if (name.Contains("1 1/4") || name.Contains("1-1/4") || name.Contains("#10") || name.Contains("32mm") || name.Contains("32m"))
            return Standards[9].Color;
        if (name.Contains("1 3/8") || name.Contains("1-3/8") || name.Contains("#11") || name.Contains("36mm") || name.Contains("35m"))
            return Standards[10].Color;
        if (name.Contains("1/4") || name.Contains("#2") || name.Contains("6mm") || name.Contains("ø6") || name.Contains("d6"))
            return Standards[0].Color;
        if (name.Contains("5/16") || name.Contains("8mm") || name.Contains("ø8") || name.Contains("d8"))
            return Standards[1].Color;

        // 3. Respaldo determinista según hash del nombre del tipo
        int index = Math.Abs(barType.Name.GetHashCode()) % FallbackPalette.Length;
        return FallbackPalette[index];
    }
}
