using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Información geométrica calculada de una viga de concreto para el armado de estribos.
/// Incluye límites físicos reales de la sección transversal extraídos del sólido 3D y recubrimiento de concreto.
/// </summary>
public class BeamGeometryInfo
{
    public FamilyInstance Beam { get; set; } = null!;
    public Line LocationLine { get; set; } = null!;
    public XYZ StartPoint { get; set; } = XYZ.Zero;
    public XYZ EndPoint { get; set; } = XYZ.Zero;
    public XYZ Direction { get; set; } = XYZ.BasisX;
    public XYZ TransverseDirection { get; set; } = XYZ.BasisY;
    public XYZ UpDirection { get; set; } = XYZ.BasisZ;

    /// <summary>
    /// Longitud total entre puntos de inserción (en pies y metros).
    /// </summary>
    public double TotalLengthFeet { get; set; }
    public double TotalLengthMeters => UnitConverter.FeetToMeters(TotalLengthFeet);

    /// <summary>
    /// Cara interior del apoyo inicial (en pies desde StartPoint).
    /// </summary>
    public double StartSupportFaceFeet { get; set; }

    /// <summary>
    /// Cara interior del apoyo final (en pies desde StartPoint).
    /// </summary>
    public double EndSupportFaceFeet { get; set; }

    /// <summary>
    /// Luz libre neta entre caras de apoyos (en pies y metros).
    /// </summary>
    public double ClearSpanFeet => Math.Max(0.1, EndSupportFaceFeet - StartSupportFaceFeet);
    public double ClearSpanMeters => UnitConverter.FeetToMeters(ClearSpanFeet);

    /// <summary>
    /// Dimensiones de la sección transversal de concreto (en pies y metros).
    /// </summary>
    public double WidthFeet { get; set; } = 0.984; // ~30 cm default
    public double HeightFeet { get; set; } = 1.640; // ~50 cm default
    public double WidthMeters => UnitConverter.FeetToMeters(WidthFeet);
    public double HeightMeters => UnitConverter.FeetToMeters(HeightFeet);

    /// <summary>
    /// Límites físicos reales de la sección proyectados respecto a LocationLine.
    /// </summary>
    public double SectionTMinFeet { get; set; } = -0.492;
    public double SectionTMaxFeet { get; set; } = 0.492;
    public double SectionUMinFeet { get; set; } = -1.640;
    public double SectionUMaxFeet { get; set; } = 0.0;

    /// <summary>
    /// Recubrimiento de concreto de la viga (en pies y metros).
    /// </summary>
    public double CoverTopFeet { get; set; } = 0.08202; // ~2.5 cm default
    public double CoverBottomFeet { get; set; } = 0.08202; // ~2.5 cm default
    public double CoverSidesFeet { get; set; } = 0.08202; // ~2.5 cm default

    public double CoverFeet
    {
        get => CoverSidesFeet;
        set
        {
            CoverSidesFeet = value;
            CoverTopFeet = value;
            CoverBottomFeet = value;
        }
    }
    public double CoverMeters => UnitConverter.FeetToMeters(CoverFeet);

    /// <summary>
    /// Dimensiones del estribo confinado (descontando el recubrimiento correspondiente en cada cara).
    /// </summary>
    public double StirrupWidthFeet => Math.Max(0.15, (SectionTMaxFeet - SectionTMinFeet) - 2 * CoverSidesFeet);
    public double StirrupHeightFeet => Math.Max(0.15, (SectionUMaxFeet - SectionUMinFeet) - (CoverTopFeet + CoverBottomFeet));

    /// <summary>
    /// Desplazamiento transversal y vertical del punto de inserción (esquina inferior-izquierda)
    /// respecto al eje de la viga LocationLine.
    /// </summary>
    public double StirrupTMinFeet => SectionTMinFeet + CoverSidesFeet;
    public double StirrupUMinFeet => SectionUMinFeet + CoverBottomFeet;

    /// <summary>
    /// Punto de la cara interior del apoyo inicial.
    /// </summary>
    public XYZ StartFacePoint => StartPoint + Direction * StartSupportFaceFeet;

    /// <summary>
    /// Punto de la cara interior del apoyo final.
    /// </summary>
    public XYZ EndFacePoint => StartPoint + Direction * EndSupportFaceFeet;
}

