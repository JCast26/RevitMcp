using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Representa los límites locales (S longitudinal y Z vertical, en pies) de una abertura o vano en el muro.
/// </summary>
public class WallOpeningInfo
{
    public double SMinFeet { get; set; }
    public double SMaxFeet { get; set; }
    public double ZMinFeet { get; set; }
    public double ZMaxFeet { get; set; }

    public double WidthFeet => Math.Max(0.0, SMaxFeet - SMinFeet);
    public double HeightFeet => Math.Max(0.0, ZMaxFeet - ZMinFeet);
}

/// <summary>
/// Contiene los datos geométricos calculados de un muro estructural para la colocación de armaduras.
/// </summary>
public class WallGeometryInfo
{
    public Wall Wall { get; set; } = null!;
    public Line LocationLine { get; set; } = null!;
    public XYZ StartPoint { get; set; } = XYZ.Zero;
    public XYZ EndPoint { get; set; } = XYZ.Zero;
    public XYZ Direction { get; set; } = XYZ.BasisX;
    public XYZ Normal { get; set; } = XYZ.BasisY;
    public XYZ UpDirection { get; set; } = XYZ.BasisZ;

    /// <summary>
    /// Longitud analítica en planta del muro (pies y metros).
    /// </summary>
    public double LengthFeet { get; set; }
    public double LengthMeters => UnitConverter.FeetToMeters(LengthFeet);

    /// <summary>
    /// Espesor total del muro (pies y metros).
    /// </summary>
    public double ThicknessFeet { get; set; }
    public double ThicknessMeters => UnitConverter.FeetToMeters(ThicknessFeet);

    /// <summary>
    /// Cota Z física inferior del muro en coordenadas de modelo (pies).
    /// </summary>
    public double ZMinFeet { get; set; }

    /// <summary>
    /// Cota Z física superior del muro en coordenadas de modelo (pies).
    /// </summary>
    public double ZMaxFeet { get; set; }

    /// <summary>
    /// Altura libre total física del muro (pies y metros).
    /// </summary>
    public double HeightFeet => Math.Max(0.1, ZMaxFeet - ZMinFeet);
    public double HeightMeters => UnitConverter.FeetToMeters(HeightFeet);

    /// <summary>
    /// Recubrimiento libre de concreto asignado en Revit (pies y metros).
    /// </summary>
    public double CoverFeet { get; set; } = 0.08202; // ~2.5 cm default
    public double CoverMeters => UnitConverter.FeetToMeters(CoverFeet);

    /// <summary>
    /// Nombre del tipo de recubrimiento asignado en Revit.
    /// </summary>
    public string CoverTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Lista de aberturas / vanos detectados en el muro, ordenados longitudinalmente.
    /// </summary>
    public List<WallOpeningInfo> Openings { get; set; } = new();

    /// <summary>
    /// Perfil de elevación y geometría de contorno del muro (detecta muros inclinados de escalera).
    /// </summary>
    public ElevationProfileInfo Profile { get; set; } = new();
}

/// <summary>
/// Información del perfil de elevación de un muro (detecta si el perfil fue editado con aristas inclinadas/escalonadas).
/// </summary>
public class ElevationProfileInfo
{
    public bool IsInclined { get; set; }
    public double StartHeightFeet { get; set; }
    public double EndHeightFeet { get; set; }
    public double Slope { get; set; } // dz/ds
    public List<(double S0, double Z0, double S1, double Z1)> BoundarySegments { get; set; } = new();

    public double GetTopElevationAt(double s, double defaultZMax)
    {
        double maxZ = double.MinValue;
        foreach (var seg in BoundarySegments)
        {
            double sMin = Math.Min(seg.S0, seg.S1);
            double sMax = Math.Max(seg.S0, seg.S1);
            if (s >= sMin - 0.005 && s <= sMax + 0.005 && Math.Abs(sMax - sMin) > 0.005)
            {
                double factor = (s - seg.S0) / (seg.S1 - seg.S0);
                double z = seg.Z0 + factor * (seg.Z1 - seg.Z0);
                if (z > maxZ) maxZ = z;
            }
        }
        return maxZ > double.MinValue ? maxZ : defaultZMax;
    }

