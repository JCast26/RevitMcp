using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Información de encuentro en esquina para los extremos de un muro.
/// </summary>
public class WallCornerInfo
{
    public bool HasStartCorner { get; set; }
    public double StartExtensionFeet { get; set; }
    public double StartSetbackFeet { get; set; }
    public XYZ? StartContinuingDir { get; set; }

    public bool HasEndCorner { get; set; }
    public double EndExtensionFeet { get; set; }
    public double EndSetbackFeet { get; set; }
    public XYZ? EndContinuingDir { get; set; }
}

/// <summary>
/// Analiza las esquinas y uniones en L / T entre muros seleccionados
/// para orientar los ganchos de refuerzo horizontal y prolongar las barras en los vértices.
/// </summary>
public static class WallCornerDetector
{
    public static Dictionary<long, WallCornerInfo> AnalyzeCorners(Document doc, IList<Wall> walls)
    {
        var result = new Dictionary<long, WallCornerInfo>();
        if (walls == null || walls.Count == 0) return result;

        var wallData = new List<(long Id, Wall Wall, XYZ P0, XYZ P1, XYZ Dir, double Thickness, double Cover)>();
        foreach (var w in walls)
        {
            if (w.Location is LocationCurve lc && lc.Curve is Line line)
            {
                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);
                var dir2D = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0.0);
                if (dir2D.GetLength() < 0.01) continue;

                var (coverFeet, _) = WallGeometryHelper.ExtractConcreteCover(doc, w);
                wallData.Add((
                    w.Id.Value,
                    w,
                    new XYZ(p0.X, p0.Y, 0.0),
                    new XYZ(p1.X, p1.Y, 0.0),
                    dir2D.Normalize(),
                    w.Width,
                    coverFeet
                ));
            }
        }

        foreach (var w in wallData)
        {
            var info = new WallCornerInfo();

            // 1. Evaluar extremo Start (P0)
            foreach (var other in wallData)
            {
                if (other.Id == w.Id) continue;

                double d0 = w.P0.DistanceTo(other.P0);
                double d1 = w.P0.DistanceTo(other.P1);
                double maxDist = Math.Max(w.Thickness, other.Thickness) * 1.5;

                if (d0 < maxDist || d1 < maxDist)
                {
                    XYZ vAdj = (d0 < d1) ? other.Dir : (-other.Dir);

                    double dot = Math.Abs(w.Dir.DotProduct(vAdj));
                    if (dot < 0.85) // Unión no colineal
                    {
                        double sinTheta = Math.Max(0.3, Math.Sqrt(Math.Max(0.0, 1.0 - dot * dot)));
                        double dFace = (other.Thickness / 2.0) / sinTheta;

                        info.HasStartCorner = true;
                        info.StartExtensionFeet = Math.Max(0.15, (other.Thickness / 2.0) - other.Cover);
                        info.StartSetbackFeet = Math.Max(w.Cover + 0.05, dFace + w.Cover);
                        info.StartContinuingDir = vAdj;
                        break;
                    }
                }
            }

            // 2. Evaluar extremo End (P1)
            foreach (var other in wallData)
            {
                if (other.Id == w.Id) continue;

                double d0 = w.P1.DistanceTo(other.P0);
                double d1 = w.P1.DistanceTo(other.P1);
                double maxDist = Math.Max(w.Thickness, other.Thickness) * 1.5;

                if (d0 < maxDist || d1 < maxDist)
                {
                    XYZ vAdj = (d0 < d1) ? other.Dir : (-other.Dir);

                    double dot = Math.Abs(w.Dir.DotProduct(vAdj));
                    if (dot < 0.85)
                    {
                        double sinTheta = Math.Max(0.3, Math.Sqrt(Math.Max(0.0, 1.0 - dot * dot)));
                        double dFace = (other.Thickness / 2.0) / sinTheta;

                        info.HasEndCorner = true;
                        info.EndExtensionFeet = Math.Max(0.15, (other.Thickness / 2.0) - other.Cover);
                        info.EndSetbackFeet = Math.Max(w.Cover + 0.05, dFace + w.Cover);
                        info.EndContinuingDir = vAdj;
                        break;
                    }
                }
            }

            result[w.Id] = info;
        }

        return result;
    }
}
