using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Motor de modelado automático para armadura de sardineles y muretes bajos de concreto armado.
/// Genera estribos transversales (abiertos en C a 180° o cerrados a 135°) y refuerzo longitudinal
/// modelado bajo la regla de diseño nativa de Número Fijo (Fixed Number Rebar Set).
/// </summary>
public static class CreateCurbRebarHandler
{
    public static CreateCurbRebarResponse Execute(Document doc, CreateCurbRebarRequest request)
    {
        var response = new CreateCurbRebarResponse();

        if (doc == null || request == null || request.WallElementIds.Count == 0)
        {
            response.Success = false;
            response.Message = "Solicitud inválida o sin sardineles seleccionados.";
            return response;
        }

        // 1. Resolver tipos de barras
        var stirrupBarType = ResolveBarType(doc, request.StirrupBarTypeName, "1/4\"") 
                          ?? ResolveBarType(doc, request.StirrupBarTypeName, "3/8\"");
        var longBarType = ResolveBarType(doc, request.LongitudinalBarTypeName, "3/8\"")
                       ?? ResolveBarType(doc, request.LongitudinalBarTypeName, "1/2\"");

        if (stirrupBarType == null || longBarType == null)
        {
            response.Success = false;
            response.Message = "No se pudieron resolver los tipos de barra de armadura en el proyecto.";
            return response;
        }

        // 2. Resolver ganchos: 180° (estilo Estándar, para la barra recta en C) y 135° (estribo cerrado sísmico)
        var allHooks = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .ToList();

        var hook180 = allHooks.FirstOrDefault(h => h.Style == RebarStyle.Standard && Math.Abs(h.HookAngle - Math.PI) < 0.1)
                   ?? allHooks.FirstOrDefault(h => h.Style == RebarStyle.Standard && h.Name.Contains("180"))
                   ?? allHooks.FirstOrDefault(h => Math.Abs(h.HookAngle - Math.PI) < 0.1 || h.Name.Contains("180"));

        var hook135 = allHooks.FirstOrDefault(h => (h.Name.Contains("135") || Math.Abs(h.HookAngle - 2.356) < 0.1) && h.Style == RebarStyle.StirrupTie)
                   ?? allHooks.FirstOrDefault(h => h.Name.Contains("135") || Math.Abs(h.HookAngle - 2.356) < 0.1)
                   ?? allHooks.FirstOrDefault();

        // Gancho de esquina seleccionado por el usuario (estilo Estándar, aplicado a las barras longitudinales)
        RebarHookType? cornerHook = null;
        if (request.ExtendAtCorners && !string.IsNullOrWhiteSpace(request.CornerHookTypeName))
        {
            cornerHook = allHooks.FirstOrDefault(h => h.Style == RebarStyle.Standard && h.Name.Equals(request.CornerHookTypeName, StringComparison.OrdinalIgnoreCase))
                      ?? allHooks.FirstOrDefault(h => h.Name.Equals(request.CornerHookTypeName, StringComparison.OrdinalIgnoreCase));
        }

        // Forma de estribo sísmico para estribo cerrado
        var seismicStirrupShape = ResolveSeismicStirrupShape(doc);

        // Conversión de unidades (m a ft)
        double stirrupSpacingFeet = UnitConverter.MetersToFeet(Math.Max(0.05, request.StirrupSpacingMeters));
        double manualCoverFeet = UnitConverter.MetersToFeet(Math.Max(0.01, request.CoverMeters));
        double startOffsetFeet = UnitConverter.MetersToFeet(Math.Max(0.02, request.StartOffsetMeters));
        double endOffsetFeet = UnitConverter.MetersToFeet(Math.Max(0.02, request.EndOffsetMeters));

        bool isOpenC = request.StirrupType == CurbStirrupType.OpenC;
        // Estribo en C: 2 o 3 barras en una sola línea. Estribo cerrado: 4 o 6 barras (2 o 3 por cara, tipo viga)
        int longBarCount = isOpenC
            ? Math.Clamp(request.LongitudinalBarCount, 2, 3)
            : (request.LongitudinalBarCount >= 6 ? 6 : 4);

        var view3D = doc.ActiveView as View3D;

        using var trans = new Transaction(doc, "BIM Suite - Armado de Sardineles");
        var failOptions = trans.GetFailureHandlingOptions();
        failOptions.SetFailuresPreprocessor(new RebarFailuresPreprocessor());
        trans.SetFailureHandlingOptions(failOptions);

        var transStatus = trans.Start();
        if (transStatus != TransactionStatus.Started)
        {
            response.Success = false;
            response.Message = "No se pudo iniciar la transacción en el modelo de Revit.";
            return response;
        }

        // Analizar esquinas y uniones entre los sardineles seleccionados
        var allSelectedWalls = request.WallElementIds
            .Select(id => doc.GetElement(new ElementId(id)) as Wall)
            .Where(w => w != null)
            .Cast<Wall>()
            .ToList();

        var cornerMap = WallCornerDetector.AnalyzeCorners(doc, allSelectedWalls);

        try
        {
            foreach (var wallIdVal in request.WallElementIds)
            {
                var summary = new CurbRebarCreatedSummary
                {
                    WallElementId = wallIdVal
                };

                var wall = doc.GetElement(new ElementId(wallIdVal)) as Wall;
                if (wall == null)
                {
                    summary.Status = "Failed";
                    summary.ErrorMessage = "El elemento no existe o no es un muro/sardinel válido.";
                    response.Results.Add(summary);
                    continue;
                }

                try
                {
                    EnsureWallIsStructural(wall);

                    WallGeometryInfo geom = WallGeometryHelper.ExtractGeometry(doc, wall);
                    double wallLen = geom.LengthFeet;
                    double wallThick = geom.ThicknessFeet;
                    double zMin = geom.ZMinFeet;
                    double zMax = geom.ZMaxFeet;
                    double wallHeight = zMax - zMin;

                    if (wallLen < 0.25 || wallHeight < 0.15)
                    {
                        summary.Status = "Failed";
                        summary.ErrorMessage = $"Dimensiones insuficientes (L={geom.LengthMeters:0.##}m, H={geom.HeightMeters:0.##}m) para alojar armadura.";
                        response.Results.Add(summary);
                        continue;
                    }

                    // Recubrimiento efectivo
                    double requestedCoverFeet = request.UseWallCover ? geom.CoverFeet : manualCoverFeet;
                    double effectiveCoverFeet = Math.Min(requestedCoverFeet, (wallThick / 2.0) * 0.40);
                    if (effectiveCoverFeet < 0.015) effectiveCoverFeet = 0.015;

                    var pOriginXY = new XYZ(geom.StartPoint.X, geom.StartPoint.Y, 0.0);
                    var vDir = geom.Direction; // Unitario longitudinal
                    var nVec = geom.Normal;    // Unitario transversal horizontal

                    XYZ PointAt(double s, double t, double z) =>
                        new XYZ(pOriginXY.X + vDir.X * s + nVec.X * t,
                                pOriginXY.Y + vDir.Y * s + nVec.Y * t,
                                z);

                    bool isRightHandedZ = vDir.CrossProduct(nVec).DotProduct(XYZ.BasisZ) >= 0.0;

                    var cornerInfo = cornerMap.TryGetValue(wallIdVal, out var ci) ? ci : new WallCornerInfo();

                    double sStart = cornerInfo.HasStartCorner ? cornerInfo.StartSetbackFeet : startOffsetFeet;
                    double sEnd = cornerInfo.HasEndCorner ? (wallLen - cornerInfo.EndSetbackFeet) : (wallLen - endOffsetFeet);
                    if (sEnd <= sStart + 0.15)
                    {
                        sStart = Math.Min(startOffsetFeet, wallLen * 0.1);
                        sEnd = Math.Max(wallLen - endOffsetFeet, wallLen * 0.9);
                    }
                    double stirrupArrayLength = Math.Max(0.2, sEnd - sStart);

                    double dStir = GetBarDiameter(stirrupBarType);
                    double rStir = dStir / 2.0;
                    double dLong = GetBarDiameter(longBarType);
                    double rLong = dLong / 2.0;

                    // Dimensiones del estribo en sección transversal
                    double halfT = wallThick / 2.0;
                    double t0 = -halfT + effectiveCoverFeet + rStir;
                    double t1 = halfT - effectiveCoverFeet - rStir;
                    double z0 = zMin + effectiveCoverFeet + rStir;
                    double z1 = zMax - effectiveCoverFeet - rStir;

                    double stirWidth = Math.Max(0.10, t1 - t0);
                    double stirHeight = Math.Max(0.12, z1 - z0);

                    // =========================================================
                    // 1. MODELADO DE ESTRIBOS TRANSVERSALES
                    // =========================================================
                    Rebar? stirrupRebar = null;

                    // Posición de las barras longitudinales para el estribo en C (definida por la geometría del gancho)
                    double cBarT = 0.0;
                    double cBarZBot = z0 + rStir + rLong;
                    double cBarZTop = z1 - rStir - rLong;

                    if (isOpenC)
                    {
                        // Estribo en "C": barra recta vertical con ganchos estándar a 180° en ambos extremos.
                        // Ambos ganchos se orientan hacia el mismo lado (+nVec) para abrazar las barras longitudinales.
                        double rBend = GetHookBendRadius(stirrupBarType, dStir);

                        // Centrar el conjunto (barra + doblez a 180°) en el espesor: el centro del doblez queda en t = 0
                        double tLine = -(rBend + rStir);
                        tLine = Math.Max(tLine, -halfT + effectiveCoverFeet + rStir);

                        // El doblez sobresale (rBend + 2·rStir) por encima/debajo del extremo de la barra recta
                        double zLineBot = zMin + effectiveCoverFeet + rBend + 2.0 * rStir;
                        double zLineTop = zMax - effectiveCoverFeet - rBend - 2.0 * rStir;
                        if (zLineTop - zLineBot < 0.10)
                        {
                            double zMid = (zMin + zMax) / 2.0;
                            zLineBot = zMid - 0.05;
                            zLineTop = zMid + 0.05;
                        }

                        var pBot = PointAt(sStart, tLine, zLineBot);
                        var pTop = PointAt(sStart, tLine, zLineTop);

                        // Orientación del gancho: con normal = vDir y tangente = +Z, 'Left' apunta a (vDir × Z)
                        XYZ hookLeftDir = vDir.CrossProduct(XYZ.BasisZ);
                        var cHookOrient = hookLeftDir.DotProduct(nVec) >= 0 ? RebarHookOrientation.Left : RebarHookOrientation.Right;

                        stirrupRebar = CreateRebarSafe(
                            doc,
                            RebarStyle.Standard,
                            stirrupBarType,
                            hook180,
                            hook180,
                            wall,
                            vDir,
                            new List<Curve> { Line.CreateBound(pBot, pTop) },
                            cHookOrient,
                            cHookOrient);

                        // Barras longitudinales centradas en el eje del gancho (t = 0) y alojadas DENTRO de los ganchos a 180°
                        double tHookCenter = tLine + rBend + rStir;
                        cBarT = Math.Abs(tHookCenter) < 0.005 ? 0.0 : tHookCenter;

                        // Desfase vertical hacia el INTERIOR del estribo para alojarse dentro de la curvatura del gancho:
                        // En la parte superior: bajamos (-Z) para entrar en el bucle superior.
                        // En la parte inferior: subimos (+Z) para entrar en el bucle inferior.
                        double vertNestOffset = 2.0 * rStir + rLong;
                        cBarZBot = zLineBot + vertNestOffset;
                        cBarZTop = zLineTop - vertNestOffset;
                        if (cBarZTop <= cBarZBot + 0.05)
                        {
                            double zMid = (zMin + zMax) / 2.0;
                            cBarZBot = zMid - 0.04;
                            cBarZTop = zMid + 0.04;
                        }
                    }
                    else
                    {
                        // Estribo cerrado rectangular con ganchos sísmicos a 135° (muros con ancho suficiente)
                        double halfH = stirHeight / 2.0;
                        double halfD = stirWidth / 2.0;
                        double zCenter = (z0 + z1) / 2.0;

                        XYZ origin;
                        XYZ xVec;
                        XYZ yVec;
                        XYZ boxVecX;
                        XYZ boxVecY;

                        if (isRightHandedZ)
                        {
                            origin = PointAt(sStart, -halfD, zCenter - halfH);
                            xVec = nVec;
                            yVec = XYZ.BasisZ;
                            boxVecX = xVec * stirWidth;
                            boxVecY = yVec * stirHeight;
                        }
                        else
                        {
                            origin = PointAt(sStart, +halfD, zCenter - halfH);
                            xVec = -nVec;
                            yVec = XYZ.BasisZ;
                            boxVecX = xVec * stirWidth;
                            boxVecY = yVec * stirHeight;
                        }

                        if (seismicStirrupShape != null)
                        {
                            try
                            {
                                stirrupRebar = Rebar.CreateFromRebarShape(doc, seismicStirrupShape, stirrupBarType, wall, origin, xVec, yVec);
                                if (stirrupRebar != null)
                                {
                                    ApplyStirrupDimensions(stirrupRebar, origin, boxVecX, boxVecY, stirWidth, stirHeight);
                                }
                            }
                            catch { }
                        }

                        if (stirrupRebar == null)
                        {
                            // Fallback con 4 curvas cerradas
                            var r1 = PointAt(sStart, -halfD, zCenter - halfH);
                            var r2 = PointAt(sStart, +halfD, zCenter - halfH);
                            var r3 = PointAt(sStart, +halfD, zCenter + halfH);
                            var r4 = PointAt(sStart, -halfD, zCenter + halfH);

                            var rectCurves = new List<Curve>
                            {
                                Line.CreateBound(r1, r2),
                                Line.CreateBound(r2, r3),
                                Line.CreateBound(r3, r4),
                                Line.CreateBound(r4, r1)
                            };

                            stirrupRebar = CreateRebarSafe(
                                doc,
                                RebarStyle.StirrupTie,
                                stirrupBarType,
                                hook135,
                                hook135,
                                wall,
                                vDir,
                                rectCurves);
                        }
                    }

                    if (stirrupRebar != null)
                    {
                        try
                        {
                            var acc = stirrupRebar.GetShapeDrivenAccessor();
                            acc.SetLayoutAsMaximumSpacing(stirrupSpacingFeet, stirrupArrayLength, true, true, true);
                        }
                        catch { }

                        summary.RebarElementIds.Add(stirrupRebar.Id.Value);
                        summary.StirrupsCount = stirrupRebar.Quantity;
                        if (view3D != null) { try { stirrupRebar.SetUnobscuredInView(view3D, true); } catch { } }
                    }

                    // =========================================================
                    // 2. MODELADO DE REFUERZO LONGITUDINAL (REGLA: NÚMERO FIJO)
                    // =========================================================
                    // En esquinas (L/T) la barra se prolonga dentro del sardinel contiguo hasta su cara opuesta
                    double sStartLong = cornerInfo.HasStartCorner ? -cornerInfo.StartExtensionFeet : effectiveCoverFeet;
                    double sEndLong = cornerInfo.HasEndCorner ? (wallLen + cornerInfo.EndExtensionFeet) : (wallLen - effectiveCoverFeet);

                    // Gancho seleccionado por el usuario en los extremos con encuentro, orientado hacia el sardinel adyacente
                    RebarHookType? longStartHook = (cornerHook != null && cornerInfo.HasStartCorner) ? cornerHook : null;
                    RebarHookType? longEndHook = (cornerHook != null && cornerInfo.HasEndCorner) ? cornerHook : null;

                    var startHookOrient = RebarHookOrientation.Left;
                    var endHookOrient = RebarHookOrientation.Left;
                    var vLeft = XYZ.BasisZ.CrossProduct(vDir).Normalize();

                    if (cornerInfo.HasStartCorner && cornerInfo.StartContinuingDir != null)
                    {
                        startHookOrient = vLeft.DotProduct(cornerInfo.StartContinuingDir) >= 0
                            ? RebarHookOrientation.Left : RebarHookOrientation.Right;
                    }
                    if (cornerInfo.HasEndCorner && cornerInfo.EndContinuingDir != null)
                    {
                        endHookOrient = vLeft.DotProduct(cornerInfo.EndContinuingDir) >= 0
                            ? RebarHookOrientation.Left : RebarHookOrientation.Right;
                    }

                    // Disposición de capas:
                    //  - Estribo en C: una sola línea de barras dentro de los ganchos (2 o 3 barras).
                    //  - Estribo cerrado (tipo viga): una capa por cara con barras en las esquinas (2 o 3 por cara).
                    var faceOffsets = new List<double>();
                    int barsPerFace;
                    double zBotBar;
                    double zTopBar;

                    if (isOpenC)
                    {
                        faceOffsets.Add(cBarT);
                        barsPerFace = longBarCount;
                        zBotBar = cBarZBot;
                        zTopBar = cBarZTop;
                    }
                    else
                    {
                        double tFace = Math.Max(0.0, stirWidth / 2.0 - rStir - rLong);
                        faceOffsets.Add(+tFace);
                        faceOffsets.Add(-tFace);
                        barsPerFace = longBarCount / 2;
                        zBotBar = z0 + rStir + rLong;
                        zTopBar = z1 - rStir - rLong;
                    }

                    double longArrayHeight = Math.Max(0.04, zTopBar - zBotBar);

                    foreach (double tLong in faceOffsets)
                    {
                        var p0 = PointAt(sStartLong, tLong, zBotBar);
                        var p1 = PointAt(sEndLong, tLong, zBotBar);

                        var longRebar = CreateRebarSafe(
                            doc,
                            RebarStyle.Standard,
                            longBarType,
                            longStartHook,
                            longEndHook,
                            wall,
                            XYZ.BasisZ,
                            new List<Curve> { Line.CreateBound(p0, p1) },
                            startHookOrient,
                            endHookOrient);

                        if (longRebar != null)
                        {
                            try
                            {
                                var accLong = longRebar.GetShapeDrivenAccessor();
                                accLong.SetLayoutAsFixedNumber(barsPerFace, longArrayHeight, true, true, true);
                            }
                            catch { }

                            summary.RebarElementIds.Add(longRebar.Id.Value);
                            summary.LongitudinalBarsCount += longRebar.Quantity;
                            if (view3D != null) { try { longRebar.SetUnobscuredInView(view3D, true); } catch { } }
                        }
                    }

                    summary.TotalRebarElementsCount = summary.RebarElementIds.Count;
                    summary.Status = summary.TotalRebarElementsCount > 0 ? "Success" : "Failed";
                    response.Results.Add(summary);
                }
                catch (Exception ex)
                {
                    summary.Status = "Failed";
                    summary.ErrorMessage = $"Error en sardinel ID {wallIdVal}: {ex.Message}";
                    response.Results.Add(summary);
                }
            }

            trans.Commit();

            int successCount = response.Results.Count(r => r.Status == "Success");
            response.Success = successCount > 0;
            response.TotalCurbsProcessed = successCount;

            if (response.Success)
            {
                response.Message = $"Se armaron con éxito {successCount} de {response.Results.Count} sardinel(es).";
            }
            else
            {
                var failed = response.Results.Select(r => $"• Sardinel ID {r.WallElementId}: {r.ErrorMessage}");
                response.Message = $"No se pudo completar el armado de sardineles:\n{string.Join("\n", failed)}";
            }

            return response;
        }
        catch (Exception ex)
        {
            try { trans.RollBack(); } catch { }
            response.Success = false;
            response.Message = $"Error durante el modelado de sardineles: {ex.Message}";
            return response;
        }
    }

