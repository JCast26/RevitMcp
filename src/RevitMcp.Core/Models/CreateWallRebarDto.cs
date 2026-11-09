namespace RevitMcp.Core.Models;

/// <summary>
/// Tipologías estructurales de armado para muros de concreto armado.
/// </summary>
public enum WallRebarTypology
{
    /// <summary>
    /// Malla continua rectangular estándar (1 o 2 capas de barras ortogonales rectas).
    /// </summary>
    StandardMesh = 0,

    /// <summary>
    /// Malla con remate de ganchos en los extremos (barras horizontales con gancho a 90° o 180° hacia el interior).
    /// </summary>
    EndHooks = 1,

    /// <summary>
    /// Malla en el alma central con elementos de borde confinados (estribos cerrados de cabezal sísmico en extremos).
    /// </summary>
    BoundaryElements = 2,

    /// <summary>
    /// Muro inclinado de confinamiento de escalera (perfil editado con barras verticales en Varying Rebar Set).
    /// </summary>
    InclinedStairWall = 3
}

/// <summary>
/// Solicitud para generar armaduras de refuerzo en muros estructurales de concreto armado.
/// Soporta configuración de una o dos mallas (doble cortina) y diversas tipologías estructurales.
/// Todas las dimensiones y espaciamientos se expresan en METROS.
/// </summary>
public class CreateWallRebarRequest
{
    /// <summary>
    /// Identificadores (ElementId) de los muros anfitriones (Walls / OST_Walls).
    /// </summary>
    public List<long> WallElementIds { get; set; } = new();

    /// <summary>
    /// Número de mallas o cortinas de armadura: 1 (malla central) o 2 (doble malla: exterior e interior).
    /// </summary>
    public int NumberOfMeshes { get; set; } = 2;

    /// <summary>
    /// Tipología estructural del armado (0: StandardMesh, 1: EndHooks, 2: BoundaryElements).
    /// </summary>
    public WallRebarTypology Typology { get; set; } = WallRebarTypology.StandardMesh;

    // --- REFUERZO VERTICAL ---
    /// <summary>
    /// Nombre del tipo de barra para el refuerzo vertical (ej. "3/8\"", "1/2\"", "#4").
    /// </summary>
    public string VerticalBarTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Espaciamiento entre barras verticales en metros (ej. 0.20m = 20cm).
    /// </summary>
    public double VerticalSpacingMeters { get; set; } = 0.20;

    /// <summary>
    /// Desfase/Recubrimiento superior para barras verticales en metros (ej. 0.025m = 2.5cm).
    /// </summary>
    public double VerticalTopOffsetMeters { get; set; } = 0.025;

    /// <summary>
    /// Desfase/Recubrimiento inferior para barras verticales en metros (ej. 0.025m = 2.5cm).
    /// </summary>
    public double VerticalBottomOffsetMeters { get; set; } = 0.025;

    /// <summary>
    /// Tipo de gancho para barras verticales (opcional, ej. "Standard - 90 deg", "Gancho 90°").
    /// </summary>
    public string? VerticalHookTypeName { get; set; }

    /// <summary>
    /// Ubicación del gancho en barras verticales: 0 (Superior / Coronación), 1 (Ambos Extremos), 2 (Inferior / Arranque).
    /// </summary>
    public int VerticalHookPosition { get; set; } = 0;

    // --- REFUERZO HORIZONTAL ---
    /// <summary>
    /// Nombre del tipo de barra para el refuerzo horizontal (ej. "3/8\"", "1/2\"", "#3").
    /// </summary>
    public string HorizontalBarTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Espaciamiento entre barras horizontales en metros (ej. 0.20m = 20cm).
    /// </summary>
    public double HorizontalSpacingMeters { get; set; } = 0.20;

    /// <summary>
    /// Desfase lateral inicial para barras horizontales en metros (ej. 0.025m = 2.5cm).
    /// </summary>
    public double HorizontalStartOffsetMeters { get; set; } = 0.025;

    /// <summary>
    /// Desfase lateral final para barras horizontales en metros (ej. 0.025m = 2.5cm).
    /// </summary>
    public double HorizontalEndOffsetMeters { get; set; } = 0.025;