    public (double SMin, double SMax) GetHorizontalSpanAt(double z, double lenFeet)
    {
        double minS = double.MaxValue;
        double maxS = double.MinValue;

        foreach (var seg in BoundarySegments)
        {
            double zMin = Math.Min(seg.Z0, seg.Z1);
            double zMax = Math.Max(seg.Z0, seg.Z1);
            if (z >= zMin - 0.005 && z <= zMax + 0.005)
            {
                if (Math.Abs(zMax - zMin) < 0.005)
                {
                    minS = Math.Min(minS, Math.Min(seg.S0, seg.S1));
                    maxS = Math.Max(maxS, Math.Max(seg.S0, seg.S1));
                }
                else
                {
                    double factor = (z - seg.Z0) / (seg.Z1 - seg.Z0);
                    double s = seg.S0 + factor * (seg.S1 - seg.S0);
                    if (s < minS) minS = s;
                    if (s > maxS) maxS = s;
                }
            }
        }

        if (minS > maxS)
        {
            return (0.0, lenFeet);
        }
        return (Math.Max(0.0, minS), Math.Min(lenFeet, maxS));
    }

    /// <summary>
    /// Extrae los tramos que componen la coronación superior del muro ordenados de izquierda a derecha en S.
    /// </summary>
    public List<TopProfileZone> GetTopProfileZones(double wallLen, double zMax)
    {
        var rawTopSegs = new List<(double s0, double z0, double s1, double z1)>();
        foreach (var seg in BoundarySegments)
        {
            double sMin = Math.Min(seg.S0, seg.S1);
            double sMax = Math.Max(seg.S0, seg.S1);
            if (sMax - sMin < 0.05) continue; // segmento vertical

            double sMid = (sMin + sMax) * 0.5;
            double factor = (sMid - seg.S0) / (seg.S1 - seg.S0);
            double zMid = seg.Z0 + factor * (seg.Z1 - seg.Z0);
            double zTopMid = GetTopElevationAt(sMid, zMax);

            if (Math.Abs(zMid - zTopMid) < 0.08)
            {
                double z0 = (seg.S0 <= seg.S1) ? seg.Z0 : seg.Z1;
                double z1 = (seg.S0 <= seg.S1) ? seg.Z1 : seg.Z0;
                rawTopSegs.Add((sMin, z0, sMax, z1));
            }
        }

        // Ordenar por S de izquierda a derecha
        rawTopSegs = rawTopSegs.OrderBy(s => s.s0).ToList();

        var zones = new List<TopProfileZone>();
        foreach (var seg in rawTopSegs)
        {
            zones.Add(new TopProfileZone
            {
                SStart = seg.s0,
                SEnd = seg.s1,
                ZStart = seg.z0,
                ZEnd = seg.z1
            });
        }

        if (zones.Count == 0)
        {
            zones.Add(new TopProfileZone
            {
                SStart = 0.0,
                SEnd = wallLen,
                ZStart = zMax,
                ZEnd = zMax
            });
        }

        return zones;
    }
}

/// <summary>
/// Representa un tramo del borde superior del perfil (recto horizontal o inclinado).
/// </summary>
public class TopProfileZone
{
    public double SStart { get; set; }
    public double SEnd { get; set; }
    public double ZStart { get; set; }
    public double ZEnd { get; set; }
    public bool IsSloped => Math.Abs(ZEnd - ZStart) >= 0.05;
    public double AvgZ => (ZStart + ZEnd) * 0.5;
}