    private static void EnsureWallIsStructural(Wall wall)
    {
        try
        {
            if (wall.StructuralUsage == StructuralWallUsage.NonBearing)
            {
                wall.StructuralUsage = StructuralWallUsage.Bearing;
            }
        }
        catch { }

        try
        {
            var pUsage = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM);
            if (pUsage != null && !pUsage.IsReadOnly && pUsage.AsInteger() == 0)
            {
                pUsage.Set(1); // 1 = Bearing
            }
        }
        catch { }
    }

    private static RebarBarType? ResolveBarType(Document doc, string? preferredName, string fallbackSearch)
    {
        var barTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .ToList();

        if (barTypes.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var match = barTypes.FirstOrDefault(b => b.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase))
                     ?? barTypes.FirstOrDefault(b => b.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        var fallbackMatch = barTypes.FirstOrDefault(b => b.Name.Contains(fallbackSearch, StringComparison.OrdinalIgnoreCase));
        return fallbackMatch ?? barTypes.FirstOrDefault();
    }

    private static RebarShape? ResolveSeismicStirrupShape(Document doc)
    {
        var shapes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarShape))
            .Cast<RebarShape>()
            .ToList();

        var stirrupShapes = shapes.Where(s => s.RebarStyle == RebarStyle.StirrupTie).ToList();
        var pool = stirrupShapes.Any() ? stirrupShapes : shapes;

        var matchT1 = pool.FirstOrDefault(s => s.Name.Contains("T1", StringComparison.OrdinalIgnoreCase))
                   ?? pool.FirstOrDefault(s => s.Name.Contains("51"))
                   ?? pool.FirstOrDefault(s => s.Name.Contains("Stirrup", StringComparison.OrdinalIgnoreCase) && !s.Name.Contains("00"))
                   ?? pool.FirstOrDefault(s => s.Name.Contains("Estribo", StringComparison.OrdinalIgnoreCase) && !s.Name.Contains("00"))
                   ?? pool.FirstOrDefault(s => s.Name.Contains("Cerco", StringComparison.OrdinalIgnoreCase));

        return matchT1 ?? pool.FirstOrDefault();
    }

    private static double GetBarDiameter(RebarBarType barType)
    {
        try
        {
            var p = barType.LookupParameter("Bar Diameter")
                 ?? barType.LookupParameter("Diámetro de barra");
            if (p != null && p.HasValue) return p.AsDouble();
        }
        catch { }
        return 0.03125; // ~3/8"
    }

    private static double GetHookBendRadius(RebarBarType barType, double barDiameter)
    {
        try
        {
            var p = barType.LookupParameter("Hook Bend Diameter")
                 ?? barType.LookupParameter("Diámetro de curvatura de gancho")
                 ?? barType.LookupParameter("Standard Bend Diameter");
            if (p != null && p.HasValue && p.AsDouble() > 0.005)
            {
                return p.AsDouble() / 2.0;
            }
        }
        catch { }

        return Math.Max(0.02, barDiameter * 2.0);
    }

    private static void ApplyStirrupDimensions(Rebar rebar, XYZ origin, XYZ boxVecX, XYZ boxVecY, double widthFeet, double heightFeet)
    {
        var acc = rebar.GetShapeDrivenAccessor();
        bool scaled = false;
        try
        {
            acc.ScaleToBox(origin, boxVecX, boxVecY);
            scaled = true;
        }
        catch { scaled = false; }

        if (!scaled)
        {
            TrySetRebarParameter(rebar, "A", widthFeet);
            TrySetRebarParameter(rebar, "B", heightFeet);
            TrySetRebarParameter(rebar, "b", widthFeet);
            TrySetRebarParameter(rebar, "h", heightFeet);
        }
    }

    private static void TrySetRebarParameter(Rebar rebar, string paramName, double value)
    {
        try
        {
            var param = rebar.LookupParameter(paramName);
            if (param != null && !param.IsReadOnly && param.StorageType == StorageType.Double)
            {
                param.Set(value);
            }
        }
        catch { }
    }

    private static Rebar? CreateRebarSafe(
        Document doc,
        RebarStyle style,
        RebarBarType barType,
        RebarHookType? startHook,
        RebarHookType? endHook,
        Element host,
        XYZ normal,
        IList<Curve> curves,
        RebarHookOrientation startHookOrient = RebarHookOrientation.Left,
        RebarHookOrientation endHookOrient = RebarHookOrientation.Left)
    {
        try
        {
            return Rebar.CreateFromCurves(
                doc,
                style,
                barType,
                startHook,
                endHook,
                host,
                normal,
                curves,
                startHookOrient,
                endHookOrient,
                true,
                true);
        }
        catch
        {
            try
            {
                return Rebar.CreateFromCurves(
                    doc,
                    style,
                    barType,
                    null,
                    null,
                    host,
                    normal,
                    curves,
                    startHookOrient,
                    endHookOrient,
                    true,
                    true);
            }
            catch { return null; }
        }
    }
}