    /// <summary>
    /// Tipo de gancho para barras horizontales (opcional, ej. "Standard - 90 deg", "Gancho 90°").
    /// </summary>
    public string? HorizontalHookTypeName { get; set; }

    // --- DISPOSICIÓN Y RECUBRIMIENTO ---
    /// <summary>
    /// Indica si las barras horizontales van por la cara exterior respecto a las verticales.
    /// Si es true: Horizontales externas, verticales internas.
    /// Si es false: Verticales externas, horizontales internas.
    /// </summary>
    public bool HorizontalBarsOutside { get; set; } = true;

    /// <summary>
    /// Si es true, obtiene y utiliza automáticamente el recubrimiento asignado al muro en Revit.
    /// Si es false, utiliza el valor manual especificado en CoverMeters.
    /// </summary>
    public bool UseWallCover { get; set; } = true;

    /// <summary>
    /// Recubrimiento libre de concreto en las caras del muro en metros (ej. 0.025m = 2.5cm).
    /// </summary>
    public double CoverMeters { get; set; } = 0.025;

    // --- CONECTORES TRANSVERSALES (GRAPAS / CROSSTIES) ---
    /// <summary>
    /// Indica si se generan grapas transversales (crossties) vinculando ambas mallas (aplica en 2 mallas).
    /// </summary>
    public bool IncludeCrossties { get; set; } = false;

    /// <summary>
    /// Nombre del tipo de barra para las grapas transversales.
    /// </summary>
    public string? CrosstieBarTypeName { get; set; }

    /// <summary>
    /// Espaciamiento de las grapas transversales en metros (ej. 0.40m = 40cm).
    /// </summary>
    public double CrosstieSpacingMeters { get; set; } = 0.40;

    /// <summary>
    /// Patrón de distribución de grapas en planta a lo largo del muro:
    /// 0 = En cada barra vertical (consecutiva)
    /// 1 = Dejando 1 barra vertical (alternada, cada 2 barras) [Predeterminado]
    /// 2 = Dejando 2 barras verticales (cada 3 barras)
    /// </summary>
    public int CrosstiePlanPattern { get; set; } = 1;

    /// <summary>
    /// Configuración de ganchos en los extremos de las grapas transversales:
    /// 0 = Ambos extremos a 135° (135° - 135°) [Predeterminado]
    /// 1 = Combinado (135° y 90°)
    /// 2 = Ambos extremos a 90° (90° - 90°)
    /// 3 = Ambos extremos a 180° (180° - 180°)
    /// </summary>
    public int CrosstieHookMode { get; set; } = 0;

    // --- ELEMENTOS DE BORDE / CONFINAMIENTO (BOUNDARY ELEMENTS) ---
    /// <summary>
    /// Indica si se generan elementos de confinamiento de borde (Boundary Elements / Cabezales) en los extremos.
    /// </summary>
    public bool IncludeBoundaryElements { get; set; } = false;

    /// <summary>
    /// Longitud de la zona de confinamiento de borde en cada extremo del muro en metros (ej. 0.40m = 40cm).
    /// </summary>
    public double BoundaryLengthMeters { get; set; } = 0.40;

    /// <summary>
    /// Espaciamiento de los estribos de confinamiento en el cabezal de borde en metros (ej. 0.10m = 10cm).
    /// </summary>
    public double BoundaryTieSpacingMeters { get; set; } = 0.10;

    /// <summary>
    /// Nombre del tipo de barra para los estribos del elemento de borde.
    /// </summary>
    public string? BoundaryTieBarTypeName { get; set; }

    /// <summary>
    /// Nombre de la forma de estribo (RebarShape) para el elemento de borde (ej. "T1", "Stirrup").
    /// </summary>
    public string? BoundaryTieShapeName { get; set; }

    // --- ABERTURAS Y REFUERZO DE VANOS ---
    /// <summary>
    /// Nombre del tipo de gancho para las barras de la malla principal que se interrumpen en los bordes de una abertura (null = barra recta).
    /// </summary>
    public string? OpeningMeshHookTypeName { get; set; }

