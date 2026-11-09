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
    public double CoverFeet { get; set; } = 0.08202; // ~2.5 cm default
    public double CoverMeters => UnitConverter.FeetToMeters(CoverFeet);

    /// <summary>
    /// Dimensiones del estribo confinado (descontando el recubrimiento a cada lado).
    /// </summary>
    public double StirrupWidthFeet => Math.Max(0.15, (SectionTMaxFeet - SectionTMinFeet) - 2 * CoverFeet);
    public double StirrupHeightFeet => Math.Max(0.15, (SectionUMaxFeet - SectionUMinFeet) - 2 * CoverFeet);

    /// <summary>
    /// Desplazamiento transversal y vertical del punto de inserción (esquina inferior-izquierda)
    /// respecto al eje de la viga LocationLine.
    /// </summary>
    public double StirrupTMinFeet => SectionTMinFeet + CoverFeet;
    public double StirrupUMinFeet => SectionUMinFeet + CoverFeet;

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
        info.CoverFeet = ExtractConcreteCover(doc, beam);

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

                    double width = tMax - tMin;
                    double height = uMax - uMin;

                    // Verificación de plausibilidad física (> 5 cm y < 5 m)
                    if (width > 0.16 && width < 16.0 && height > 0.16 && height < 16.0)
                    {
                        info.WidthFeet = width;
                        info.HeightFeet = height;
                        info.SectionTMinFeet = tMin;
                        info.SectionTMaxFeet = tMax;
                        info.SectionUMinFeet = uMin;
                        info.SectionUMaxFeet = uMax;
                        return;
                    }
                }
            }
        }
        catch
        {
            // Fallback a lectura de parámetros
        }

        FallbackSectionBoundsFromParameters(beam, info);
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

    private static double ExtractConcreteCover(Document doc, FamilyInstance beam)
    {
        try
        {
            BuiltInParameter[] coverParams =
            [
                BuiltInParameter.CLEAR_COVER_OTHER,
                BuiltInParameter.CLEAR_COVER_BOTTOM,
                BuiltInParameter.CLEAR_COVER_TOP,
                BuiltInParameter.CLEAR_COVER_EXTERIOR
            ];

            foreach (var bip in coverParams)
            {
                var param = beam.get_Parameter(bip);
                if (param != null && param.HasValue && param.AsElementId() != ElementId.InvalidElementId)
                {
                    if (doc.GetElement(param.AsElementId()) is RebarCoverType coverType)
                    {
                        if (coverType.CoverDistance > 0.01)
                        {
                            return coverType.CoverDistance;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }

        // Default ~2.5 cm (0.08202 pies)
        return 0.08202;
    }

    private static void FallbackSectionBoundsFromParameters(FamilyInstance beam, BeamGeometryInfo info)
    {
        var symbol = beam.Symbol;
        double? width = GetParameterAsDouble(beam, "b")
                     ?? (symbol != null ? GetParameterAsDouble(symbol, "b") : null)
                     ?? GetParameterAsDouble(beam, "Width")
                     ?? (symbol != null ? GetParameterAsDouble(symbol, "Width") : null)
                     ?? GetParameterAsDouble(beam, "Ancho")
                     ?? (symbol != null ? GetParameterAsDouble(symbol, "Ancho") : null)
                     ?? GetParameterAsDouble(beam, "Base")
                     ?? (symbol != null ? GetParameterAsDouble(symbol, "Base") : null);

        double? height = GetParameterAsDouble(beam, "h")
                      ?? (symbol != null ? GetParameterAsDouble(symbol, "h") : null)
                      ?? GetParameterAsDouble(beam, "Height")
                      ?? (symbol != null ? GetParameterAsDouble(symbol, "Height") : null)
                      ?? GetParameterAsDouble(beam, "Peralte")
                      ?? (symbol != null ? GetParameterAsDouble(symbol, "Peralte") : null)
                      ?? GetParameterAsDouble(beam, "Altura")
                      ?? (symbol != null ? GetParameterAsDouble(symbol, "Altura") : null);

        if (width.HasValue && width.Value > 0.1) info.WidthFeet = width.Value;
        if (height.HasValue && height.Value > 0.1) info.HeightFeet = height.Value;

        // Asumir justificación típica de viga de hormigón: Top-Center
        info.SectionTMinFeet = -info.WidthFeet / 2.0;
        info.SectionTMaxFeet = info.WidthFeet / 2.0;
        info.SectionUMinFeet = -info.HeightFeet;
        info.SectionUMaxFeet = 0.0;
    }

    private static double? GetParameterAsDouble(Element elem, string paramName)
    {
        var param = elem.LookupParameter(paramName);
        if (param != null && param.HasValue && param.StorageType == StorageType.Double)
        {
            var val = param.AsDouble();
            if (val > 0.001) return val;
        }
        return null;
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
