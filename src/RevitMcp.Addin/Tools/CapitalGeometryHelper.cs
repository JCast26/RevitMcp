using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

public sealed class CapitalGeometryInfo
{
    public XYZ CenterXY { get; init; } = XYZ.Zero;
    public XYZ DirX { get; init; } = XYZ.BasisX;
    public XYZ DirY { get; init; } = XYZ.BasisY;
    public double SizeXFeet { get; init; }
    public double SizeYFeet { get; init; }
    public double ZMinFeet { get; init; }
    public double ZMaxFeet { get; init; }
    public double HeightFeet => Math.Max(0.0, ZMaxFeet - ZMinFeet);

    public double CoverSidesFeet { get; init; }
    public double CoverTopFeet { get; init; }
    public double CoverBottomFeet { get; init; }
    public string CoverTypeName { get; init; } = string.Empty;
}

public static class CapitalGeometryHelper
{
    public static CapitalGeometryInfo? ExtractGeometry(Document doc, FamilyInstance column)
    {
        var solids = new List<Solid>();
        var opts = new Options
        {
            ComputeReferences = false,
            DetailLevel = ViewDetailLevel.Fine,
            IncludeNonVisibleObjects = false
        };

        CollectSolids(column.get_Geometry(opts), solids);
        if (solids.Count == 0)
        {
            opts.DetailLevel = ViewDetailLevel.Medium;
            CollectSolids(column.get_Geometry(opts), solids);
        }

        XYZ dirX = ResolveHorizontalDirX(column, solids);
        XYZ dirY = XYZ.BasisZ.CrossProduct(dirX).Normalize();

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        int pointCount = 0;

        foreach (var solid in solids)
        {
            foreach (Edge edge in solid.Edges)
            {
                var pts = edge.Tessellate();
                if (pts == null) continue;
                foreach (var pt in pts)
                {
                    AccumulatePoint(pt, dirX, dirY, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
                    pointCount++;
                }
            }

            if (pointCount == 0)
            {
                foreach (Face face in solid.Faces)
                {
                    var mesh = face.Triangulate();
                    if (mesh == null) continue;
                    foreach (var pt in mesh.Vertices)
                    {
                        AccumulatePoint(pt, dirX, dirY, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
                        pointCount++;
                    }
                }
            }
        }

        if (pointCount == 0)
        {
            var bbox = column.get_BoundingBox(null);
            if (bbox == null) return null;

            XYZ[] corners =
            [
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z)
            ];

            foreach (var pt in corners)
            {
                AccumulatePoint(pt, dirX, dirY, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
            }
        }

        double sizeX = maxX - minX;
        double sizeY = maxY - minY;
        if (sizeX <= 1e-4 || sizeY <= 1e-4 || maxZ <= minZ + 1e-4)
            return null;

        double midX = 0.5 * (minX + maxX);
        double midY = 0.5 * (minY + maxY);
        XYZ centerXY = dirX * midX + dirY * midY;

        var (coverSides, coverTop, coverBottom, coverName) = ExtractConcreteCover(doc, column);

        return new CapitalGeometryInfo
        {
            CenterXY = new XYZ(centerXY.X, centerXY.Y, 0.0),
            DirX = dirX,
            DirY = dirY,
            SizeXFeet = sizeX,
            SizeYFeet = sizeY,
            ZMinFeet = minZ,
            ZMaxFeet = maxZ,
            CoverSidesFeet = coverSides,
            CoverTopFeet = coverTop,
            CoverBottomFeet = coverBottom,
            CoverTypeName = coverName
        };
    }

    public static (double sidesFeet, double topFeet, double bottomFeet, string typeName) ExtractConcreteCover(
        Document doc, Element element)
    {
        double fallback = UnitConverter.MetersToFeet(0.04);

        var (otherCov, otherName) = TryReadCoverParam(doc, element, BuiltInParameter.CLEAR_COVER_OTHER);
        var (extCov, extName) = TryReadCoverParam(doc, element, BuiltInParameter.CLEAR_COVER_EXTERIOR);
        var (intCov, intName) = TryReadCoverParam(doc, element, BuiltInParameter.CLEAR_COVER_INTERIOR);
        var (topCov, topName) = TryReadCoverParam(doc, element, BuiltInParameter.CLEAR_COVER_TOP);
        var (botCov, botName) = TryReadCoverParam(doc, element, BuiltInParameter.CLEAR_COVER_BOTTOM);

        double sides = otherCov ?? extCov ?? intCov ?? fallback;
        double top = topCov ?? otherCov ?? extCov ?? sides;
        double bottom = botCov ?? otherCov ?? extCov ?? sides;

        string name = otherName
            ?? extName
            ?? intName
            ?? topName
            ?? botName
            ?? "Por defecto (4.0 cm)";

        return (sides, top, bottom, name);
    }

    private static (double? coverFeet, string? name) TryReadCoverParam(
        Document doc, Element element, BuiltInParameter bip)
    {
        var param = element.get_Parameter(bip);
        if (param == null || !param.HasValue || param.StorageType != StorageType.ElementId)
            return (null, null);

        var id = param.AsElementId();
        if (id == ElementId.InvalidElementId)
            return (null, null);

        if (doc.GetElement(id) is RebarCoverType coverType)
            return (coverType.CoverDistance, coverType.Name);

        return (null, null);
    }

    private static void CollectSolids(GeometryElement? geomElem, List<Solid> solids)
    {
        if (geomElem == null) return;

        foreach (GeometryObject obj in geomElem)
        {
            if (obj is Solid solid)
            {
                if (solid.Volume > 1e-6 && solid.Faces.Size > 0)
                    solids.Add(solid);
            }
            else if (obj is GeometryInstance inst)
            {
                CollectSolids(inst.GetInstanceGeometry(), solids);
            }
        }
    }

    private static XYZ ResolveHorizontalDirX(FamilyInstance column, List<Solid> solids)
    {
        var transform = column.GetTransform();
        var txRaw = new XYZ(transform.BasisX.X, transform.BasisX.Y, 0.0);
        XYZ tx = txRaw.GetLength() > 1e-6 ? txRaw.Normalize() : XYZ.BasisX;

        XYZ? faceAxis = null;
        double bestArea = 0.0;

        foreach (var solid in solids)
        {
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace pf) continue;
                if (Math.Abs(pf.FaceNormal.Z) > 0.05) continue;

                var hNorm = new XYZ(pf.FaceNormal.X, pf.FaceNormal.Y, 0.0);
                if (hNorm.GetLength() <= 1e-6) continue;

                if (pf.Area > bestArea)
                {
                    bestArea = pf.Area;
                    faceAxis = hNorm.Normalize();
                }
            }
        }

        if (faceAxis == null)
        {
            foreach (var solid in solids)
            {
                foreach (Face face in solid.Faces)
                {
                    if (face is not PlanarFace pf) continue;
                    var hNorm = new XYZ(pf.FaceNormal.X, pf.FaceNormal.Y, 0.0);
                    if (hNorm.GetLength() <= 0.1) continue;

                    if (pf.Area > bestArea)
                    {
                        bestArea = pf.Area;
                        faceAxis = hNorm.Normalize();
                    }
                }
            }
        }

        if (faceAxis == null)
            return tx;

        XYZ axis1 = faceAxis;
        XYZ axis2 = XYZ.BasisZ.CrossProduct(axis1).Normalize();

        XYZ[] candidates = [axis1, -axis1, axis2, -axis2];
        XYZ bestDir = axis1;
        double bestDot = double.MinValue;
        foreach (var c in candidates)
        {
            double dot = c.DotProduct(tx);
            if (dot > bestDot)
            {
                bestDot = dot;
                bestDir = c;
            }
        }

        return bestDir;
    }

    private static void AccumulatePoint(
        XYZ pt, XYZ dirX, XYZ dirY,
        ref double minX, ref double maxX,
        ref double minY, ref double maxY,
        ref double minZ, ref double maxZ)
    {
        double px = pt.DotProduct(dirX);
        double py = pt.DotProduct(dirY);
        double pz = pt.Z;

        if (px < minX) minX = px;
        if (px > maxX) maxX = px;
        if (py < minY) minY = py;
        if (py > maxY) maxY = py;
        if (pz < minZ) minZ = pz;
        if (pz > maxZ) maxZ = pz;
    }
}