/// <summary>
/// Extrae la geometría física y de referencia de un muro de concreto para el armado de armadura.
/// Garantiza la detección estricta de las cotas Z superior e inferior reales del muro,
/// contemplando desfases de nivel base, restricciones de parte superior o sólidos físicos.
/// </summary>
public static class WallGeometryHelper
{
    public static WallGeometryInfo ExtractGeometry(Document doc, Wall wall)
    {
        if (wall.Location is not LocationCurve locCurve || locCurve.Curve is not Line line)
        {
            throw new InvalidOperationException($"El muro ID {wall.Id.Value} no posee un eje analítico recto compatible.");
        }

        var pStart = line.GetEndPoint(0);
        var pEnd = line.GetEndPoint(1);

        // Vector longitudinal estrictamente horizontal en planta (Z = 0)
        var dir2D = new XYZ(pEnd.X - pStart.X, pEnd.Y - pStart.Y, 0.0);
        double len2D = dir2D.GetLength();
        if (len2D < 0.01)
        {
            throw new InvalidOperationException($"El muro ID {wall.Id.Value} tiene una longitud nula o insignificante.");
        }
        var dir = dir2D.Normalize();

        // Vector normal transversal estrictamente horizontal en planta (Z = 0)
        XYZ normal;
        try
        {
            var orient = wall.Orientation;
            var norm2D = new XYZ(orient.X, orient.Y, 0.0);
            normal = norm2D.GetLength() > 0.01 ? norm2D.Normalize() : XYZ.BasisZ.CrossProduct(dir).Normalize();
        }
        catch
        {
            normal = XYZ.BasisZ.CrossProduct(dir).Normalize();
        }

        // --- CÁLCULO PRECISO Y SEGURO DE COTAS Z ---
        // Método A: Límites calculados a partir de los parámetros de nivel y desfase de Revit
        double paramZMin, paramZMax;
        bool hasParams = TryGetParameterZBounds(doc, wall, pStart.Z, out paramZMin, out paramZMax);

        // Método B: Límites extraídos del sólido 3D y BoundingBox
        double geomZMin, geomZMax;
        bool hasGeom = TryGetGeometryZBounds(wall, out geomZMin, out geomZMax);

        double zMin, zMax;
        if (hasGeom && (geomZMax - geomZMin) > 0.1)
        {
            zMin = geomZMin;
            zMax = geomZMax;
        }
        else if (hasParams && (paramZMax - paramZMin) > 0.1)
        {
            zMin = paramZMin;
            zMax = paramZMax;
        }
        else
        {
            // Fallback con altura desconectada
            var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            double h = heightParam != null && heightParam.HasValue ? heightParam.AsDouble() : 9.8425; // ~3m
            zMin = pStart.Z;
            zMax = zMin + h;
        }

        double thickness = wall.Width;
        var (cover, coverName) = ExtractConcreteCover(doc, wall);
        var (openings, profile) = ExtractWallOpenings(doc, wall, pStart, dir, normal, len2D, zMin, zMax);

        return new WallGeometryInfo
        {
            Wall = wall,
            LocationLine = line,
            StartPoint = pStart,
            EndPoint = pEnd,
            Direction = dir,
            Normal = normal,
            UpDirection = XYZ.BasisZ,
            LengthFeet = len2D,
            ThicknessFeet = thickness,
            ZMinFeet = zMin,
            ZMaxFeet = zMax,
            CoverFeet = cover,
            CoverTypeName = coverName,
            Openings = openings,
            Profile = profile
        };
    }

    /// <summary>
    /// Cuenta el total de aberturas detectadas en una colección de muros seleccionados.
    /// </summary>
    public static int CountOpeningsInWalls(Document doc, IEnumerable<long>? wallIds)
    {
        if (doc == null || wallIds == null) return 0;
        int total = 0;
        foreach (var id in wallIds)
        {
            try
            {
                if (doc.GetElement(new ElementId(id)) is Wall wall)
                {
                    var geom = ExtractGeometry(doc, wall);
                    total += geom.Openings.Count;
                }
            }
            catch { }
        }
        return total;
    }