    /// <summary>
    /// Indica si se agrega refuerzo longitudinal perimetral alrededor de las aberturas.
    /// </summary>
    public bool IncludeOpeningPerimeterBars { get; set; } = false;

    /// <summary>
    /// Nombre del tipo de barra para el refuerzo longitudinal perimetral de aberturas.
    /// </summary>
    public string? OpeningPerimeterBarTypeName { get; set; }

    /// <summary>
    /// Cantidad de barras perimetrales por cara/malla en cada borde de la abertura (ej. 2).
    /// </summary>
    public int OpeningPerimeterBarsPerLayer { get; set; } = 2;

    /// <summary>
    /// Longitud de anclaje (Ld) que las barras perimetrales se prolongan más allá de la esquina de la abertura en metros (ej. 0.50m = 50cm).
    /// </summary>
    public double OpeningPerimeterAnchorageMeters { get; set; } = 0.50;

    /// <summary>
    /// Separación entre barras del paquete de refuerzo perimetral en metros (ej. 0.10m = 10cm).
    /// </summary>
    public double OpeningPerimeterSpacingMeters { get; set; } = 0.10;

    /// <summary>
    /// Indica si se agrega refuerzo diagonal a 45° en las esquinas de las aberturas.
    /// </summary>
    public bool IncludeOpeningDiagonalBars { get; set; } = false;

    /// <summary>
    /// Nombre del tipo de barra para el refuerzo diagonal a 45° en esquinas de aberturas.
    /// </summary>
    public string? OpeningDiagonalBarTypeName { get; set; }

    /// <summary>
    /// Cantidad de barras diagonales por esquina en cada malla (ej. 1 o 2).
    /// </summary>
    public int OpeningDiagonalBarsPerCorner { get; set; } = 1;

    /// <summary>
    /// Longitud total de cada barra diagonal en metros (ej. 0.90m = 90cm).
    /// </summary>
    public double OpeningDiagonalLengthMeters { get; set; } = 0.90;

    /// <summary>
    /// Indica si se agregan cercos / estribos cerrados confinando los bordes de las aberturas.
    /// </summary>
    public bool IncludeOpeningEdgeStirrups { get; set; } = false;

    /// <summary>
    /// Nombre del tipo de barra para los cercos / estribos de borde de aberturas.
    /// </summary>
    public string? OpeningStirrupBarTypeName { get; set; }

    /// <summary>
    /// Espaciamiento de los cercos / estribos de borde de aberturas en metros (ej. 0.10m = 10cm).
    /// </summary>
    public double OpeningStirrupSpacingMeters { get; set; } = 0.10;

    // --- REFUERZO DE MUROS INCLINADOS DE ESCALERA ---
    /// <summary>
    /// Indica si se incluye barra perimetral corrida paralela a la arista inclinada superior del muro de escalera.
    /// </summary>
    public bool IncludeInclinedTopBar { get; set; } = true;

    /// <summary>
    /// Nombre del tipo de barra para el refuerzo perimetral inclinado superior (ej. "1/2\"", "#4").
    /// </summary>
    public string? InclinedTopBarTypeName { get; set; }
}

/// <summary>
/// Resumen individual del armado de refuerzo generado para un muro específico.
/// </summary>
public class WallRebarCreatedSummary
{
    public long WallElementId { get; set; }
    public string WallName { get; set; } = string.Empty;
    public double LengthMeters { get; set; }
    public double HeightMeters { get; set; }
    public double ThicknessMeters { get; set; }
    public int MeshesCount { get; set; }
    public int VerticalBarsCount { get; set; }
    public int HorizontalBarsCount { get; set; }
    public int TotalRebarElementsCount { get; set; }
    public List<long> RebarElementIds { get; set; } = new();
    public string Status { get; set; } = "Success";
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Respuesta del procesamiento de armaduras de refuerzo en muros.
/// </summary>
public class CreateWallRebarResponse
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = string.Empty;
    public int TotalWallsProcessed { get; set; }
    public List<WallRebarCreatedSummary> Results { get; set; } = new();
}