public static class BeamGeometryHelper
{
    /// <summary>
    /// Analiza la geometría analítica y física de la viga, detectando columnas o muros de apoyo
    /// para calcular la luz libre real entre elementos de confinamiento y los límites de la sección de concreto.
    /// </summary>
    public static BeamGeometryInfo ExtractGeometry(Document doc, FamilyInstance beam)
    {
        if (beam.Location is not LocationCurve locCurve || locCurve.Curve is not Line line)
        {
            throw new InvalidOperationException($"La viga ID {beam.Id.Value} no tiene una línea analítica válida.");
        }

        var pStart = line.GetEndPoint(0);
        var pEnd = line.GetEndPoint(1);
        var totalLength = pStart.DistanceTo(pEnd);
        var dir = (pEnd - pStart).Normalize();

        // Determinar vectores ortogonales de la sección transversal
        var up = XYZ.BasisZ;
        if (Math.Abs(dir.DotProduct(XYZ.BasisZ)) > 0.99)
        {
            // Viga vertical o muy inclinada
            up = XYZ.BasisY;
        }

        var trans = up.CrossProduct(dir).Normalize();
        up = dir.CrossProduct(trans).Normalize();

        // Considerar rotación de sección transversal si existe
        var rotParam = beam.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE);
        if (rotParam != null && rotParam.HasValue)
        {
            double rotAngle = rotParam.AsDouble();
            if (Math.Abs(rotAngle) > 0.001)
            {
                var rotTransform = Transform.CreateRotation(dir, rotAngle);
                trans = rotTransform.OfVector(trans).Normalize();
                up = rotTransform.OfVector(up).Normalize();
            }
        }

        var info = new BeamGeometryInfo
        {
            Beam = beam,
            LocationLine = line,
            StartPoint = pStart,
            EndPoint = pEnd,
            Direction = dir,
            TransverseDirection = trans,
            UpDirection = up,
            TotalLengthFeet = totalLength,
            StartSupportFaceFeet = 0.0,
            EndSupportFaceFeet = totalLength
        };

        // 1. Obtener límites físicos de la sección transversal y recubrimiento
        ExtractSectionBounds(doc, beam, info);

        // 2. Detección automática de caras de columnas / muros de soporte (Luz libre)
        DetectSupportFaces(doc, beam, info);