    /// <summary>
    /// Detecta todas las aberturas en un muro: elementos Opening (Rectangular Straight Wall Opening),
    /// insertos (Puertas/Ventanas/Modelos genéricos) y recortes o bucles de perfil editado (Edit Profile).
    /// </summary>
    public static (List<WallOpeningInfo> Openings, ElevationProfileInfo Profile) ExtractWallOpenings(
        Document doc,
        Wall wall,
        XYZ pStart,
        XYZ dir,
        XYZ normal,
        double len2D,
        double zMin,
        double zMax)
    {
        var rawOpenings = new List<WallOpeningInfo>();
        var profile = new ElevationProfileInfo();

        double ProjectS(XYZ pt) => (pt.X - pStart.X) * dir.X + (pt.Y - pStart.Y) * dir.Y;

        void AddCandidate(double s0, double s1, double z0, double z1)
        {
            double sMin = Math.Clamp(Math.Min(s0, s1), 0.0, len2D);
            double sMax = Math.Clamp(Math.Max(s0, s1), 0.0, len2D);
            double zBot = Math.Clamp(Math.Min(z0, z1), zMin, zMax);
            double zTop = Math.Clamp(Math.Max(z0, z1), zMin, zMax);

            // Descartar ranuras diminutas (< ~6 cm) o que abarquen casi todo el muro
            if ((sMax - sMin) < 0.20 || (zTop - zBot) < 0.20) return;
            if ((sMax - sMin) > len2D * 0.96 && (zTop - zBot) > (zMax - zMin) * 0.96) return;

            rawOpenings.Add(new WallOpeningInfo
            {
                SMinFeet = sMin,
                SMaxFeet = sMax,
                ZMinFeet = zBot,
                ZMaxFeet = zTop
            });
        }

        // 1. Vía wall.FindInserts (detecta Rectangular Straight Wall Opening, Puertas, Ventanas y Vacíos anfitrionados)
        try
        {
            var insertIds = wall.FindInserts(true, false, false, true);
            if (insertIds != null)
            {
                foreach (var insId in insertIds)
                {
                    var elem = doc.GetElement(insId);
                    if (elem == null) continue;

                    if (elem is Opening op)
                    {
                        if (op.IsRectBoundary && op.BoundaryRect != null && op.BoundaryRect.Count >= 2)
                        {
                            var pt0 = op.BoundaryRect[0];
                            var pt1 = op.BoundaryRect[1];
                            AddCandidate(ProjectS(pt0), ProjectS(pt1), pt0.Z, pt1.Z);
                            continue;
                        }
                        if (op.BoundaryCurves != null && op.BoundaryCurves.Size > 0)
                        {
                            double minS = double.MaxValue, maxS = double.MinValue;
                            double minZ = double.MaxValue, maxZ = double.MinValue;
                            foreach (Curve c in op.BoundaryCurves)
                            {
                                foreach (var pt in new[] { c.GetEndPoint(0), c.Evaluate(0.5, true), c.GetEndPoint(1) })
                                {
                                    double s = ProjectS(pt);
                                    if (s < minS) minS = s;
                                    if (s > maxS) maxS = s;
                                    if (pt.Z < minZ) minZ = pt.Z;
                                    if (pt.Z > maxZ) maxZ = pt.Z;
                                }
                            }
                            if (maxS > minS && maxZ > minZ)
                            {
                                AddCandidate(minS, maxS, minZ, maxZ);
                                continue;
                            }
                        }
                    }

                    // Para FamilyInstance (Puertas / Ventanas) u otros insertos: proyectar su BoundingBox o geometría
                    var bb = elem.get_BoundingBox(null);
                    if (bb != null)
                    {
                        var corners = new[]
                        {
                            new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                            new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                            new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                            new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
                        };
                        double sMin = corners.Min(ProjectS);
                        double sMax = corners.Max(ProjectS);
                        AddCandidate(sMin, sMax, bb.Min.Z, bb.Max.Z);
                    }
                }
            }
        }
        catch { }

        // 2. Vía Sólido 3D del muro (detecta bucles internos y escotes/recortes por Edit Profile)
        try
        {
            var opt = new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };
            var geomElem = wall.get_Geometry(opt);
            if (geomElem != null)
            {
                PlanarFace? mainFace = null;
                double maxFaceArea = 0.0;

                foreach (var geomObj in geomElem)
                {
                    if (geomObj is Solid solid && solid.Volume > 0.001)
                    {
                        foreach (Face f in solid.Faces)
                        {
                            if (f is PlanarFace pf && Math.Abs(pf.FaceNormal.DotProduct(normal)) > 0.85)
                            {
                                if (pf.Area > maxFaceArea)
                                {
                                    maxFaceArea = pf.Area;
                                    mainFace = pf;
                                }
                            }
                        }
                    }
                }

                if (mainFace != null)
                {
                    var loops = mainFace.GetEdgesAsCurveLoops();
                    if (loops != null && loops.Count > 0)
                    {
                        // Identificar el bucle exterior (el de mayor extensión)
                        int outerIdx = 0;
                        double maxLoopAreaProxy = -1.0;
                        var loopBounds = new List<(double sMin, double sMax, double zMin, double zMax)>();

                        for (int i = 0; i < loops.Count; i++)
                        {
                            double lMinS = double.MaxValue, lMaxS = double.MinValue;
                            double lMinZ = double.MaxValue, lMaxZ = double.MinValue;
                            foreach (Curve c in loops[i])
                            {
                                var p0 = c.GetEndPoint(0);
                                var p1 = c.GetEndPoint(1);
                                double s0 = ProjectS(p0), s1 = ProjectS(p1);
                                lMinS = Math.Min(lMinS, Math.Min(s0, s1));
                                lMaxS = Math.Max(lMaxS, Math.Max(s0, s1));
                                lMinZ = Math.Min(lMinZ, Math.Min(p0.Z, p1.Z));
                                lMaxZ = Math.Max(lMaxZ, Math.Max(p0.Z, p1.Z));
                            }
                            double areaProxy = Math.Max(0, lMaxS - lMinS) * Math.Max(0, lMaxZ - lMinZ);
                            loopBounds.Add((lMinS, lMaxS, lMinZ, lMaxZ));
                            if (areaProxy > maxLoopAreaProxy)
                            {
                                maxLoopAreaProxy = areaProxy;
                                outerIdx = i;
                            }
                        }

                        // A. Todos los bucles interiores son vanos cerrados (ventanas/pases)
                        for (int i = 0; i < loops.Count; i++)
                        {
                            if (i == outerIdx) continue;
                            var b = loopBounds[i];
                            AddCandidate(b.sMin, b.sMax, b.zMin, b.zMax);
                        }

                        // B. Analizar contorno exterior del muro y segmentos del bucle
                        var outerLoop = loops[outerIdx];
                        foreach (Curve c in outerLoop)
                        {
                            var p0 = c.GetEndPoint(0);
                            var p1 = c.GetEndPoint(1);
                            double s0 = ProjectS(p0);
                            double s1 = ProjectS(p1);
                            profile.BoundarySegments.Add((s0, p0.Z, s1, p1.Z));
                            if (Math.Abs(s1 - s0) > 0.3 && Math.Abs(p1.Z - p0.Z) > 0.3)
                            {
                                profile.IsInclined = true;
                            }
                        }

                        if (profile.IsInclined)
                        {
                            profile.StartHeightFeet = profile.GetTopElevationAt(0.1, zMax) - zMin;
                            profile.EndHeightFeet = profile.GetTopElevationAt(len2D - 0.1, zMax) - zMin;
                            profile.Slope = (profile.EndHeightFeet - profile.StartHeightFeet) / Math.Max(0.1, len2D);
                        }
                        else
                        {
                            // Detectar escotes/vanos rectangulares de puertas en muros rectangulares
                            foreach (Curve c in outerLoop)
                            {
                                var p0 = c.GetEndPoint(0);
                                var p1 = c.GetEndPoint(1);
                                if (Math.Abs(p0.Z - p1.Z) < 0.05)
                                {
                                    double zSeg = (p0.Z + p1.Z) * 0.5;
                                    if (zSeg > zMin + 0.25 && zSeg < zMax - 0.25)
                                    {
                                        double sA = Math.Min(ProjectS(p0), ProjectS(p1));
                                        double sB = Math.Max(ProjectS(p0), ProjectS(p1));
                                        if ((sB - sA) > 0.25)
                                        {
                                            double sMid = (sA + sB) * 0.5;
                                            var faceOrigin = mainFace.Origin;
                                            double tFace = (faceOrigin - pStart).DotProduct(normal);

                                            double zTestBelow = (zMin + zSeg) * 0.5;
                                            var ptBelow = new XYZ(
                                                pStart.X + dir.X * sMid + normal.X * tFace,
                                                pStart.Y + dir.Y * sMid + normal.Y * tFace,
                                                zTestBelow);
                                            var projBelow = mainFace.Project(ptBelow);
                                            if (projBelow == null || !mainFace.IsInside(projBelow.UVPoint))
                                            {
                                                AddCandidate(sA, sB, zMin, zSeg);
                                            }
                                            else
                                            {
                                                double zTestAbove = (zSeg + zMax) * 0.5;
                                                var ptAbove = new XYZ(
                                                    pStart.X + dir.X * sMid + normal.X * tFace,
                                                    pStart.Y + dir.Y * sMid + normal.Y * tFace,
                                                    zTestAbove);
                                                var projAbove = mainFace.Project(ptAbove);
                                                if (projAbove == null || !mainFace.IsInside(projAbove.UVPoint))
                                                {
                                                    AddCandidate(sA, sB, zSeg, zMax);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // 3. Fusionar/desduplicar aberturas solapadas (ej. detectadas simultáneamente por FindInserts y PlanarFace)
        var merged = new List<WallOpeningInfo>();
        foreach (var cand in rawOpenings.OrderBy(o => o.SMinFeet).ThenBy(o => o.ZMinFeet))
        {
            var existing = merged.FirstOrDefault(m =>
                Math.Max(m.SMinFeet, cand.SMinFeet) < Math.Min(m.SMaxFeet, cand.SMaxFeet) - 0.05 &&
                Math.Max(m.ZMinFeet, cand.ZMinFeet) < Math.Min(m.ZMaxFeet, cand.ZMaxFeet) - 0.05);

            if (existing != null)
            {
                existing.SMinFeet = Math.Min(existing.SMinFeet, cand.SMinFeet);
                existing.SMaxFeet = Math.Max(existing.SMaxFeet, cand.SMaxFeet);
                existing.ZMinFeet = Math.Min(existing.ZMinFeet, cand.ZMinFeet);
                existing.ZMaxFeet = Math.Max(existing.ZMaxFeet, cand.ZMaxFeet);
            }
            else
            {
                merged.Add(cand);
            }
        }

        return (merged.OrderBy(o => o.SMinFeet).ThenBy(o => o.ZMinFeet).ToList(), profile);
    }

    private static bool TryGetParameterZBounds(Document doc, Wall wall, double fallbackLineZ, out double zMin, out double zMax)
    {
        zMin = 0.0;
        zMax = 10.0;

        try
        {
            double baseElevation = fallbackLineZ;
            if (wall.LevelId != ElementId.InvalidElementId)
            {
                if (doc.GetElement(wall.LevelId) is Level lvl)
                {
                    baseElevation = lvl.Elevation;
                }
            }

            double baseOffset = 0.0;
            var pBaseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
            if (pBaseOffset != null && pBaseOffset.HasValue)
            {
                baseOffset = pBaseOffset.AsDouble();
            }

            zMin = baseElevation + baseOffset;

            // Nivel superior o altura desconectada
            var pTopLevel = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
            if (pTopLevel != null && pTopLevel.HasValue && pTopLevel.AsElementId() != ElementId.InvalidElementId)
            {
                if (doc.GetElement(pTopLevel.AsElementId()) is Level topLvl)
                {
                    double topElevation = topLvl.Elevation;
                    double topOffset = 0.0;
                    var pTopOffset = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                    if (pTopOffset != null && pTopOffset.HasValue)
                    {
                        topOffset = pTopOffset.AsDouble();
                    }
                    zMax = topElevation + topOffset;
                    return (zMax - zMin) > 0.1;
                }
            }

            var pUnconnected = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (pUnconnected != null && pUnconnected.HasValue)
            {
                zMax = zMin + pUnconnected.AsDouble();
                return (zMax - zMin) > 0.1;
            }

            zMax = zMin + 9.8425; // ~3m
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetGeometryZBounds(Wall wall, out double zMin, out double zMax)
    {
        zMin = double.MaxValue;
        zMax = double.MinValue;

        try
        {
            // 1. Intentar con los sólidos reales del muro
            var opt = new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };
            var geomElem = wall.get_Geometry(opt);
            if (geomElem != null)
            {
                double solidMinZ = double.MaxValue;
                double solidMaxZ = double.MinValue;
                bool foundSolid = false;

                CollectSolidZ(geomElem, ref solidMinZ, ref solidMaxZ, ref foundSolid);

                if (foundSolid && (solidMaxZ - solidMinZ) > 0.1)
                {
                    zMin = solidMinZ;
                    zMax = solidMaxZ;
                    return true;
                }
            }

            // 2. Fallback a BoundingBoxXYZ
            var bb = wall.get_BoundingBox(null);
            if (bb != null && (bb.Max.Z - bb.Min.Z) > 0.1)
            {
                zMin = bb.Min.Z;
                zMax = bb.Max.Z;
                return true;
            }
        }
        catch
        {
            // Silencioso
        }

        return false;
    }

    private static void CollectSolidZ(GeometryElement geomElem, ref double minZ, ref double maxZ, ref bool found)
    {
        foreach (var geomObj in geomElem)
        {
            if (geomObj is Solid solid && solid.Volume > 0.0001)
            {
                foreach (Edge edge in solid.Edges)
                {
                    var c = edge.AsCurve();
                    if (c != null)
                    {
                        var p0 = c.GetEndPoint(0);
                        var p1 = c.GetEndPoint(1);
                        if (p0.Z < minZ) minZ = p0.Z;
                        if (p0.Z > maxZ) maxZ = p0.Z;
                        if (p1.Z < minZ) minZ = p1.Z;
                        if (p1.Z > maxZ) maxZ = p1.Z;
                        found = true;
                    }
                }
            }
            else if (geomObj is GeometryInstance inst)
            {
                var instGeom = inst.GetInstanceGeometry();
                if (instGeom != null)
                {
                    CollectSolidZ(instGeom, ref minZ, ref maxZ, ref found);
                }
            }
        }
    }

    public static (double CoverFeet, string CoverTypeName) ExtractConcreteCover(Document doc, Wall wall)
    {
        try
        {
            BuiltInParameter[] coverParams =
            [
                BuiltInParameter.CLEAR_COVER_EXTERIOR,
                BuiltInParameter.CLEAR_COVER_INTERIOR,
                BuiltInParameter.CLEAR_COVER_OTHER
            ];

            foreach (var bip in coverParams)
            {
                var param = wall.get_Parameter(bip);
                if (param != null && param.HasValue && param.AsElementId() != ElementId.InvalidElementId)
                {
                    if (doc.GetElement(param.AsElementId()) is RebarCoverType coverType)
                    {
                        if (coverType.CoverDistance > 0.005)
                        {
                            return (coverType.CoverDistance, coverType.Name);
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback
        }

        return (0.08202, "Estándar (2.5 cm)");
    }

    /// <summary>
    /// Determina rápidamente si un muro posee un perfil editado con aristas inclinadas (típico de muros de escalera).
    /// </summary>
    public static bool IsInclinedProfileWall(Document doc, Wall wall)
    {
        try
        {
            var geom = ExtractGeometry(doc, wall);
            return geom.Profile != null && geom.Profile.IsInclined;
        }
        catch { return false; }
    }
}