        return info;
    }

    private static void ExtractSectionBounds(Document doc, FamilyInstance beam, BeamGeometryInfo info)
    {
        // 1. Recubrimientos específicos por cara
        ExtractConcreteCovers(doc, beam, info);

        // 2. Extraer dimensiones nominales puras (sin cortes booleanos ni uniones con losas/columnas)
        var (nomWidth, nomHeight) = ExtractNominalDimensions(beam);

        // Fallbacks seguros si la familia no tiene ningún dato paramétrico legible
        double finalWidth = nomWidth ?? 0.984;  // ~30 cm default
        double finalHeight = nomHeight ?? 1.640; // ~50 cm default

        // 3. Calcular límites teóricos según la justificación oficial de la viga en Revit
        GetNominalJustificationBounds(beam, finalWidth, finalHeight,
            out double tNomMin, out double tNomMax, out double uNomMin, out double uNomMax);

        // 4. Analizar el sólido 3D de la instancia colocada en el modelo
        double? solidTMin = null, solidTMax = null;
        double? solidUMin = null, solidUMax = null;

        try
        {
            var opt = new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            var geomElem = beam.get_Geometry(opt);
            if (geomElem != null)
            {
                var vertices = new List<XYZ>();
                CollectSolidVertices(geomElem, vertices);

                if (vertices.Count >= 4)
                {
                    double tMin = double.MaxValue, tMax = double.MinValue;
                    double uMin = double.MaxValue, uMax = double.MinValue;

                    foreach (var pt in vertices)
                    {
                        var delta = pt - info.StartPoint;
                        double t = delta.DotProduct(info.TransverseDirection);
                        double u = delta.DotProduct(info.UpDirection);

                        if (t < tMin) tMin = t;
                        if (t > tMax) tMax = t;
                        if (u < uMin) uMin = u;
                        if (u > uMax) uMax = u;
                    }

                    if (tMax - tMin > 0.16 && uMax - uMin > 0.16)
                    {
                        solidTMin = tMin;
                        solidTMax = tMax;
                        solidUMin = uMin;
                        solidUMax = uMax;
                    }
                }
            }
        }
        catch { }

        // 5. Conciliación estricta entre el sólido físico y las dimensiones nominales de la viga:
        // El ancho y peralte del estribo NUNCA deben exceder ni reducirse respecto a las dimensiones
        // nominales de la sección de concreto. Si el sólido fue cortado por losas o expandido por uniones
        // con columnas/losas laterales, se garantiza que la sección conserve exactamente su forma nominal.
        double finalTMin = tNomMin;
        double finalTMax = tNomMax;
        double finalUMin = uNomMin;
        double finalUMax = uNomMax;

        if (solidUMin.HasValue && solidUMax.HasValue && solidTMin.HasValue && solidTMax.HasValue)
        {
            double sHeight = solidUMax.Value - solidUMin.Value;
            double sWidth = solidTMax.Value - solidTMin.Value;

            // 1. Manejo del ancho transversal (T):
            // Si el sólido conserva un ancho limpio que coincide con el nominal (dentro de un margen de ~2 cm)
            if (Math.Abs(sWidth - finalWidth) < 0.06)
            {
                // El sólido nos da la posición exacta centrada de la sección
                finalTMin = solidTMin.Value;
                finalTMax = solidTMin.Value + finalWidth;
            }
            else
            {
                // Si el sólido fue expandido por intersecciones con columnas o losas contiguas, o cortado,
                // respetamos estrictamente la justificación oficial de la viga en Revit.
                // Bajo ningún concepto se permite que el ancho exceda el ancho nominal.
                finalTMin = tNomMin;
                finalTMax = tNomMin + finalWidth;
            }

            // 2. Manejo del peralte vertical (U):
            // Si el sólido conserva la altura nominal completa (no está cortado por losa)
            if (Math.Abs(sHeight - finalHeight) < 0.06)
            {
                finalUMin = solidUMin.Value;
                finalUMax = solidUMin.Value + finalHeight;
            }
            else if (sHeight < finalHeight - 0.05)
            {
                // El sólido fue cortado (típico caso de viga unida a losa donde la losa corta el lomo superior).
                // Identificamos cuál de las dos caras (inferior o superior) permanece intacta.
                double distBottom = Math.Abs(solidUMin.Value - uNomMin);
                double distTop = Math.Abs(solidUMax.Value - uNomMax);

                if (distBottom <= distTop)
                {
                    // La cara inferior es la base intacta (fondo de viga); reconstruir hacia arriba con el peralte nominal completo
                    finalUMin = solidUMin.Value;
                    finalUMax = finalUMin + finalHeight;
                }
                else
                {
                    // La cara superior es la base intacta; reconstruir hacia abajo con el peralte nominal completo
                    finalUMax = solidUMax.Value;
                    finalUMin = finalUMax - finalHeight;
                }
            }
            else
            {
                // Sólido con uniones complejas; asegurar la justificación nominal completa
                finalUMin = uNomMin;
                finalUMax = uNomMin + finalHeight;
            }
        }

        info.WidthFeet = finalWidth;
        info.HeightFeet = finalHeight;
        info.SectionTMinFeet = finalTMin;
        info.SectionTMaxFeet = finalTMax;
        info.SectionUMinFeet = finalUMin;
        info.SectionUMaxFeet = finalUMax;
    }

    private static void ExtractConcreteCovers(Document doc, FamilyInstance beam, BeamGeometryInfo info)
    {
        double defaultCover = 0.08202; // ~2.5 cm default
        double? topCover = GetCoverDistance(doc, beam, BuiltInParameter.CLEAR_COVER_TOP);
        double? bottomCover = GetCoverDistance(doc, beam, BuiltInParameter.CLEAR_COVER_BOTTOM);
        double? otherCover = GetCoverDistance(doc, beam, BuiltInParameter.CLEAR_COVER_OTHER)
                          ?? GetCoverDistance(doc, beam, BuiltInParameter.CLEAR_COVER_EXTERIOR);

        double baseCover = otherCover ?? bottomCover ?? topCover ?? defaultCover;
        info.CoverSidesFeet = otherCover ?? baseCover;
        info.CoverTopFeet = topCover ?? baseCover;
        info.CoverBottomFeet = bottomCover ?? baseCover;
    }

    private static double? GetCoverDistance(Document doc, FamilyInstance beam, BuiltInParameter bip)
    {
        try
        {
            var param = beam.get_Parameter(bip);
            if (param != null && param.HasValue && param.AsElementId() != ElementId.InvalidElementId)
            {
                if (doc.GetElement(param.AsElementId()) is RebarCoverType coverType)
                {
                    if (coverType.CoverDistance > 0.005)
                    {
                        return coverType.CoverDistance;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static (double? width, double? height) ExtractNominalDimensions(FamilyInstance beam)
    {
        var symbol = beam.Symbol;

        // 1. BuiltInParameters de secciones estructurales estandarizadas en Revit
        double? width = GetBuiltInParam(beam, BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH)
                     ?? (symbol != null ? GetBuiltInParam(symbol, BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH) : null);

        double? height = GetBuiltInParam(beam, BuiltInParameter.STRUCTURAL_SECTION_COMMON_HEIGHT)
                      ?? (symbol != null ? GetBuiltInParam(symbol, BuiltInParameter.STRUCTURAL_SECTION_COMMON_HEIGHT) : null);

        // 2. Búsqueda paramétrica exhaustiva (insensible a mayúsculas/minúsculas) en Instancia y Tipo
        if (!width.HasValue)
        {
            width = FindDimensionParameter(beam, isWidth: true)
                 ?? (symbol != null ? FindDimensionParameter(symbol, isWidth: true) : null);
        }

        if (!height.HasValue)
        {
            height = FindDimensionParameter(beam, isWidth: false)
                  ?? (symbol != null ? FindDimensionParameter(symbol, isWidth: false) : null);
        }

        // 3. Geometría prístina del FamilySymbol (la familia original en Revit nunca está cortada por uniones)
        if ((!width.HasValue || !height.HasValue) && symbol != null)
        {
            var (symW, symH) = ExtractDimensionsFromSymbolGeometry(symbol);
            if (symW.HasValue && !width.HasValue) width = symW;
            if (symH.HasValue && !height.HasValue) height = symH;
        }

        // 4. Extracción desde el nombre del tipo o familia (ej. "30x60", "300 x 600 mm", "0.30 x 0.60 m")
        if ((!width.HasValue || !height.HasValue) && symbol != null)
        {
            var (nameW, nameH) = ParseDimensionsFromName(symbol.Name);
            if (!nameW.HasValue || !nameH.HasValue)
            {
                var (famW, famH) = ParseDimensionsFromName(symbol.FamilyName);
                if (famW.HasValue && famH.HasValue)
                {
                    nameW ??= famW;
                    nameH ??= famH;
                }
            }

            if (nameW.HasValue && !width.HasValue) width = nameW;
            if (nameH.HasValue && !height.HasValue) height = nameH;
        }

        return (width, height);
    }

    private static double? GetBuiltInParam(Element elem, BuiltInParameter bip)
    {
        try
        {
            var p = elem.get_Parameter(bip);
            if (p != null && p.HasValue && p.StorageType == StorageType.Double)
            {
                double val = p.AsDouble();
                if (val > 0.05) return val;
            }
        }
        catch { }
        return null;
    }

    private static double? FindDimensionParameter(Element elem, bool isWidth)
    {
        // Coincidencias exactas de alta prioridad
        string[] widthExact = ["b", "width", "ancho", "base", "w", "b_beam", "ancho viga", "base viga", "ancho de viga", "dimension b"];
        string[] heightExact = ["h", "height", "peralte", "altura", "depth", "canto", "d", "h_beam", "peralte viga", "altura viga", "peralte de viga", "altura de viga", "dimension h"];

        var targetList = isWidth ? widthExact : heightExact;

        foreach (var name in targetList)
        {
            var p = elem.LookupParameter(name);
            if (p != null && p.HasValue && p.StorageType == StorageType.Double)
            {
                double val = p.AsDouble();
                if (val > 0.05) return val;
            }
        }

        // Búsqueda inteligente por coincidencia parcial en todos los parámetros numéricos
        foreach (Parameter p in elem.Parameters)
        {
            if (p.StorageType != StorageType.Double || !p.HasValue) continue;
            string pName = p.Definition.Name.Trim().ToLowerInvariant();

            // Descartar parámetros que no correspondan a la sección
            if (pName.Contains("offset") || pName.Contains("desfase") ||
                pName.Contains("cover") || pName.Contains("recubrimiento") ||
                pName.Contains("longitud") || pName.Contains("length") || pName.Contains("cut") ||
                pName.Contains("corte") || pName.Contains("elevaci") || pName.Contains("area") || pName.Contains("volum"))
            {
                continue;
            }

            if (isWidth)
            {
                if (pName == "b" || pName == "w" || pName.StartsWith("b ") || pName.StartsWith("b(") ||
                    pName.EndsWith(" b") || pName.Contains("ancho") || pName.Contains("width") ||
                    (pName.Contains("base") && !pName.Contains("base level") && !pName.Contains("nivel base")))
                {
                    double val = p.AsDouble();
                    if (val > 0.1 && val < 15.0) return val;
                }
            }
            else
            {
                if (pName == "h" || pName == "d" || pName.StartsWith("h ") || pName.StartsWith("h(") ||
                    pName.EndsWith(" h") || pName.Contains("peralte") || pName.Contains("height") ||
                    pName.Contains("altura") || pName.Contains("canto") || pName.Contains("depth"))
                {
                    double val = p.AsDouble();
                    if (val > 0.1 && val < 25.0) return val;
                }
            }
        }

        return null;
    }

    private static (double? width, double? height) ExtractDimensionsFromSymbolGeometry(FamilySymbol symbol)
    {
        try
        {
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };
            var geom = symbol.get_Geometry(opt);
            if (geom == null) return (null, null);

            var vertices = new List<XYZ>();
            CollectSolidVertices(geom, vertices);
            if (vertices.Count < 4) return (null, null);

            double minX = vertices.Min(v => v.X), maxX = vertices.Max(v => v.X);
            double minY = vertices.Min(v => v.Y), maxY = vertices.Max(v => v.Y);
            double minZ = vertices.Min(v => v.Z), maxZ = vertices.Max(v => v.Z);

            double dx = maxX - minX;
            double dy = maxY - minY;
            double dz = maxZ - minZ;

            // En familias estándar de vigas, la longitud de extrusión es la dimensión más grande.
            // Las otras dos dimensiones representan exactamente el ancho y peralte de la sección transversal.
            var dims = new List<double> { dx, dy, dz }.Where(d => d > 0.1).OrderBy(d => d).ToList();
            if (dims.Count >= 2)
            {
                double w = dims[0];
                double h = dims[1];
                if (w > 0.16 && w < 16.0 && h > 0.16 && h < 16.0)
                {
                    return (w, h);
                }
            }
        }
        catch { }
        return (null, null);
    }

    private static (double? width, double? height) ParseDimensionsFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, null);
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                name,
                @"(?:^|[^\d.,])(\d+(?:[.,]\d+)?)\s*(cm|mm|m)?\s*[xX*x/]\s*(\d+(?:[.,]\d+)?)\s*(cm|mm|m)?");

            if (match.Success)
            {
                string s1 = match.Groups[1].Value.Replace(',', '.');
                string u1 = match.Groups[2].Value.ToLowerInvariant();
                string s2 = match.Groups[3].Value.Replace(',', '.');
                string u2 = match.Groups[4].Value.ToLowerInvariant();

                if (double.TryParse(s1, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v1) &&
                    double.TryParse(s2, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v2))
                {
                    string unit = !string.IsNullOrEmpty(u2) ? u2 : (!string.IsNullOrEmpty(u1) ? u1 : "");
                    double wMeters, hMeters;

                    if (unit == "mm" || (string.IsNullOrEmpty(unit) && v1 >= 80 && v2 >= 80))
                    {
                        wMeters = Math.Min(v1, v2) / 1000.0;
                        hMeters = Math.Max(v1, v2) / 1000.0;
                    }
                    else if (unit == "m" || (string.IsNullOrEmpty(unit) && v1 <= 3.0 && v2 <= 3.0))
                    {
                        wMeters = Math.Min(v1, v2);
                        hMeters = Math.Max(v1, v2);
                    }
                    else // default centímetros
                    {
                        wMeters = Math.Min(v1, v2) / 100.0;
                        hMeters = Math.Max(v1, v2) / 100.0;
                    }

                    double wFeet = UnitConverter.MetersToFeet(wMeters);
                    double hFeet = UnitConverter.FeetToMeters(hMeters); // wait! FeetToMeters or MetersToFeet? UnitConverter.MetersToFeet!
                    hFeet = UnitConverter.MetersToFeet(hMeters);

                    if (wFeet > 0.16 && wFeet < 16.0 && hFeet > 0.16 && hFeet < 16.0)
                    {
                        return (wFeet, hFeet);
                    }
                }
            }
        }
        catch { }
        return (null, null);
    }

    private static void GetNominalJustificationBounds(
        FamilyInstance beam,
        double nomWidth,
        double nomHeight,
        out double tNomMin,
        out double tNomMax,
        out double uNomMin,
        out double uNomMax)
    {
        // Z Justification en Revit API: Top = 0, Center = 1, Origin = 2, Bottom = 3
        ZJustification zJust = ZJustification.Top;
        var pZJust = beam.get_Parameter(BuiltInParameter.Z_JUSTIFICATION);
        if (pZJust != null && pZJust.HasValue) zJust = (ZJustification)pZJust.AsInteger();

        double zOffset = 0.0;
        var pZOffset = beam.get_Parameter(BuiltInParameter.Z_OFFSET_VALUE);
        if (pZOffset != null && pZOffset.HasValue) zOffset = pZOffset.AsDouble();

        // Y Justification en Revit API: Left = 0, Center = 1, Origin = 2, Right = 3
        YJustification yJust = YJustification.Origin;
        var pYJust = beam.get_Parameter(BuiltInParameter.Y_JUSTIFICATION);
        if (pYJust != null && pYJust.HasValue) yJust = (YJustification)pYJust.AsInteger();

        double yOffset = 0.0;
        var pYOffset = beam.get_Parameter(BuiltInParameter.Y_OFFSET_VALUE);
        if (pYOffset != null && pYOffset.HasValue) yOffset = pYOffset.AsDouble();

        // Límites verticales (U)
        switch (zJust)
        {
            case ZJustification.Bottom:
                uNomMin = 0.0 + zOffset;
                uNomMax = nomHeight + zOffset;
                break;
            case ZJustification.Center:
                uNomMin = -nomHeight / 2.0 + zOffset;
                uNomMax = nomHeight / 2.0 + zOffset;
                break;
            case ZJustification.Top:
            case ZJustification.Origin:
            default:
                // En familias estándar de concreto, Top y Origin colocan la cara superior de la viga en el eje analítico
                uNomMin = -nomHeight + zOffset;
                uNomMax = 0.0 + zOffset;
                break;
        }

        // Límites transversales (T)
        switch (yJust)
        {
            case YJustification.Left:
                tNomMin = 0.0 + yOffset;
                tNomMax = nomWidth + yOffset;
                break;
            case YJustification.Right:
                tNomMin = -nomWidth + yOffset;
                tNomMax = 0.0 + yOffset;
                break;
            case YJustification.Center:
            case YJustification.Origin:
            default:
                // Tanto Center como Origin en vigas de concreto centran simétricamente el ancho respecto al eje
                tNomMin = -nomWidth / 2.0 + yOffset;
                tNomMax = nomWidth / 2.0 + yOffset;
                break;
        }
    }

    private static void CollectSolidVertices(GeometryElement geomElem, List<XYZ> vertices)
    {
        foreach (var geomObj in geomElem)
        {
            if (geomObj is Solid solid && solid.Volume > 0.0001)
            {
                foreach (Edge edge in solid.Edges)
                {
                    var curve = edge.AsCurve();
                    if (curve != null)
                    {
                        vertices.Add(curve.GetEndPoint(0));
                        vertices.Add(curve.GetEndPoint(1));
                    }
                }
            }
            else if (geomObj is GeometryInstance geomInst)
            {
                var instGeom = geomInst.GetInstanceGeometry();
                if (instGeom != null)
                {
                    CollectSolidVertices(instGeom, vertices);
                }
            }
        }
    }

    private static void DetectSupportFaces(Document doc, FamilyInstance beam, BeamGeometryInfo info)
    {
        try
        {
            var pStart = info.StartPoint;
            var pEnd = info.EndPoint;
            var dir = info.Direction;
            var totalLength = info.TotalLengthFeet;

            // Filtro por categorías de confinamiento y soporte:
            // 1. Columnas estructurales (OST_StructuralColumns)
            // 2. Muros estructurales (OST_Walls)
            // 3. Vigas principales o de apoyo transversal (OST_StructuralFraming)
            var catFilter = new LogicalOrFilter(new ElementFilter[]
            {
                new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns),
                new ElementCategoryFilter(BuiltInCategory.OST_Walls),
                new ElementCategoryFilter(BuiltInCategory.OST_StructuralFraming)
            });

            // Usar filtro por BoundingBox expandido (0.5 pies ~ 15 cm) para capturar elementos de apoyo
            // incluso si Revit resolvió uniones (joins) dejando intersecciones limpias de volumen cero
            var beamBb = beam.get_BoundingBox(null);
            FilteredElementCollector collector;
            if (beamBb != null)
            {
                var outline = new Outline(
                    beamBb.Min - new XYZ(0.5, 0.5, 0.5),
                    beamBb.Max + new XYZ(0.5, 0.5, 0.5));
                collector = new FilteredElementCollector(doc)
                    .WherePasses(catFilter)
                    .WherePasses(new BoundingBoxIntersectsFilter(outline));
            }
            else
            {
                collector = new FilteredElementCollector(doc)
                    .WherePasses(catFilter)
                    .WherePasses(new ElementIntersectsElementFilter(beam));
            }

            var supportElements = collector
                .Where(e => e.Id.Value != beam.Id.Value)
                .ToList();

            double startLimit = 0.0;
            double endLimit = totalLength;

            var opt = new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            foreach (var elem in supportElements)
            {
                // Si el elemento es otra viga, verificar que sea de apoyo transversal y no una viga paralela/colineal continua
                if (elem is FamilyInstance otherBeam &&
                    otherBeam.Category?.Id.Value == (int)BuiltInCategory.OST_StructuralFraming)
                {
                    if (otherBeam.Location is LocationCurve otherLoc && otherLoc.Curve is Line otherLine)
                    {
                        var otherDir = (otherLine.GetEndPoint(1) - otherLine.GetEndPoint(0)).Normalize();
                        // Si el ángulo es muy cerrado (paralela o colineal > ~30°), no actúa como apoyo transversal
                        if (Math.Abs(otherDir.DotProduct(dir)) > 0.85)
                        {
                            continue;
                        }
                    }
                }

                // Extraer límites físicos del elemento proyectados sobre el eje longitudinal de la viga
                if (!TryGetElementExtentsAlongDirection(elem, opt, pStart, dir, out double elemVMin, out double elemVMax))
                {
                    continue;
                }

                // Apoyo cercano al inicio de la viga (la cara interior que da al vano libre es elemVMax)
                if (elemVMin < totalLength * 0.35 && elemVMax > 0.0)
                {
                    if (elemVMax > startLimit && elemVMax < totalLength * 0.45)
                    {
                        startLimit = elemVMax;
                    }
                }
                // Apoyo cercano al fin de la viga (la cara interior que da al vano libre es elemVMin)
                else if (elemVMax > totalLength * 0.65 && elemVMin < totalLength)
                {
                    if (elemVMin < endLimit && elemVMin > totalLength * 0.55)
                    {
                        endLimit = elemVMin;
                    }
                }
            }

            if (endLimit > startLimit + 0.5) // Al menos medio pie de luz
            {
                info.StartSupportFaceFeet = startLimit;
                info.EndSupportFaceFeet = endLimit;
            }
        }
        catch
        {
            // Si la detección geométrica falla, se usa el largo total de la viga como fallback seguro
            info.StartSupportFaceFeet = 0.0;
            info.EndSupportFaceFeet = info.TotalLengthFeet;
        }
    }

    private static bool TryGetElementExtentsAlongDirection(
        Element elem,
        Options opt,
        XYZ pStart,
        XYZ dir,
        out double vMin,
        out double vMax)
    {
        vMin = double.MaxValue;
        vMax = double.MinValue;

        try
        {
            var vertices = new List<XYZ>();
            var geomElem = elem.get_Geometry(opt);
            if (geomElem != null)
            {
                CollectSolidVertices(geomElem, vertices);
            }

            if (vertices.Count >= 4)
            {
                foreach (var pt in vertices)
                {
                    double v = (pt - pStart).DotProduct(dir);
                    if (v < vMin) vMin = v;
                    if (v > vMax) vMax = v;
                }
                return true;
            }
        }
        catch
        {
            // Continuar con fallback BoundingBox
        }

        var bb = elem.get_BoundingBox(null);
        if (bb != null)
        {
            XYZ[] corners =
            [
                new(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new(bb.Max.X, bb.Max.Y, bb.Max.Z)
            ];

            foreach (var corner in corners)
            {
                double v = (corner - pStart).DotProduct(dir);
                if (v < vMin) vMin = v;
                if (v > vMax) vMax = v;
            }
            return true;
        }

        return false;
    }
}
