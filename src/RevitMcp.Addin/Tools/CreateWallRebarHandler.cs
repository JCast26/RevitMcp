using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Motor de modelado de armaduras de refuerzo en muros estructurales de concreto armado.
/// Soporta configuración de 1 o 2 mallas, refuerzo vertical y horizontal paramétrico,
/// remates con ganchos y elementos de confinamiento de borde (Boundary Elements).
/// Diseñado con tolerancia ante fallos geométricos y prevención estricta de conflictos de forma en Revit.
/// </summary>
public static class CreateWallRebarHandler
{
    public static CreateWallRebarResponse Execute(Document doc, CreateWallRebarRequest request)
    {
        var response = new CreateWallRebarResponse();

        if (request.WallElementIds == null || request.WallElementIds.Count == 0)
        {
            response.Success = false;
            response.Message = "No se especificó ningún muro para armar.";
            return response;
        }

        // 1. Resolver tipos de barras
        var vertBarType = ResolveBarType(doc, request.VerticalBarTypeName, "1/2\"");
        if (vertBarType == null)
        {
            response.Success = false;
            response.Message = $"No se encontró un tipo de barra válido para refuerzo vertical ('{request.VerticalBarTypeName}').";
            return response;
        }

        var horizBarType = ResolveBarType(doc, request.HorizontalBarTypeName, "3/8\"");
        if (horizBarType == null)
        {
            response.Success = false;
            response.Message = $"No se encontró un tipo de barra válido para refuerzo horizontal ('{request.HorizontalBarTypeName}').";
            return response;
        }

        // Gancho horizontal: debe ser estrictamente de estilo Standard para evitar 'The Rebar's hooks do not match its Rebar Shape'
        RebarHookType? horizHookType = null;
        if (request.Typology == WallRebarTypology.EndHooks || !string.IsNullOrWhiteSpace(request.HorizontalHookTypeName))
        {
            horizHookType = ResolveStandardHook(doc, request.HorizontalHookTypeName);
        }

        // Gancho vertical: debe ser estrictamente de estilo Standard
        RebarHookType? vertHookType = null;
        if (!string.IsNullOrWhiteSpace(request.VerticalHookTypeName))
        {
            vertHookType = ResolveStandardHook(doc, request.VerticalHookTypeName);
        }

        RebarBarType? boundaryTieBarType = null;
        RebarShape? boundaryTieShape = null;
        RebarHookType? boundaryHook135 = null;
        if (request.IncludeBoundaryElements || request.Typology == WallRebarTypology.BoundaryElements)
        {
            boundaryTieBarType = ResolveBarType(doc, request.BoundaryTieBarTypeName, "3/8\"");
            boundaryTieShape = ResolveRebarShape(doc, request.BoundaryTieShapeName);

            var allHooks = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarHookType))
                .Cast<RebarHookType>()
                .ToList();

            boundaryHook135 = allHooks.FirstOrDefault(h => (h.Name.Contains("135") || Math.Abs(h.HookAngle - 2.356) < 0.1) && h.Style == RebarStyle.StirrupTie)
                           ?? allHooks.FirstOrDefault(h => h.Name.Contains("135") || Math.Abs(h.HookAngle - 2.356) < 0.1)
                           ?? allHooks.FirstOrDefault(h => h.Style == RebarStyle.StirrupTie)
                           ?? allHooks.FirstOrDefault();
        }

        // Conversión de unidades (Metros a Pies Decimales)
        double sVFeet = UnitConverter.MetersToFeet(Math.Max(0.05, request.VerticalSpacingMeters));
        double sHFeet = UnitConverter.MetersToFeet(Math.Max(0.05, request.HorizontalSpacingMeters));
        double manualCoverFeet = UnitConverter.MetersToFeet(Math.Max(0.01, request.CoverMeters));
        double vTopOffsetFeet = UnitConverter.MetersToFeet(Math.Max(0.01, request.VerticalTopOffsetMeters));
        double vBotOffsetFeet = UnitConverter.MetersToFeet(Math.Max(0.01, request.VerticalBottomOffsetMeters));
        double hStartOffsetFeet = UnitConverter.MetersToFeet(Math.Max(0.01, request.HorizontalStartOffsetMeters));
        double hEndOffsetFeet = UnitConverter.MetersToFeet(Math.Max(0.01, request.HorizontalEndOffsetMeters));
        double boundaryLenFeet = UnitConverter.MetersToFeet(Math.Max(0.20, request.BoundaryLengthMeters));
        double boundaryTieSpacingFeet = UnitConverter.MetersToFeet(Math.Max(0.05, request.BoundaryTieSpacingMeters));

        int numMeshes = Math.Clamp(request.NumberOfMeshes, 1, 2);

        var view3D = doc.ActiveView as View3D;

        using var trans = new Transaction(doc, "BIM Suite - Acero en Muros");
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

        // Analizar esquinas y encuentros en L / T entre los muros seleccionados
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
                var summary = new WallRebarCreatedSummary
                {
                    WallElementId = wallIdVal,
                    MeshesCount = numMeshes
                };

                try
                {
                    var elem = doc.GetElement(new ElementId(wallIdVal));
                    if (elem is not Wall wall)
                    {
                        summary.Status = "Failed";
                        summary.ErrorMessage = $"El elemento ID {wallIdVal} no es un muro (Wall) válido.";
                        response.Results.Add(summary);
                        continue;
                    }

                    summary.WallName = $"{wall.WallType?.FamilyName ?? "Wall"}: {wall.Name}";

                    // Asegurar que el muro esté configurado como estructural en Revit para permitir armadura
                    EnsureWallIsStructural(wall);

                    WallGeometryInfo geom;
                    try
                    {
                        geom = WallGeometryHelper.ExtractGeometry(doc, wall);
                    }
                    catch (Exception ex)
                    {
                        summary.Status = "Failed";
                        summary.ErrorMessage = ex.Message;
                        response.Results.Add(summary);
                        continue;
                    }

                    summary.LengthMeters = geom.LengthMeters;
                    summary.HeightMeters = geom.HeightMeters;
                    summary.ThicknessMeters = geom.ThicknessMeters;

                    double wallLen = geom.LengthFeet;
                    double wallThick = geom.ThicknessFeet;
                    double zMin = geom.ZMinFeet;
                    double zMax = geom.ZMaxFeet;
                    double wallHeight = zMax - zMin;

                    // Verificación de dimensiones físicas mínimas
                    if (wallLen < 0.3)
                    {
                        summary.Status = "Failed";
                        summary.ErrorMessage = $"Longitud insuficiente ({geom.LengthMeters:0.##} m) para alojar armadura.";
                        response.Results.Add(summary);
                        continue;
                    }

                    if (wallHeight < 0.3)
                    {
                        summary.Status = "Failed";
                        summary.ErrorMessage = $"Altura libre insuficiente ({geom.HeightMeters:0.##} m) para alojar armadura.";
                        response.Results.Add(summary);
                        continue;
                    }

                    // Decidir recubrimiento efectivo: del muro o manual, asegurando que no exceda el 45% del semiespesor
                    double requestedCoverFeet = request.UseWallCover ? geom.CoverFeet : manualCoverFeet;
                    double effectiveCoverFeet = Math.Min(requestedCoverFeet, (wallThick / 2.0) * 0.45);
                    if (effectiveCoverFeet < 0.01) effectiveCoverFeet = 0.01;

                    // Clamping defensivo de offsets en extremos y cotas verticales
                    double currentVTop = vTopOffsetFeet;
                    double currentVBot = vBotOffsetFeet;
                    if (wallHeight <= (currentVTop + currentVBot + 0.2))
                    {
                        currentVTop = Math.Min(0.08, wallHeight * 0.1);
                        currentVBot = Math.Min(0.08, wallHeight * 0.1);
                    }

                    double currentHStart = hStartOffsetFeet;
                    double currentHEnd = hEndOffsetFeet;
                    if (wallLen <= (currentHStart + currentHEnd + 0.2))
                    {
                        currentHStart = Math.Min(0.08, wallLen * 0.1);
                        currentHEnd = Math.Min(0.08, wallLen * 0.1);
                    }

                    // Definición estricta de origen y vectores en XY (Z = 0)
                    var pOriginXY = new XYZ(geom.StartPoint.X, geom.StartPoint.Y, 0.0);
                    var vDir = geom.Direction; // Unitario horizontal (Z=0)
                    var nVec = geom.Normal;    // Unitario normal horizontal (Z=0)
                    var zVec = XYZ.BasisZ;

                    // Función auxiliar de coordenadas 3D seguras
                    XYZ PointAt(double s, double t, double z) =>
                        new XYZ(pOriginXY.X + vDir.X * s + nVec.X * t,
                                pOriginXY.Y + vDir.Y * s + nVec.Y * t,
                                z);

                    // Diámetros de barras
                    double dV = GetBarDiameter(vertBarType);
                    double dH = GetBarDiameter(horizBarType);

                    // Capas transversales respecto al eje del muro
                    var layerOffsets = CalculateLayerOffsets(numMeshes, wallThick, effectiveCoverFeet, dV, dH, request.HorizontalBarsOutside);

                    int totalVertBars = 0;
                    int totalHorizBars = 0;

                    // --- ARMADO DE MURO INCLINADO DE ESCALERA (DETECCIÓN AUTOMÁTICA) ---
                    if ((geom.Profile != null && geom.Profile.IsInclined) || request.Typology == WallRebarTypology.InclinedStairWall)
                    {
                        CreateInclinedWallRebar(doc, wall, geom, request, vertBarType, horizBarType, vertHookType, horizHookType, numMeshes, summary, view3D);
                        summary.TotalRebarElementsCount = summary.RebarElementIds.Count;
                        response.Results.Add(summary);
                        continue;
                    }

                    // --- ARMADO CON ELEMENTOS DE BORDE (BOUNDARY ELEMENTS) ---
                    bool hasBoundary = (request.IncludeBoundaryElements || request.Typology == WallRebarTypology.BoundaryElements) && wallLen > (boundaryLenFeet * 2.2);
                    double webStartFeet = hasBoundary ? boundaryLenFeet : currentHStart;
                    double webEndFeet = hasBoundary ? (wallLen - boundaryLenFeet) : (wallLen - currentHEnd);

                    if (hasBoundary && boundaryTieBarType != null)
                    {
                        bool isRightHandedZ = vDir.CrossProduct(nVec).DotProduct(XYZ.BasisZ) >= 0.0;

                        CreateBoundaryTieSet(doc, wall, PointAt,
                            0.0, boundaryLenFeet, wallThick, effectiveCoverFeet,
                            zMin + currentVBot, zMax - currentVTop,
                            vDir, nVec, isRightHandedZ,
                            boundaryTieBarType, boundaryTieShape, boundaryHook135, boundaryTieSpacingFeet, view3D, summary);

                        CreateBoundaryTieSet(doc, wall, PointAt,
                            wallLen - boundaryLenFeet, boundaryLenFeet, wallThick, effectiveCoverFeet,
                            zMin + currentVBot, zMax - currentVTop,
                            vDir, nVec, isRightHandedZ,
                            boundaryTieBarType, boundaryTieShape, boundaryHook135, boundaryTieSpacingFeet, view3D, summary);
                    }

                    // --- GENERAR MALLAS (1 O 2 CORTINAS) ---
                    var cornerInfo = cornerMap.TryGetValue(wallIdVal, out var ci) ? ci : new WallCornerInfo();

                    // Rango de distribución longitudinal para barras verticales y grapas
                    // Si hay esquina en L/T, retrocede a la cara del muro perpendicular + recubrimiento (evita ocupar el vértice)
                    double vStartDist = cornerInfo.HasStartCorner ? cornerInfo.StartSetbackFeet : currentHStart;
                    double vEndDist = cornerInfo.HasEndCorner ? (wallLen - cornerInfo.EndSetbackFeet) : (wallLen - currentHEnd);
                    if (hasBoundary)
                    {
                        vStartDist = Math.Max(vStartDist, webStartFeet);
                        vEndDist = Math.Min(vEndDist, webEndFeet);
                    }
                    double vArrayLength = Math.Max(0.2, vEndDist - vStartDist);

                    // Prolongación automática en esquinas si comparte vértice con otro muro seleccionado
                    double hBarStart = cornerInfo.HasStartCorner ? -cornerInfo.StartExtensionFeet : currentHStart;
                    double hBarEnd = cornerInfo.HasEndCorner ? (wallLen + cornerInfo.EndExtensionFeet) : (wallLen - currentHEnd);

                    // Asignación de ganchos en refuerzo vertical (0: Superior, 1: Ambos, 2: Inferior)
                    RebarHookType? vStartHook = null;
                    RebarHookType? vEndHook = null;
                    if (vertHookType != null)
                    {
                        if (request.VerticalHookPosition == 0) // Superior / Coronación
                        {
                            vEndHook = vertHookType;
                        }
                        else if (request.VerticalHookPosition == 1) // Ambos
                        {
                            vStartHook = vertHookType;
                            vEndHook = vertHookType;
                        }
                        else if (request.VerticalHookPosition == 2) // Inferior / Base
                        {
                            vStartHook = vertHookType;
                        }
                    }

                    // Orientación inteligente de ganchos horizontales hacia el muro adyacente que continúa en la esquina
                    var startHookOrient = RebarHookOrientation.Left;
                    var endHookOrient = RebarHookOrientation.Left;

                    var vLeft = zVec.CrossProduct(vDir).Normalize();
                    var vRight = -vLeft;

                    if (cornerInfo.HasStartCorner && cornerInfo.StartContinuingDir != null)
                    {
                        double dotL = vLeft.DotProduct(cornerInfo.StartContinuingDir);
                        double dotR = vRight.DotProduct(cornerInfo.StartContinuingDir);
                        startHookOrient = (dotL >= dotR) ? RebarHookOrientation.Left : RebarHookOrientation.Right;
                    }

                    if (cornerInfo.HasEndCorner && cornerInfo.EndContinuingDir != null)
                    {
                        double dotL = vLeft.DotProduct(cornerInfo.EndContinuingDir);
                        double dotR = vRight.DotProduct(cornerInfo.EndContinuingDir);
                        endHookOrient = (dotL >= dotR) ? RebarHookOrientation.Left : RebarHookOrientation.Right;
                    }

                    if (geom.Openings == null || geom.Openings.Count == 0)
                    {
                        // =========================================================================
                        // CASO A: MURO CIEGO (SIN ABERTURAS) - MALLA CONTINUA ESTÁNDAR
                        // =========================================================================
                        foreach (var layer in layerOffsets)
                        {
                            // 1. Refuerzo Vertical
                            double zBotV = zMin + currentVBot;
                            double zTopV = zMax - currentVTop;

                            var pStartV = PointAt(vStartDist, layer.VerticalOffset, zBotV);
                            var pEndV = PointAt(vStartDist, layer.VerticalOffset, zTopV);

                            XYZ vHookLeft = vDir.CrossProduct(XYZ.BasisZ);
                            XYZ inwardDir = (layer.VerticalOffset >= 0) ? (-nVec) : nVec;
                            var vHookOrient = (vHookLeft.DotProduct(inwardDir) >= 0) ? RebarHookOrientation.Left : RebarHookOrientation.Right;

                            var vCurve = Line.CreateBound(pStartV, pEndV);
                            var vRebar = CreateRebarSafe(
                                doc,
                                RebarStyle.Standard,
                                vertBarType,
                                vStartHook,
                                vEndHook,
                                wall,
                                vDir,
                                new List<Curve> { vCurve },
                                vHookOrient,
                                vHookOrient);

                            if (vRebar != null)
                            {
                                try
                                {
                                    var accV = vRebar.GetShapeDrivenAccessor();
                                    accV.SetLayoutAsMaximumSpacing(sVFeet, vArrayLength, true, true, true);
                                }
                                catch { }

                                summary.RebarElementIds.Add(vRebar.Id.Value);
                                totalVertBars += vRebar.Quantity;

                                if (view3D != null)
                                {
                                    try { vRebar.SetUnobscuredInView(view3D, true); } catch { }
                                }
                            }

                            // 2. Refuerzo Horizontal
                            double zBotH = zMin + currentVBot;
                            double zTopH = zMax - currentVTop;
                            double hArrayLength = Math.Max(0.2, zTopH - zBotH);

                            var pStartH = PointAt(hBarStart, layer.HorizontalOffset, zBotH);
                            var pEndH = PointAt(hBarEnd, layer.HorizontalOffset, zBotH);

                            var hCurve = Line.CreateBound(pStartH, pEndH);
                            var hRebar = CreateRebarSafe(
                                doc,
                                RebarStyle.Standard,
                                horizBarType,
                                horizHookType,
                                horizHookType,
                                wall,
                                zVec,
                                new List<Curve> { hCurve },
                                startHookOrient,
                                endHookOrient);

                            if (hRebar != null)
                            {
                                try
                                {
                                    var accH = hRebar.GetShapeDrivenAccessor();
                                    accH.SetLayoutAsMaximumSpacing(sHFeet, hArrayLength, true, true, true);
                                }
                                catch { }

                                summary.RebarElementIds.Add(hRebar.Id.Value);
                                totalHorizBars += hRebar.Quantity;

                                if (view3D != null)
                                {
                                    try { hRebar.SetUnobscuredInView(view3D, true); } catch { }
                                }
                            }
                        }

                        // 3. Conectores transversales (Grapas / Crossties)
                        if (numMeshes == 2 && request.IncludeCrossties && layerOffsets.Count == 2)
                        {
                            CreateCrossties(doc, wall, PointAt,
                                wallLen, effectiveCoverFeet,
                                vStartDist, vEndDist, sVFeet,
                                zMin + currentVBot, zMax - currentVTop,
                                layerOffsets[0].VerticalOffset, layerOffsets[1].VerticalOffset,
                                dV,
                                request.CrosstieBarTypeName, request.CrosstieSpacingMeters,
                                request.CrosstiePlanPattern, request.CrosstieHookMode,
                                view3D, summary);
                        }
                    }
                    else
                    {
                        // =========================================================================
                        // CASO B: MURO CON ABERTURAS / VANOS - SUBDIVISIÓN + REFUERZO ADICIONAL
                        // =========================================================================
                        RebarHookType? openingMeshHook = !string.IsNullOrWhiteSpace(request.OpeningMeshHookTypeName)
                            ? ResolveStandardHook(doc, request.OpeningMeshHookTypeName)
                            : null;

                        CreateMeshWithOpenings(
                            doc, wall, PointAt,
                            geom, layerOffsets,
                            wallLen, zMin, zMax,
                            effectiveCoverFeet, currentVBot, currentVTop, currentHStart, currentHEnd,
                            vStartDist, vEndDist, hBarStart, hBarEnd,
                            sVFeet, sHFeet, dV,
                            vertBarType, horizBarType,
                            vStartHook, vEndHook, horizHookType, openingMeshHook,
                            startHookOrient, endHookOrient,
                            vDir, nVec, zVec,
                            numMeshes, request, view3D, summary,
                            ref totalVertBars, ref totalHorizBars);

                        CreateOpeningReinforcement(
                            doc, wall, PointAt,
                            geom, layerOffsets,
                            wallLen, wallThick, zMin, zMax,
                            effectiveCoverFeet, currentVBot, currentVTop, currentHStart, currentHEnd,
                            sVFeet, vertBarType, horizBarType,
                            vDir, nVec, zVec,
                            request, view3D, summary,
                            ref totalVertBars, ref totalHorizBars);
                    }

                    summary.VerticalBarsCount = totalVertBars;
                    summary.HorizontalBarsCount = totalHorizBars;
                    summary.TotalRebarElementsCount = summary.RebarElementIds.Count;
                    summary.Status = summary.TotalRebarElementsCount > 0 ? "Success" : "Failed";
                    if (summary.Status == "Failed")
                    {
                        summary.ErrorMessage = "No se pudieron generar armaduras en la geometría del muro.";
                    }
                    response.Results.Add(summary);
                }
                catch (Exception wallEx)
                {
                    summary.Status = "Failed";
                    summary.ErrorMessage = $"Error en muro ID {wallIdVal}: {wallEx.Message}";
                    response.Results.Add(summary);
                }
            }

            trans.Commit();

            int successCount = response.Results.Count(r => r.Status == "Success");
            response.Success = successCount > 0;
            response.TotalWallsProcessed = successCount;

            if (response.Success)
            {
                if (successCount == response.Results.Count)
                {
                    response.Message = $"Se armaron con éxito {successCount} muro(s) de concreto.";
                }
                else
                {
                    var failed = response.Results.Where(r => r.Status != "Success")
                        .Select(r => $"• Muro ID {r.WallElementId}: {r.ErrorMessage}");
                    response.Message = $"Se armaron con éxito {successCount} de {response.Results.Count} muro(s).\n\nMuros no procesados:\n{string.Join("\n", failed)}";
                }
            }
            else
            {
                var failed = response.Results.Select(r => $"• Muro ID {r.WallElementId}: {r.ErrorMessage}");
                response.Message = $"No se pudo completar el armado de muros:\n{string.Join("\n", failed)}";
            }

            return response;
        }
        catch (Exception ex)
        {
            try { trans.RollBack(); } catch { }
            response.Success = false;
            response.Message = $"Error durante el modelado de acero en muros: {ex.Message}";
            return response;
        }
    }

    /// <summary>
    /// Habilita los parámetros estructurales del muro en Revit para que sea un anfitrión válido de armadura.
    /// Evita que Revit lance 'InternalException: An internal error has occurred' en muros no definidos como estructurales.
    /// </summary>
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

    /// <summary>
    /// Genera un elemento Rebar de manera ultra-defensiva, evitando excepciones internas de Revit y conflictos de ganchos.
    /// </summary>
    private static Rebar? CreateRebarSafe(
        Document doc,
        RebarStyle style,
        RebarBarType barType,
        RebarHookType? startHook,
        RebarHookType? endHook,
        Element host,
        XYZ norm,
        IList<Curve> curves,
        RebarHookOrientation hookOrientStart = RebarHookOrientation.Left,
        RebarHookOrientation hookOrientEnd = RebarHookOrientation.Left)
    {
        // 1. Validar compatibilidad de estilos de gancho
        if (startHook != null && startHook.Style != style)
        {
            startHook = null;
        }
        if (endHook != null && endHook.Style != style)
        {
            endHook = null;
        }

        // 2. Intentar primero con useExistingShapeIfPossible=true, createNewShape=false (con ganchos si existen)
        try
        {
            var r = Rebar.CreateFromCurves(doc, style, barType, startHook, endHook, host, norm, curves, hookOrientStart, hookOrientEnd, true, false);
            if (r != null) return r;
        }
        catch { }

        // 3. Intentar con createNewShape=true (con ganchos si existen)
        if (startHook != null || endHook != null)
        {
            try
            {
                var r = Rebar.CreateFromCurves(doc, style, barType, startHook, endHook, host, norm, curves, hookOrientStart, hookOrientEnd, true, true);
                if (r != null) return r;
            }
            catch { }
        }

        // 4. Fallback si falló con ganchos: intentar sin ganchos para evitar 'RebarHooksDontMatchShape'
        try
        {
            var r = Rebar.CreateFromCurves(doc, style, barType, null, null, host, norm, curves, hookOrientStart, hookOrientEnd, true, false);
            if (r != null) return r;
        }
        catch { }

        // 5. Fallback definitivo: sin ganchos y createNewShape=true
        try
        {
            return Rebar.CreateFromCurves(doc, style, barType, null, null, host, norm, curves, hookOrientStart, hookOrientEnd, true, true);
        }
        catch
        {
            return null;
        }
    }

    private struct LayerOffset
    {
        public double VerticalOffset;
        public double HorizontalOffset;
    }

    private static List<LayerOffset> CalculateLayerOffsets(
        int numMeshes,
        double wallThickness,
        double cover,
        double dV,
        double dH,
        bool horizontalBarsOutside)
    {
        var result = new List<LayerOffset>();
        double halfThick = wallThickness / 2.0;

        if (numMeshes == 1)
        {
            // Malla simple (central)
            if (horizontalBarsOutside)
            {
                result.Add(new LayerOffset { HorizontalOffset = 0.0, VerticalOffset = -(dH / 2.0 + dV / 2.0) });
            }
            else
            {
                result.Add(new LayerOffset { VerticalOffset = 0.0, HorizontalOffset = -(dV / 2.0 + dH / 2.0) });
            }
        }
        else
        {
            // Doble malla (exterior e interior)
            double maxOffset = Math.Max(0.01, halfThick - cover);

            // Capa Exterior (+nVec)
            double extH, extV;
            if (horizontalBarsOutside)
            {
                extH = maxOffset - dH / 2.0;
                extV = extH - dH / 2.0 - dV / 2.0;
            }
            else
            {
                extV = maxOffset - dV / 2.0;
                extH = extV - dV / 2.0 - dH / 2.0;
            }
            result.Add(new LayerOffset { HorizontalOffset = extH, VerticalOffset = extV });

            // Capa Interior (-nVec)
            double intH, intV;
            if (horizontalBarsOutside)
            {
                intH = -(maxOffset - dH / 2.0);
                intV = intH + dH / 2.0 + dV / 2.0;
            }
            else
            {
                intV = -(maxOffset - dV / 2.0);
                intH = intV + dV / 2.0 + dH / 2.0;
            }
            result.Add(new LayerOffset { HorizontalOffset = intH, VerticalOffset = intV });
        }

        return result;
    }

    /// <summary>
    /// Genera la armadura de refuerzo para un muro con perfil editado inclinado (confinamiento de escalera).
    /// Las barras verticales se configuran como un conjunto continuo nativo (Varying Rebar Set), adaptando sus longitudes al perfil inclinado.
    /// Las barras horizontales se colocan paralelas al piso con longitudes progresivamente acortadas (Alternativa 1).
    /// Se añade una barra perimetral superior paralela a la arista inclinada (dintel inclinado).
    /// </summary>
    private static void CreateInclinedWallRebar(
        Document doc,
        Wall wall,
        WallGeometryInfo geom,
        CreateWallRebarRequest request,
        RebarBarType vertBarType,
        RebarBarType horizBarType,
        RebarHookType? vertHookType,
        RebarHookType? horizHookType,
        int numMeshes,
        WallRebarCreatedSummary summary,
        View3D? view3D)
    {
        double wallLen = geom.LengthFeet;
        double wallThick = geom.ThicknessFeet;
        double zMin = geom.ZMinFeet;
        double zMax = geom.ZMaxFeet;
        double wallHeight = zMax - zMin;

        double sVFeet = UnitConverter.MetersToFeet(Math.Max(0.05, request.VerticalSpacingMeters));
        double sHFeet = UnitConverter.MetersToFeet(Math.Max(0.05, request.HorizontalSpacingMeters));
        double manualCoverFeet = UnitConverter.MetersToFeet(Math.Max(0.01, request.CoverMeters));
        double requestedCoverFeet = request.UseWallCover ? geom.CoverFeet : manualCoverFeet;
        double effectiveCoverFeet = Math.Min(requestedCoverFeet, (wallThick / 2.0) * 0.45);
        if (effectiveCoverFeet < 0.01) effectiveCoverFeet = 0.01;

        double currentVTop = UnitConverter.MetersToFeet(Math.Max(0.01, request.VerticalTopOffsetMeters));
        double currentVBot = UnitConverter.MetersToFeet(Math.Max(0.01, request.VerticalBottomOffsetMeters));
        double currentHStart = UnitConverter.MetersToFeet(Math.Max(0.01, request.HorizontalStartOffsetMeters));
        double currentHEnd = UnitConverter.MetersToFeet(Math.Max(0.01, request.HorizontalEndOffsetMeters));

        var pOriginXY = new XYZ(geom.StartPoint.X, geom.StartPoint.Y, 0.0);
        var vDir = geom.Direction;
        var nVec = geom.Normal;
        var zVec = XYZ.BasisZ;

        XYZ PointAt(double s, double t, double z) =>
            new XYZ(pOriginXY.X + vDir.X * s + nVec.X * t,
                    pOriginXY.Y + vDir.Y * s + nVec.Y * t,
                    z);

        double dV = GetBarDiameter(vertBarType);
        double dH = GetBarDiameter(horizBarType);

        var layerOffsets = CalculateLayerOffsets(numMeshes, wallThick, effectiveCoverFeet, dV, dH, request.HorizontalBarsOutside);

        int totalVertBars = 0;
        int totalHorizBars = 0;

        double vStartDist = currentHStart;
        double vEndDist = wallLen - currentHEnd;
        double vArrayLength = Math.Max(0.2, vEndDist - vStartDist);

        RebarHookType? vStartHook = null;
        RebarHookType? vEndHook = null;
        if (vertHookType != null)
        {
            if (request.VerticalHookPosition == 0) vEndHook = vertHookType;
            else if (request.VerticalHookPosition == 1) { vStartHook = vertHookType; vEndHook = vertHookType; }
            else if (request.VerticalHookPosition == 2) vStartHook = vertHookType;
        }

        // 1. Detección de tramos de coronación (rectos vs inclinados)
        var topZones = geom.Profile.GetTopProfileZones(wallLen, zMax);

        // 2. Grilla longitudinal global de barras verticales
        int totalVSpaces = Math.Max(1, (int)Math.Round(vArrayLength / sVFeet));
        double actualSV = vArrayLength / totalVSpaces;

        var allSCoords = new List<double>();
        for (int i = 0; i <= totalVSpaces; i++)
        {
            allSCoords.Add(vStartDist + i * actualSV);
        }

        // 3. Refuerzo Vertical: Modelado segmentado por tramos (Conjuntos en tramos rectos / Varying en inclinados)
        foreach (var layer in layerOffsets)
        {
            double zBotV = zMin + currentVBot;
            XYZ vHookLeft = vDir.CrossProduct(XYZ.BasisZ);
            XYZ inwardDir = (layer.VerticalOffset >= 0) ? (-nVec) : nVec;
            var vHookOrient = (vHookLeft.DotProduct(inwardDir) >= 0) ? RebarHookOrientation.Left : RebarHookOrientation.Right;

            foreach (var zone in topZones)
            {
                var zoneS = allSCoords.Where(s => s >= zone.SStart - 0.01 && s <= zone.SEnd + 0.01).ToList();
                if (zoneS.Count == 0) continue;

                if (!zone.IsSloped)
                {
                    // =========================================================================
                    // TRAMO RECTO HORIZONTAL: CONJUNTO DE ARMADURA (REBAR SET) CON REGLA DE DISEÑO
                    // =========================================================================
                    double zTopFlat = zone.AvgZ - (effectiveCoverFeet + dV / 2.0);
                    if (zTopFlat <= zBotV + 0.2) continue;

                    if (zoneS.Count == 1)
                    {
                        var pt0 = PointAt(zoneS[0], layer.VerticalOffset, zBotV);
                        var pt1 = PointAt(zoneS[0], layer.VerticalOffset, zTopFlat);
                        var c = Line.CreateBound(pt0, pt1);
                        var r = CreateRebarSafe(doc, RebarStyle.Standard, vertBarType, vStartHook, vEndHook, wall, vDir, new List<Curve> { c }, vHookOrient, vHookOrient);
                        if (r != null)
                        {
                            summary.RebarElementIds.Add(r.Id.Value);
                            totalVertBars += 1;
                            if (view3D != null) r.SetUnobscuredInView(view3D, true);
                        }
                    }
                    else
                    {
                        double spanLen = zoneS.Last() - zoneS.First();
                        var pt0 = PointAt(zoneS.First(), layer.VerticalOffset, zBotV);
                        var pt1 = PointAt(zoneS.First(), layer.VerticalOffset, zTopFlat);
                        var c = Line.CreateBound(pt0, pt1);
                        var vRebarSet = CreateRebarSafe(doc, RebarStyle.Standard, vertBarType, vStartHook, vEndHook, wall, vDir, new List<Curve> { c }, vHookOrient, vHookOrient);
                        if (vRebarSet != null)
                        {
                            try
                            {
                                var acc = vRebarSet.GetShapeDrivenAccessor();
                                acc.SetLayoutAsNumberWithSpacing(zoneS.Count, actualSV, true, true, true);
                            }
                            catch
                            {
                                try
                                {
                                    var acc = vRebarSet.GetShapeDrivenAccessor();
                                    acc.SetLayoutAsMaximumSpacing(sVFeet, spanLen, true, true, true);
                                }
                                catch { }
                            }

                            summary.RebarElementIds.Add(vRebarSet.Id.Value);
                            totalVertBars += vRebarSet.Quantity;
                            if (view3D != null) vRebarSet.SetUnobscuredInView(view3D, true);
                        }
                    }
                }
                else
                {
                    // =========================================================================
                    // TRAMO INCLINADO / DIAGONAL: VARYING REBAR SET (O BARRAS INDEPENDIENTES SOLO AQUÍ)
                    // =========================================================================
                    double zTopStart = geom.Profile.GetTopElevationAt(zoneS.First(), zMax) - (effectiveCoverFeet + dV / 2.0);
                    double spanLen = zoneS.Last() - zoneS.First();

                    bool varyingSuccess = false;
                    if (zoneS.Count > 1)
                    {
                        var pt0 = PointAt(zoneS.First(), layer.VerticalOffset, zBotV);
                        var pt1 = PointAt(zoneS.First(), layer.VerticalOffset, zTopStart);
                        var c = Line.CreateBound(pt0, pt1);
                        var vVaryingSet = CreateRebarSafe(doc, RebarStyle.Standard, vertBarType, vStartHook, vEndHook, wall, vDir, new List<Curve> { c }, vHookOrient, vHookOrient);

                        if (vVaryingSet != null)
                        {
                            try
                            {
                                var acc = vVaryingSet.GetShapeDrivenAccessor();
                                acc.SetLayoutAsNumberWithSpacing(zoneS.Count, actualSV, true, true, true);
                                if (vVaryingSet.CanHaveVaryingLengthBars)
                                {
                                    acc.UseRebarConstraintsToProduceVaryingBars = true;
                                    varyingSuccess = true;
                                }
                            }
                            catch { }

                            if (varyingSuccess)
                            {
                                summary.RebarElementIds.Add(vVaryingSet.Id.Value);
                                totalVertBars += vVaryingSet.Quantity;
                                if (view3D != null) vVaryingSet.SetUnobscuredInView(view3D, true);
                            }
                            else
                            {
                                try { doc.Delete(vVaryingSet.Id); } catch { }
                                vVaryingSet = null;
                            }
                        }
                    }

                    if (!varyingSuccess)
                    {
                        // Fallback acotado exclusivamente a la zona inclinada
                        foreach (double sCur in zoneS)
                        {
                            double zTopCur = geom.Profile.GetTopElevationAt(sCur, zMax) - (effectiveCoverFeet + dV / 2.0);
                            if (zTopCur <= zBotV + 0.2) continue;

                            var pt0 = PointAt(sCur, layer.VerticalOffset, zBotV);
                            var pt1 = PointAt(sCur, layer.VerticalOffset, zTopCur);
                            var c = Line.CreateBound(pt0, pt1);
                            var r = CreateRebarSafe(doc, RebarStyle.Standard, vertBarType, vStartHook, vEndHook, wall, vDir, new List<Curve> { c }, vHookOrient, vHookOrient);
                            if (r != null)
                            {
                                summary.RebarElementIds.Add(r.Id.Value);
                                totalVertBars += 1;
                                if (view3D != null) r.SetUnobscuredInView(view3D, true);
                            }
                        }
                    }
                }
            }
        }

        // 4. Refuerzo Horizontal: Paño inferior completo (Rebar Set) + Paño superior inclinado (progresivo)
        var slopedZonesList = topZones.Where(z => z.IsSloped).ToList();
        double zLowestSlope = slopedZonesList.Count > 0
            ? slopedZonesList.Select(z => Math.Min(z.ZStart, z.ZEnd)).Min()
            : zMax;

        double zBotH = zMin + currentVBot;
        double zFullMax = zLowestSlope - (effectiveCoverFeet + dH / 2.0);
        double fullHeight = zFullMax - zBotH;

        if (fullHeight > 0.2)
        {
            int fullSpaces = Math.Max(1, (int)Math.Floor(fullHeight / sHFeet));
            double actualFullHeight = fullSpaces * sHFeet;

            // 4.1 Paño Inferior Completo: Conjunto de armadura continuo de extremo a extremo
            foreach (var layer in layerOffsets)
            {
                var ptStart = PointAt(currentHStart, layer.HorizontalOffset, zBotH);
                var ptEnd = PointAt(wallLen - currentHEnd, layer.HorizontalOffset, zBotH);
                var hCurve = Line.CreateBound(ptStart, ptEnd);

                var hRebarSet = CreateRebarSafe(
                    doc,
                    RebarStyle.Standard,
                    horizBarType,
                    horizHookType,
                    horizHookType,
                    wall,
                    zVec,
                    new List<Curve> { hCurve });

                if (hRebarSet != null)
                {
                    try
                    {
                        var acc = hRebarSet.GetShapeDrivenAccessor();
                        acc.SetLayoutAsMaximumSpacing(sHFeet, actualFullHeight, true, true, true);
                    }
                    catch { }

                    summary.RebarElementIds.Add(hRebarSet.Id.Value);
                    totalHorizBars += hRebarSet.Quantity;
                    if (view3D != null) hRebarSet.SetUnobscuredInView(view3D, true);
                }
            }

            // 4.2 Paño Superior Inclinado: Barras con longitud progresivamente acortada según la pendiente
            double curZ = zBotH + (fullSpaces + 1) * sHFeet;
            double zTopH = zMax - (effectiveCoverFeet + dH / 2.0);

            while (curZ <= zTopH + 0.01)
            {
                var (sMinSpan, sMaxSpan) = geom.Profile.GetHorizontalSpanAt(curZ, wallLen);
                double sStart = Math.Max(sMinSpan + currentHStart, currentHStart);
                double sEnd = Math.Min(sMaxSpan - currentHEnd, wallLen - currentHEnd);
                double barLen = sEnd - sStart;

                if (barLen >= 0.20)
                {
                    foreach (var layer in layerOffsets)
                    {
                        var ptStart = PointAt(sStart, layer.HorizontalOffset, curZ);
                        var ptEnd = PointAt(sEnd, layer.HorizontalOffset, curZ);
                        var hCurve = Line.CreateBound(ptStart, ptEnd);

                        var hRebar = CreateRebarSafe(
                            doc,
                            RebarStyle.Standard,
                            horizBarType,
                            horizHookType,
                            horizHookType,
                            wall,
                            zVec,
                            new List<Curve> { hCurve });

                        if (hRebar != null)
                        {
                            summary.RebarElementIds.Add(hRebar.Id.Value);
                            totalHorizBars += 1;
                            if (view3D != null) hRebar.SetUnobscuredInView(view3D, true);
                        }
                    }
                }

                curZ += sHFeet;
            }
        }
        else
        {
            // Muro completamente inclinado de punta a punta
            double curZ = zBotH;
            double zTopH = zMax - (effectiveCoverFeet + dH / 2.0);

            while (curZ <= zTopH + 0.01)
            {
                var (sMinSpan, sMaxSpan) = geom.Profile.GetHorizontalSpanAt(curZ, wallLen);
                double sStart = Math.Max(sMinSpan + currentHStart, currentHStart);
                double sEnd = Math.Min(sMaxSpan - currentHEnd, wallLen - currentHEnd);
                double barLen = sEnd - sStart;

                if (barLen >= 0.20)
                {
                    foreach (var layer in layerOffsets)
                    {
                        var ptStart = PointAt(sStart, layer.HorizontalOffset, curZ);
                        var ptEnd = PointAt(sEnd, layer.HorizontalOffset, curZ);
                        var hCurve = Line.CreateBound(ptStart, ptEnd);

                        var hRebar = CreateRebarSafe(
                            doc,
                            RebarStyle.Standard,
                            horizBarType,
                            horizHookType,
                            horizHookType,
                            wall,
                            zVec,
                            new List<Curve> { hCurve });

                        if (hRebar != null)
                        {
                            summary.RebarElementIds.Add(hRebar.Id.Value);
                            totalHorizBars += 1;
                            if (view3D != null) hRebar.SetUnobscuredInView(view3D, true);
                        }
                    }
                }

                curZ += sHFeet;
            }
        }

        // 5. Barra perimetral superior (dintel inclinado de coronación, desfasado en espesor T sin interferencias)
        if (request.IncludeInclinedTopBar && geom.Profile.BoundarySegments.Count > 0)
        {
            var topBarType = ResolveBarType(doc, request.InclinedTopBarTypeName, request.VerticalBarTypeName) ?? vertBarType;
            double dTop = GetBarDiameter(topBarType);

            var slopedSegs = geom.Profile.BoundarySegments
                .Where(seg => Math.Abs(seg.S1 - seg.S0) > 0.3 && Math.Abs(seg.Z1 - seg.Z0) > 0.3)
                .ToList();

            foreach (var seg in slopedSegs)
            {
                double sA = Math.Min(seg.S0, seg.S1);
                double sB = Math.Max(seg.S0, seg.S1);
                double zA = (seg.S0 <= seg.S1) ? seg.Z0 : seg.Z1;
                double zB = (seg.S0 <= seg.S1) ? seg.Z1 : seg.Z0;

                double ds = sB - sA;
                double dz = zB - zA;
                double lenSeg = Math.Sqrt(ds * ds + dz * dz);
                if (lenSeg < 0.5) continue;

                double us = ds / lenSeg;
                double uz = dz / lenSeg;

                double ns = -uz;
                double nz = us;
                if (nz > 0)
                {
                    ns = -ns;
                    nz = -nz;
                }

                // Cota de eje respetando estrictamente el recubrimiento libre de concreto
                double distFromEdge = effectiveCoverFeet + dTop / 2.0;

                double s0Bar = sA + us * currentHStart + ns * distFromEdge;
                double z0Bar = zA + uz * currentHStart + nz * distFromEdge;
                double s1Bar = sB - us * currentHEnd + ns * distFromEdge;
                double z1Bar = zB - uz * currentHEnd + nz * distFromEdge;

                double barSegLen = Math.Sqrt(Math.Pow(s1Bar - s0Bar, 2) + Math.Pow(z1Bar - z0Bar, 2));
                if (barSegLen < 0.3) continue;

                foreach (var layer in layerOffsets)
                {
                    // Desfase en espesor (T) hacia la cara interna de los verticales para evitar cruces 3D
                    double tDiag;
                    if (numMeshes == 1)
                    {
                        tDiag = layer.VerticalOffset + (dV / 2.0 + dTop / 2.0);
                    }
                    else
                    {
                        double sign = layer.VerticalOffset >= 0 ? -1.0 : 1.0;
                        tDiag = layer.VerticalOffset + sign * (dV / 2.0 + dTop / 2.0);
                    }

                    var pt0 = PointAt(s0Bar, tDiag, z0Bar);
                    var pt1 = PointAt(s1Bar, tDiag, z1Bar);
                    var topCurve = Line.CreateBound(pt0, pt1);

                    var topRebar = CreateRebarSafe(
                        doc,
                        RebarStyle.Standard,
                        topBarType,
                        null,
                        null,
                        wall,
                        nVec,
                        new List<Curve> { topCurve });

                    if (topRebar != null)
                    {
                        summary.RebarElementIds.Add(topRebar.Id.Value);
                        if (view3D != null)
                        {
                            try { topRebar.SetUnobscuredInView(view3D, true); } catch { }
                        }
                    }
                }
            }
        }

        // 4. Conectores transversales (Grapas / Crossties) si aplica
        if (numMeshes == 2 && request.IncludeCrossties && layerOffsets.Count == 2)
        {
            CreateInclinedCrossties(doc, wall, PointAt, geom, layerOffsets[0].VerticalOffset, layerOffsets[1].VerticalOffset,
                dV, request.CrosstieBarTypeName, request.CrosstieSpacingMeters, request.CrosstiePlanPattern, request.CrosstieHookMode,
                sVFeet, vStartDist, vEndDist, zMin + currentVBot, zMax, currentVTop, effectiveCoverFeet, view3D, summary);
        }

        summary.VerticalBarsCount = totalVertBars;
        summary.HorizontalBarsCount = totalHorizBars;
        summary.Status = "Success";
    }

    private static void CreateInclinedCrossties(
        Document doc,
        Wall wall,
        Func<double, double, double, XYZ> pointAt,
        WallGeometryInfo geom,
        double extOffset,
        double intOffset,
        double vertBarDiameter,
        string? barTypeName,
        double spacingMeters,
        int planPattern,
        int hookMode,
        double sVFeet,
        double vStartDist,
        double vEndDist,
        double zBot,
        double zMax,
        double currentVTop,
        double effectiveCoverFeet,
        View3D? view3D,
        WallRebarCreatedSummary summary)
    {
        try
        {
            var tieType = ResolveBarType(doc, barTypeName, "3/8\"");
            if (tieType == null) return;

            double sCrosstieFeet = UnitConverter.MetersToFeet(Math.Max(0.15, spacingMeters));
            double vSpan = vEndDist - vStartDist;
            if (vSpan < 0.2) return;

            int vSpaces = Math.Max(1, (int)Math.Ceiling(vSpan / sVFeet));
            double actualSv = vSpan / vSpaces;
            int kStep = (planPattern == 0) ? 1 : ((planPattern == 2) ? 3 : 2);

            var crosstieSCoords = new List<double>();
            for (int k = 0; k <= vSpaces; k += kStep)
            {
                crosstieSCoords.Add(vStartDist + k * actualSv);
            }
            if (crosstieSCoords.Count == 0) crosstieSCoords.Add(vStartDist + vSpan / 2.0);

            var allHooks = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarHookType))
                .Cast<RebarHookType>()
                .ToList();

            RebarHookType? hook135 = allHooks.FirstOrDefault(h => h.Name.Contains("135"));
            RebarHookType? hook90 = allHooks.FirstOrDefault(h => h.Name.Contains("90"));
            RebarHookType? hook180 = allHooks.FirstOrDefault(h => h.Name.Contains("180"));

            RebarHookType? startHook = hook135;
            RebarHookType? endHook = hook135;
            if (hookMode == 1) { startHook = hook135; endHook = hook90; }
            else if (hookMode == 2) { startHook = hook90; endHook = hook90; }
            else if (hookMode == 3) { startHook = hook180; endHook = hook180; }

            var tieStyle = RebarStyle.Standard;
            if (startHook != null) tieStyle = startHook.Style;

            double rV = vertBarDiameter / 2.0;
            double rTie = GetBarDiameter(tieType) / 2.0;
            double ptExtT = extOffset;
            double ptIntT = intOffset;

            foreach (double s in crosstieSCoords)
            {
                double zTopCol = geom.Profile.GetTopElevationAt(s, zMax) - currentVTop;
                double colHeight = zTopCol - zBot;
                if (colHeight < 0.3) continue;

                double sBar = s - rV - rTie;
                if (sBar < (effectiveCoverFeet + 0.01)) sBar = s + rV + rTie;

                var ptA = pointAt(sBar, ptIntT, zBot);
                var ptB = pointAt(sBar, ptExtT, zBot);
                if (ptA.DistanceTo(ptB) < 0.05) continue;

                var ptVert = pointAt(s, ptIntT, zBot);
                var targetDir = (ptVert - ptA).Normalize();
                var tieDir = (ptB - ptA).Normalize();
                var vLeft = XYZ.BasisZ.CrossProduct(tieDir).Normalize();
                var hookOrient = (vLeft.DotProduct(targetDir) >= 0)
                    ? RebarHookOrientation.Left
                    : RebarHookOrientation.Right;

                var tieCurve = Line.CreateBound(ptA, ptB);
                var rebarTie = CreateRebarSafe(
                    doc,
                    tieStyle,
                    tieType,
                    startHook,
                    endHook,
                    wall,
                    XYZ.BasisZ,
                    new List<Curve> { tieCurve },
                    hookOrient,
                    hookOrient);

                if (rebarTie != null)
                {
                    try
                    {
                        var acc = rebarTie.GetShapeDrivenAccessor();
                        acc.SetLayoutAsMaximumSpacing(sCrosstieFeet, colHeight, true, true, true);
                    }
                    catch { }

                    summary.RebarElementIds.Add(rebarTie.Id.Value);
                    if (view3D != null)
                    {
                        try { rebarTie.SetUnobscuredInView(view3D, true); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    private static void CreateBoundaryTieSet(
        Document doc,
        Wall wall,
        Func<double, double, double, XYZ> pointAt,
        double startDistAlongWall,
        double boundaryLength,
        double wallThickness,
        double cover,
        double zBot,
        double zTop,
        XYZ vDir,
        XYZ nVec,
        bool isRightHandedZ,
        RebarBarType tieBarType,
        RebarShape? tieShape,
        RebarHookType? hook135,
        double tieSpacing,
        View3D? view3D,
        WallRebarCreatedSummary summary)
    {
        try
        {
            double tieWidth = Math.Max(0.2, boundaryLength - 2.0 * cover);
            double tieDepth = Math.Max(0.12, wallThickness - 2.0 * cover);
            double arrayHeight = Math.Max(0.2, zTop - zBot);

            double sCenter = startDistAlongWall + boundaryLength / 2.0;
            double halfW = tieWidth / 2.0;
            double halfD = tieDepth / 2.0;

            XYZ origin;
            XYZ xVec;
            XYZ yVec;
            XYZ boxVecX;
            XYZ boxVecY;
            double dimA;
            double dimB;

            if (isRightHandedZ)
            {
                origin = pointAt(sCenter - halfW, -halfD, zBot);
                xVec = vDir;
                yVec = nVec;
                boxVecX = xVec * tieWidth;
                boxVecY = yVec * tieDepth;
                dimA = tieWidth;
                dimB = tieDepth;
            }
            else
            {
                origin = pointAt(sCenter - halfW, -halfD, zBot);
                xVec = nVec;
                yVec = vDir;
                boxVecX = xVec * tieDepth;
                boxVecY = yVec * tieWidth;
                dimA = tieDepth;
                dimB = tieWidth;
            }

            Rebar? tieRebar = null;

            // 1. Intentar creación nativa con RebarShape de estribo sísmico (T1/51 con ganchos a 135°)
            if (tieShape != null)
            {
                try
                {
                    tieRebar = Rebar.CreateFromRebarShape(doc, tieShape, tieBarType, wall, origin, xVec, yVec);
                    if (tieRebar != null)
                    {
                        ApplyStirrupDimensions(tieRebar, origin, boxVecX, boxVecY, dimA, dimB);
                    }
                }
                catch { }
            }

            // 2. Fallback con curvas si no hay shape o falló
            if (tieRebar == null)
            {
                var c1 = pointAt(sCenter - halfW, -halfD, zBot);
                var c2 = pointAt(sCenter + halfW, -halfD, zBot);
                var c3 = pointAt(sCenter + halfW, +halfD, zBot);
                var c4 = pointAt(sCenter - halfW, +halfD, zBot);

                // Pequeño desfase para dejar el loop abierto y permitir que Revit asigne ganchos a 135°
                double dStir = GetBarDiameter(tieBarType);
                var cEnd = pointAt(sCenter - halfW, -halfD + Math.Min(halfD * 0.5, dStir), zBot);

                var curves = new List<Curve>
                {
                    Line.CreateBound(c1, c2),
                    Line.CreateBound(c2, c3),
                    Line.CreateBound(c3, c4),
                    Line.CreateBound(c4, cEnd)
                };

                RebarStyle tieStyle = hook135?.Style ?? RebarStyle.StirrupTie;
                tieRebar = CreateRebarSafe(
                    doc,
                    tieStyle,
                    tieBarType,
                    hook135,
                    hook135,
                    wall,
                    XYZ.BasisZ,
                    curves,
                    RebarHookOrientation.Left,
                    RebarHookOrientation.Right);
            }

            if (tieRebar != null)
            {
                // Asegurar asignación explícita del gancho sísmico a 135° en ambos extremos
                if (hook135 != null)
                {
                    try { tieRebar.SetHookTypeId(0, hook135.Id); } catch { }
                    try { tieRebar.SetHookTypeId(1, hook135.Id); } catch { }
                    try { tieRebar.SetHookOrientation(0, RebarHookOrientation.Left); } catch { }
                    try { tieRebar.SetHookOrientation(1, RebarHookOrientation.Right); } catch { }
                }

                try
                {
                    var acc = tieRebar.GetShapeDrivenAccessor();
                    acc.SetLayoutAsMaximumSpacing(tieSpacing, arrayHeight, true, true, true);
                }
                catch { }

                summary.RebarElementIds.Add(tieRebar.Id.Value);

                if (view3D != null)
                {
                    try { tieRebar.SetUnobscuredInView(view3D, true); } catch { }
                }
            }
        }
        catch
        {
            // Silencioso ante contingencias de borde
        }
    }

    private static void CreateCrossties(
        Document doc,
        Wall wall,
        Func<double, double, double, XYZ> pointAt,
        double wallLen,
        double effectiveCoverFeet,
        double vStartDist,
        double vEndDist,
        double sVFeet,
        double zBot,
        double zTop,
        double extOffset,
        double intOffset,
        double vertBarDiameter,
        string? barTypeName,
        double spacingMeters,
        int planPattern,
        int hookMode,
        View3D? view3D,
        WallRebarCreatedSummary summary)
    {
        try
        {
            var tieType = ResolveBarType(doc, barTypeName, "3/8\"");
            if (tieType == null) return;

            double sCrosstieFeet = UnitConverter.MetersToFeet(Math.Max(0.15, spacingMeters));
            double vSpan = vEndDist - vStartDist;
            double arrayHeight = Math.Max(0.2, zTop - zBot);

            if (vSpan < 0.2 || arrayHeight < 0.2) return;

            // 1. Identificar las posiciones exactas de las barras verticales
            int vSpaces = Math.Max(1, (int)Math.Ceiling(vSpan / sVFeet));
            double actualSv = vSpan / vSpaces;

            // Determinar cada cuántas barras verticales se coloca una columna de grapas según el patrón en planta:
            // 0 = En cada barra vertical (kStep = 1)
            // 1 = Dejando 1 barra vertical (kStep = 2) [alternada]
            // 2 = Dejando 2 barras verticales (kStep = 3)
            int kStep = (planPattern == 0) ? 1 : ((planPattern == 2) ? 3 : 2);

            var crosstieSCoords = new List<double>();
            for (int k = 0; k <= vSpaces; k += kStep)
            {
                crosstieSCoords.Add(vStartDist + k * actualSv);
            }

            if (crosstieSCoords.Count == 0)
            {
                crosstieSCoords.Add(vStartDist + vSpan / 2.0);
            }

            // 2. Resolver ganchos para los extremos según el modo seleccionado:
            // 0 = Ambos a 135° (135° - 135°) [Predeterminado]
            // 1 = Combinado (135° y 90°)
            // 2 = Ambos a 90° (90° - 90°)
            // 3 = Ambos a 180° (180° - 180°)
            var allHooks = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarHookType))
                .Cast<RebarHookType>()
                .ToList();

            var hook135 = allHooks.FirstOrDefault(h => h.Name.Contains("135"))
                       ?? allHooks.FirstOrDefault(h => h.Name.Contains("90"));

            var hook90 = allHooks.FirstOrDefault(h => h.Name.Contains("90"))
                      ?? hook135;

            var hook180 = allHooks.FirstOrDefault(h => h.Name.Contains("180"))
                       ?? hook135;

            if (hook135 == null && allHooks.Count > 0)
            {
                hook135 = allHooks[0];
                hook90 = allHooks[0];
                hook180 = allHooks[0];
            }

            RebarHookType? startHook;
            RebarHookType? endHook;

            switch (hookMode)
            {
                case 1: // Combinado (135° y 90°)
                    startHook = hook90;
                    endHook = hook135;
                    break;
                case 2: // Ambos a 90°
                    startHook = hook90;
                    endHook = hook90;
                    break;
                case 3: // Ambos a 180°
                    startHook = hook180;
                    endHook = hook180;
                    break;
                case 0: // Ambos a 135° (Predeterminado)
                default:
                    startHook = hook135;
                    endHook = hook135;
                    break;
            }

            RebarStyle tieStyle = endHook?.Style ?? startHook?.Style ?? RebarStyle.Standard;
            if (startHook != null && startHook.Style != tieStyle)
            {
                if (hookMode == 1)
                {
                    var matching90 = allHooks.FirstOrDefault(h => h.Name.Contains("90") && h.Style == tieStyle);
                    startHook = matching90 ?? endHook;
                }
                else
                {
                    startHook = endHook;
                }
            }

            double dTie = GetBarDiameter(tieType);
            double rTie = dTie / 2.0;
            double rV = vertBarDiameter / 2.0;

            // 3. Coordenadas transversales que extienden el tramo recto hasta la cara exterior de las barras verticales
            // De este modo, los ganchos nacen en la cara exterior y giran abrazando y confinando al acero vertical
            double ptIntT = intOffset - rV - rTie;
            double ptExtT = extOffset + rV + rTie;

            foreach (double s in crosstieSCoords)
            {
                // Ubicación del tramo recto adyacente a la pareja de barras verticales
                double sBar = s - rV - rTie;
                if (sBar < (effectiveCoverFeet + 0.01))
                {
                    sBar = s + rV + rTie;
                }

                var ptA = pointAt(sBar, ptIntT, zBot);
                var ptB = pointAt(sBar, ptExtT, zBot);

                if (ptA.DistanceTo(ptB) < 0.05) continue;

                // Determinar dinámicamente la orientación del gancho según el vector real del muro (independiente de wall.Orientation)
                var ptVert = pointAt(s, ptIntT, zBot);
                var targetDir = (ptVert - ptA).Normalize();
                var tieDir = (ptB - ptA).Normalize();
                var vLeft = XYZ.BasisZ.CrossProduct(tieDir).Normalize();
                var hookOrient = (vLeft.DotProduct(targetDir) >= 0)
                    ? RebarHookOrientation.Left
                    : RebarHookOrientation.Right;

                try
                {
                    var tieCurve = Line.CreateBound(ptA, ptB);
                    var rebarTie = CreateRebarSafe(
                        doc,
                        tieStyle,
                        tieType,
                        startHook,
                        endHook,
                        wall,
                        XYZ.BasisZ,
                        new List<Curve> { tieCurve },
                        hookOrient,
                        hookOrient);

                    // Verificación geométrica 3D post-creación: si los ganchos quedaron proyectados en sentido opuesto al acero vertical, invertir orientación
                    if (rebarTie != null && !IsHookWrappingVerticalBar(rebarTie, ptA, targetDir))
                    {
                        var flippedOrient = (hookOrient == RebarHookOrientation.Left)
                            ? RebarHookOrientation.Right
                            : RebarHookOrientation.Left;
                        try { doc.Delete(rebarTie.Id); } catch { }
                        rebarTie = CreateRebarSafe(
                            doc,
                            tieStyle,
                            tieType,
                            startHook,
                            endHook,
                            wall,
                            XYZ.BasisZ,
                            new List<Curve> { tieCurve },
                            flippedOrient,
                            flippedOrient);
                    }

                    if (rebarTie != null)
                    {
                        // Configurar como conjunto con regla de espaciamiento máximo en altura
                        try
                        {
                            var acc = rebarTie.GetShapeDrivenAccessor();
                            acc.SetLayoutAsMaximumSpacing(sCrosstieFeet, arrayHeight, true, true, true);
                        }
                        catch { }

                        summary.RebarElementIds.Add(rebarTie.Id.Value);
                        if (view3D != null)
                        {
                            try { rebarTie.SetUnobscuredInView(view3D, true); } catch { }
                        }
                    }
                }
                catch
                {
                    // Tolerar fallo de una columna individual sin comprometer el conjunto
                }
            }
        }
        catch
        {
            // Silencioso
        }
    }

    /// <summary>
    /// Comprueba mediante la geometría 3D real generada por Revit si los ganchos de la grapa se proyectan
    /// envolviendo hacia la barra vertical (targetDir) y no en sentido opuesto.
    /// </summary>
    private static bool IsHookWrappingVerticalBar(Rebar rebarTie, XYZ ptOnStraightBar, XYZ targetDir)
    {
        try
        {
            var curves = rebarTie.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
            if (curves == null || curves.Count == 0) return true;

            double maxAbsProj = 0.0;
            double maxSignedProj = 0.0;

            foreach (var c in curves)
            {
                var pts = new[] { c.GetEndPoint(0), c.Evaluate(0.5, true), c.GetEndPoint(1) };
                foreach (var pt in pts)
                {
                    double proj = (pt - ptOnStraightBar).DotProduct(targetDir);
                    if (Math.Abs(proj) > maxAbsProj)
                    {
                        maxAbsProj = Math.Abs(proj);
                        maxSignedProj = proj;
                    }
                }
            }

            if (maxAbsProj > 0.005 && maxSignedProj < 0.0)
            {
                return false;
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static double GetBarDiameter(RebarBarType barType)
    {
        try
        {
            if (barType.BarNominalDiameter > 0.005) return barType.BarNominalDiameter;
            if (barType.BarModelDiameter > 0.005) return barType.BarModelDiameter;
        }
        catch { }
        return 0.03125; // ~3/8" (0.03125 ft) fallback
    }

    private static RebarBarType? ResolveBarType(Document doc, string? name, string fallbackPattern = "3/8\"")
    {
        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .ToList();

        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                     ?? types.FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return types.FirstOrDefault(t => t.Name.Contains(fallbackPattern, StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault(t => t.Name.Contains("10") || t.Name.Contains("12") || t.Name.Contains("#4"))
            ?? types.FirstOrDefault();
    }

    /// <summary>
    /// Resuelve un gancho estrictamente de estilo Standard para armadura longitudinal de malla.
    /// Evita ganchos de estilo Stirrup/Tie que provocan error 'The Rebar's hooks do not match its Rebar Shape'.
    /// </summary>
    private static RebarHookType? ResolveStandardHook(Document doc, string? preferredName)
    {
        var standardHooks = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .Where(h => h.Style == RebarStyle.Standard)
            .ToList();

        if (standardHooks.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var match = standardHooks.FirstOrDefault(h => h.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase))
                     ?? standardHooks.FirstOrDefault(h => h.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return standardHooks.FirstOrDefault(h => h.Name.Contains("90") || h.Name.Contains("Standard") || h.Name.Contains("Estándar"));
    }

    private static RebarShape? ResolveRebarShape(Document doc, string? preferredName)
    {
        return ResolveSeismicStirrupShape(doc, preferredName);
    }

    private static RebarShape? ResolveSeismicStirrupShape(Document doc, string? preferredName = null)
    {
        var shapes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarShape))
            .Cast<RebarShape>()
            .ToList();

        var stirrupShapes = shapes.Where(s => s.RebarStyle == RebarStyle.StirrupTie).ToList();
        var pool = stirrupShapes.Any() ? stirrupShapes : shapes;

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var match = pool.FirstOrDefault(s => s.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase))
                     ?? pool.FirstOrDefault(s => s.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        // Buscar por nomenclatura estándar de estribo sísmico cerrado con ganchos (T1, 51, M_T1, 135, Estribo, Stirrup, Cerco)
        // Se descartan formas tipo '00' que corresponden a rectángulos cerrados sin ganchos
        var matchT1 = pool.FirstOrDefault(s => s.Name.Contains("T1", StringComparison.OrdinalIgnoreCase))
                   ?? pool.FirstOrDefault(s => s.Name.Contains("51"))
                   ?? pool.FirstOrDefault(s => s.Name.Contains("135"))
                   ?? pool.FirstOrDefault(s => (s.Name.Contains("Stirrup", StringComparison.OrdinalIgnoreCase) ||
                                                s.Name.Contains("Estribo", StringComparison.OrdinalIgnoreCase) ||
                                                s.Name.Contains("Cerco", StringComparison.OrdinalIgnoreCase)) && !s.Name.Contains("00"));

        if (matchT1 != null) return matchT1;

        // Si no hay match explícito, buscar cualquier forma que no sea '00' (rectángulo plano sin ganchos)
        var non00 = pool.FirstOrDefault(s => !s.Name.Contains("00") && s.RebarStyle == RebarStyle.StirrupTie);
        return non00;
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
        catch
        {
            scaled = false;
        }

        if (!scaled)
        {
            TrySetRebarParameter(rebar, "A", widthFeet);
            TrySetRebarParameter(rebar, "B", heightFeet);
            TrySetRebarParameter(rebar, "b", widthFeet);
            TrySetRebarParameter(rebar, "h", heightFeet);
            TrySetRebarParameter(rebar, "Width", widthFeet);
            TrySetRebarParameter(rebar, "Height", heightFeet);
            TrySetRebarParameter(rebar, "Ancho", widthFeet);
            TrySetRebarParameter(rebar, "Peralte", heightFeet);
            TrySetRebarParameter(rebar, "Altura", heightFeet);
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

    // =========================================================================
    // SUBDIVISIÓN DE MALLA Y REFUERZO ESPECIAL EN ABERTURAS / VANOS
    // =========================================================================

    private struct VerticalZone
    {
        public double S0;
        public double S1;
        public double Z0;
        public double Z1;
        public bool BottomAtOpening;
        public bool TopAtOpening;
    }

    private struct HorizontalZone
    {
        public double S0;
        public double S1;
        public double Z0;
        public double Z1;
        public bool StartAtOpening;
        public bool EndAtOpening;
    }

    /// <summary>
    /// Subdivide las mallas verticales, horizontales y grapas transversales alrededor de todas las aberturas
    /// detectadas en el muro, aplicando opcionalmente ganchos en los bordes del vano.
    /// </summary>
    private static void CreateMeshWithOpenings(
        Document doc,
        Wall wall,
        Func<double, double, double, XYZ> pointAt,
        WallGeometryInfo geom,
        List<LayerOffset> layerOffsets,
        double wallLen,
        double zMin,
        double zMax,
        double effectiveCoverFeet,
        double currentVBot,
        double currentVTop,
        double currentHStart,
        double currentHEnd,
        double vStartDist,
        double vEndDist,
        double hBarStart,
        double hBarEnd,
        double sVFeet,
        double sHFeet,
        double dV,
        RebarBarType vertBarType,
        RebarBarType horizBarType,
        RebarHookType? vStartHook,
        RebarHookType? vEndHook,
        RebarHookType? horizHookType,
        RebarHookType? openingMeshHook,
        RebarHookOrientation startHookOrient,
        RebarHookOrientation endHookOrient,
        XYZ vDir,
        XYZ nVec,
        XYZ zVec,
        int numMeshes,
        CreateWallRebarRequest request,
        View3D? view3D,
        WallRebarCreatedSummary summary,
        ref int totalVertBars,
        ref int totalHorizBars)
    {
        double zBotV = zMin + currentVBot;
        double zTopV = zMax - currentVTop;

        // 1. Construir sub-zonas para barras verticales y grapas (machones completos + dinteles + alféizares)
        var sortedOps = geom.Openings.OrderBy(o => o.SMinFeet).ToList();
        var vZones = new List<VerticalZone>();

        // Puntos de corte longitudinales X/S generados por las aberturas
        var sBreaks = new List<double> { vStartDist, vEndDist };
        foreach (var op in sortedOps)
        {
            double sLeft = op.SMinFeet - effectiveCoverFeet;
            double sRight = op.SMaxFeet + effectiveCoverFeet;
            if (sLeft > vStartDist + 0.05 && sLeft < vEndDist - 0.05) sBreaks.Add(sLeft);
            if (sRight > vStartDist + 0.05 && sRight < vEndDist - 0.05) sBreaks.Add(sRight);
        }
        sBreaks = sBreaks.OrderBy(x => x).ToList();

        // Filtrar intervalos longitudinales únicos
        for (int i = 0; i < sBreaks.Count - 1; i++)
        {
            double s0 = sBreaks[i];
            double s1 = sBreaks[i + 1];
            if (s1 - s0 < 0.12) continue;

            double sMid = 0.5 * (s0 + s1);
            // Buscar aberturas que cubran este tramo longitudinal sMid
            var opsInStrip = sortedOps
                .Where(o => sMid > (o.SMinFeet - effectiveCoverFeet - 0.01) && sMid < (o.SMaxFeet + effectiveCoverFeet + 0.01))
                .OrderBy(o => o.ZMinFeet)
                .ToList();

            if (opsInStrip.Count == 0)
            {
                // Machón continuo de piso a techo
                vZones.Add(new VerticalZone
                {
                    S0 = s0,
                    S1 = s1,
                    Z0 = zBotV,
                    Z1 = zTopV,
                    BottomAtOpening = false,
                    TopAtOpening = false
                });
            }
            else
            {
                double curZ = zBotV;
                bool curBottomAtOp = false;

                foreach (var op in opsInStrip)
                {
                    double opZBot = op.ZMinFeet - effectiveCoverFeet;
                    double opZTop = op.ZMaxFeet + effectiveCoverFeet;

                    // Segmento bajo la abertura (Alféizar)
                    if (opZBot - curZ >= 0.22)
                    {
                        vZones.Add(new VerticalZone
                        {
                            S0 = s0,
                            S1 = s1,
                            Z0 = curZ,
                            Z1 = opZBot,
                            BottomAtOpening = curBottomAtOp,
                            TopAtOpening = true
                        });
                    }

                    curZ = Math.Max(curZ, opZTop);
                    curBottomAtOp = true;
                }

                // Segmento sobre la última abertura (Dintel)
                if (zTopV - curZ >= 0.22)
                {
                    vZones.Add(new VerticalZone
                    {
                        S0 = s0,
                        S1 = s1,
                        Z0 = curZ,
                        Z1 = zTopV,
                        BottomAtOpening = curBottomAtOp,
                        TopAtOpening = false
                    });
                }
            }
        }

        // 2. Construir sub-zonas para barras horizontales (franjas continuas + segmentos laterales a los vanos)
        var hZones = new List<HorizontalZone>();
        var zBreaks = new List<double> { zBotV, zTopV };
        foreach (var op in sortedOps)
        {
            double zLow = op.ZMinFeet - effectiveCoverFeet;
            double zHigh = op.ZMaxFeet + effectiveCoverFeet;
            if (zLow > zBotV + 0.05 && zLow < zTopV - 0.05) zBreaks.Add(zLow);
            if (zHigh > zBotV + 0.05 && zHigh < zTopV - 0.05) zBreaks.Add(zHigh);
        }
        zBreaks = zBreaks.OrderBy(z => z).ToList();

        for (int i = 0; i < zBreaks.Count - 1; i++)
        {
            double z0 = zBreaks[i];
            double z1 = zBreaks[i + 1];
            if (z1 - z0 < 0.12) continue;

            double zMid = 0.5 * (z0 + z1);
            var opsInBand = sortedOps
                .Where(o => zMid > (o.ZMinFeet - effectiveCoverFeet - 0.01) && zMid < (o.ZMaxFeet + effectiveCoverFeet + 0.01))
                .OrderBy(o => o.SMinFeet)
                .ToList();

            if (opsInBand.Count == 0)
            {
                // Franja horizontal continua de extremo a extremo del muro (dintel o alféizar corrido)
                hZones.Add(new HorizontalZone
                {
                    S0 = hBarStart,
                    S1 = hBarEnd,
                    Z0 = z0,
                    Z1 = z1,
                    StartAtOpening = false,
                    EndAtOpening = false
                });
            }
            else
            {
                double curS = hBarStart;
                bool curStartAtOp = false;

                foreach (var op in opsInBand)
                {
                    double opSLeft = op.SMinFeet - effectiveCoverFeet;
                    double opSRight = op.SMaxFeet + effectiveCoverFeet;

                    if (opSLeft - curS >= 0.22)
                    {
                        hZones.Add(new HorizontalZone
                        {
                            S0 = curS,
                            S1 = opSLeft,
                            Z0 = z0,
                            Z1 = z1,
                            StartAtOpening = curStartAtOp,
                            EndAtOpening = true
                        });
                    }

                    curS = Math.Max(curS, opSRight);
                    curStartAtOp = true;
                }

                if (hBarEnd - curS >= 0.22)
                {
                    hZones.Add(new HorizontalZone
                    {
                        S0 = curS,
                        S1 = hBarEnd,
                        Z0 = z0,
                        Z1 = z1,
                        StartAtOpening = curStartAtOp,
                        EndAtOpening = false
                    });
                }
            }
        }

        // 3. Modelar todas las sub-zonas verticales y horizontales para cada capa de malla
        var vLeftHoriz = zVec.CrossProduct(vDir).Normalize();
        XYZ vHookLeft = vDir.CrossProduct(XYZ.BasisZ);

        foreach (var layer in layerOffsets)
        {
            XYZ inwardDir = (layer.VerticalOffset >= 0) ? (-nVec) : nVec;
            var vHookOrient = (vHookLeft.DotProduct(inwardDir) >= 0) ? RebarHookOrientation.Left : RebarHookOrientation.Right;

            XYZ hInwardDir = (layer.HorizontalOffset >= 0) ? (-nVec) : nVec;
            var opHorizHookOrient = (vLeftHoriz.DotProduct(hInwardDir) >= 0) ? RebarHookOrientation.Left : RebarHookOrientation.Right;

            // 3A. Sub-zonas Verticales
            foreach (var vz in vZones)
            {
                var pStartV = pointAt(vz.S0, layer.VerticalOffset, vz.Z0);
                var pEndV = pointAt(vz.S0, layer.VerticalOffset, vz.Z1);
                if (pStartV.DistanceTo(pEndV) < 0.20) continue;

                RebarHookType? curStartHook = vz.BottomAtOpening ? openingMeshHook : vStartHook;
                RebarHookType? curEndHook = vz.TopAtOpening ? openingMeshHook : vEndHook;

                var vCurve = Line.CreateBound(pStartV, pEndV);
                var vRebar = CreateRebarSafe(
                    doc,
                    RebarStyle.Standard,
                    vertBarType,
                    curStartHook,
                    curEndHook,
                    wall,
                    vDir,
                    new List<Curve> { vCurve },
                    vHookOrient,
                    vHookOrient);

                if (vRebar != null)
                {
                    double spanS = vz.S1 - vz.S0;
                    if (spanS >= sVFeet * 0.55)
                    {
                        try
                        {
                            var accV = vRebar.GetShapeDrivenAccessor();
                            accV.SetLayoutAsMaximumSpacing(sVFeet, spanS, true, true, true);
                        }
                        catch { }
                    }

                    summary.RebarElementIds.Add(vRebar.Id.Value);
                    totalVertBars += vRebar.Quantity;

                    if (view3D != null)
                    {
                        try { vRebar.SetUnobscuredInView(view3D, true); } catch { }
                    }
                }
            }

            // 3B. Sub-zonas Horizontales
            foreach (var hz in hZones)
            {
                var pStartH = pointAt(hz.S0, layer.HorizontalOffset, hz.Z0);
                var pEndH = pointAt(hz.S1, layer.HorizontalOffset, hz.Z0);
                if (pStartH.DistanceTo(pEndH) < 0.20) continue;

                RebarHookType? curHStartHook = hz.StartAtOpening ? openingMeshHook : horizHookType;
                RebarHookType? curHEndHook = hz.EndAtOpening ? openingMeshHook : horizHookType;

                var curStartOrient = hz.StartAtOpening ? opHorizHookOrient : startHookOrient;
                var curEndOrient = hz.EndAtOpening ? opHorizHookOrient : endHookOrient;

                var hCurve = Line.CreateBound(pStartH, pEndH);
                var hRebar = CreateRebarSafe(
                    doc,
                    RebarStyle.Standard,
                    horizBarType,
                    curHStartHook,
                    curHEndHook,
                    wall,
                    zVec,
                    new List<Curve> { hCurve },
                    curStartOrient,
                    curEndOrient);

                if (hRebar != null)
                {
                    double spanZ = hz.Z1 - hz.Z0;
                    if (spanZ >= sHFeet * 0.55)
                    {
                        try
                        {
                            var accH = hRebar.GetShapeDrivenAccessor();
                            accH.SetLayoutAsMaximumSpacing(sHFeet, spanZ, true, true, true);
                        }
                        catch { }
                    }

                    summary.RebarElementIds.Add(hRebar.Id.Value);
                    totalHorizBars += hRebar.Quantity;

                    if (view3D != null)
                    {
                        try { hRebar.SetUnobscuredInView(view3D, true); } catch { }
                    }
                }
            }
        }

        // 4. Grapas transversales (Crossties) en cada sub-zona vertical
        if (numMeshes == 2 && request.IncludeCrossties && layerOffsets.Count == 2)
        {
            foreach (var vz in vZones)
            {
                if ((vz.S1 - vz.S0) >= 0.25 && (vz.Z1 - vz.Z0) >= 0.25)
                {
                    CreateCrossties(doc, wall, pointAt,
                        wallLen, effectiveCoverFeet,
                        vz.S0, vz.S1, sVFeet,
                        vz.Z0, vz.Z1,
                        layerOffsets[0].VerticalOffset, layerOffsets[1].VerticalOffset,
                        dV,
                        request.CrosstieBarTypeName, request.CrosstieSpacingMeters,
                        request.CrosstiePlanPattern, request.CrosstieHookMode,
                        view3D, summary);
                }
            }
        }
    }

    /// <summary>
    /// Genera el refuerzo adicional alrededor de cada abertura del muro según las opciones activas:
    /// 1) Barras longitudinales perimetrales (laterales, dintel y alféizar) con parámetro de separación,
    ///    modeladas como conjunto de regla 'Número Fijo' (Fixed Number).
    /// 2) Estribos cerrados confinando y envolviendo exactamente al paquete perimetral.
    /// 3) Barras diagonales a 45° en las esquinas libres del vano.
    /// </summary>
    private static void CreateOpeningReinforcement(
        Document doc,
        Wall wall,
        Func<double, double, double, XYZ> pointAt,
        WallGeometryInfo geom,
        List<LayerOffset> layerOffsets,
        double wallLen,
        double wallThick,
        double zMin,
        double zMax,
        double effectiveCoverFeet,
        double currentVBot,
        double currentVTop,
        double currentHStart,
        double currentHEnd,
        double sVFeet,
        RebarBarType defaultVertBar,
        RebarBarType defaultHorizBar,
        XYZ vDir,
        XYZ nVec,
        XYZ zVec,
        CreateWallRebarRequest request,
        View3D? view3D,
        WallRebarCreatedSummary summary,
        ref int totalVertBars,
        ref int totalHorizBars)
    {
        if (!request.IncludeOpeningPerimeterBars && !request.IncludeOpeningDiagonalBars && !request.IncludeOpeningEdgeStirrups)
            return;

        double sMinWall = currentHStart;
        double sMaxWall = wallLen - currentHEnd;
        double zMinWall = zMin + currentVBot;
        double zMaxWall = zMax - currentVTop;

        var perimBarType = ResolveBarType(doc, request.OpeningPerimeterBarTypeName, defaultVertBar.Name) ?? defaultVertBar;
        var diagBarType = ResolveBarType(doc, request.OpeningDiagonalBarTypeName, defaultVertBar.Name) ?? defaultVertBar;
        var stirrupBarType = ResolveBarType(doc, request.OpeningStirrupBarTypeName, "3/8\"") ?? defaultHorizBar;

        int pCount = Math.Clamp(request.OpeningPerimeterBarsPerLayer, 1, 6);
        double perimSpacingFeet = UnitConverter.MetersToFeet(Math.Max(0.04, request.OpeningPerimeterSpacingMeters));
        double ldFeet = UnitConverter.MetersToFeet(Math.Max(0.15, request.OpeningPerimeterAnchorageMeters));
        double pArrayLength = (pCount > 1) ? (pCount - 1) * perimSpacingFeet : 0.0;

        double dPerim = GetBarDiameter(perimBarType);
        double rPerim = dPerim / 2.0;
        double dStir = GetBarDiameter(stirrupBarType);
        double rStir = dStir / 2.0;

        bool hasStir = request.IncludeOpeningEdgeStirrups;
        bool hasPerim = request.IncludeOpeningPerimeterBars;

        int diagBarsPerCorner = Math.Clamp(request.OpeningDiagonalBarsPerCorner, 1, 4);
        double diagTotalLenFeet = UnitConverter.MetersToFeet(Math.Max(0.30, request.OpeningDiagonalLengthMeters));
        double diagHalfProj = (diagTotalLenFeet * 0.5) * 0.70710678;
        double diagStepFeet = UnitConverter.MetersToFeet(0.06);

        double stirrupSpacingFeet = UnitConverter.MetersToFeet(Math.Max(0.05, request.OpeningStirrupSpacingMeters));

        // Resolver forma de estribo sísmico (T1, 51, etc. que incorpora ganchos a 135°)
        var seismicStirrupShape = ResolveSeismicStirrupShape(doc);

        // Verificar orientación del sistema para que el array de estribos crezca siempre en sentido positivo (+Z y +vDir)
        bool isRightHandedZ = vDir.CrossProduct(nVec).DotProduct(XYZ.BasisZ) >= 0.0;

        // Resolver gancho sísmico a 135° para estribos de abertura
        var allHooks = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .ToList();

        var stirrupHook135 = allHooks.FirstOrDefault(h => (h.Name.Contains("135") || Math.Abs(h.HookAngle - 2.356) < 0.1) && h.Style == RebarStyle.StirrupTie)
                           ?? allHooks.FirstOrDefault(h => h.Name.Contains("135") || Math.Abs(h.HookAngle - 2.356) < 0.1)
                           ?? allHooks.FirstOrDefault(h => h.Style == RebarStyle.StirrupTie)
                           ?? allHooks.FirstOrDefault();

        // Dimensiones transversales en el espesor del muro
        double tieDepth = Math.Max(0.12, wallThick - 2.0 * effectiveCoverFeet - dStir);
        double halfD = tieDepth / 2.0;

        // Ubicación en espesor (T) para que las barras longitudinales queden dentro del estribo
        double tBarPos, tBarNeg;
        if (hasStir)
        {
            tBarPos = Math.Max(0.0, halfD - rStir - rPerim);
            tBarNeg = -tBarPos;
        }
        else
        {
            tBarPos = layerOffsets[0].VerticalOffset;
            tBarNeg = (layerOffsets.Count > 1) ? layerOffsets[1].VerticalOffset : tBarPos;
        }

        var perimFaces = (layerOffsets.Count > 1) ? new[] { tBarPos, tBarNeg } : new[] { 0.0 };

        // Desfase en espesor (T) hacia el interior del muro para que las diagonales queden detrás
        // del refuerzo longitudinal perimetral, eliminando interferencias en 3D
        double dDiag = GetBarDiameter(diagBarType);
        double rDiag = dDiag / 2.0;
        double tDiagPos = Math.Max(0.015, tBarPos - rPerim - rDiag - 0.015);
        double tDiagNeg = -tDiagPos;
        var diagFaces = (layerOffsets.Count > 1)
            ? new[] { tDiagPos, tDiagNeg }
            : new[] { (layerOffsets.Count == 1 && layerOffsets[0].VerticalOffset != 0) ? (layerOffsets[0].VerticalOffset - Math.Sign(layerOffsets[0].VerticalOffset) * (rPerim + rDiag + 0.015)) : (rPerim + rDiag + 0.015) };

        foreach (var op in geom.Openings)
        {
            bool hasBottomEdge = (op.ZMinFeet - zMin) > (currentVBot + 0.25);
            bool hasTopEdge = (zMax - op.ZMaxFeet) > (currentVTop + 0.25);
            bool hasLeftEdge = op.SMinFeet > (currentHStart + 0.25);
            bool hasRightEdge = (wallLen - op.SMaxFeet) > (currentHEnd + 0.25);

            // Alturas y longitudes de anclaje
            double zPerimBot = Math.Max(zMinWall, hasBottomEdge ? (op.ZMinFeet - ldFeet) : zMinWall);
            double zPerimTop = Math.Min(zMaxWall, hasTopEdge ? (op.ZMaxFeet + ldFeet) : zMaxWall);
            double sPerimLeft = Math.Max(sMinWall, hasLeftEdge ? (op.SMinFeet - ldFeet) : sMinWall);
            double sPerimRight = Math.Min(sMaxWall, hasRightEdge ? (op.SMaxFeet + ldFeet) : sMaxWall);

            double jambZ0 = Math.Max(zMinWall, op.ZMinFeet);
            double jambZ1 = Math.Min(zMaxWall, op.ZMaxFeet);
            double jambArrayHeight = jambZ1 - jambZ0;

            double lintelS0 = Math.Max(sMinWall, op.SMinFeet);
            double lintelS1 = Math.Min(sMaxWall, op.SMaxFeet);
            double lintelSpan = lintelS1 - lintelS0;

            // -----------------------------------------------------------------
            // 1. LATERAL IZQUIERDO (Borde vertical izquierdo del vano)
            // -----------------------------------------------------------------
            if (hasLeftEdge)
            {
                double sInner = op.SMinFeet - effectiveCoverFeet;
                double tieWidth = Math.Max(0.35, pArrayLength + 2.0 * (rPerim + rStir));
                double halfW = tieWidth / 2.0;
                double sCenter = (sInner - (hasStir ? rStir : 0.0)) - halfW;
                double sFirst = hasStir ? (sCenter + halfW - rStir - rPerim) : (sInner - rPerim);

                // Estribos cerrados verticales
                if (hasStir && jambArrayHeight >= 0.20)
                {
                    CreateOpeningVerticalStirrups(doc, wall, pointAt, sCenter, halfW, halfD, jambZ0, jambZ1,
                        vDir, nVec, isRightHandedZ, seismicStirrupShape, stirrupBarType, stirrupSpacingFeet, stirrupHook135, view3D, summary);
                }

                // Barras longitudinales como conjunto 'Número Fijo' (Fixed Number)
                if (hasPerim && (zPerimTop - zPerimBot) >= 0.25)
                {
                    foreach (double tFace in perimFaces)
                    {
                        var p0 = pointAt(sFirst, tFace, zPerimBot);
                        var p1 = pointAt(sFirst, tFace, zPerimTop);
                        var r = CreateRebarSafe(doc, RebarStyle.Standard, perimBarType, null, null, wall, -vDir, new List<Curve> { Line.CreateBound(p0, p1) });
                        if (r != null)
                        {
                            if (pCount > 1 && pArrayLength > 0.005)
                            {
                                try
                                {
                                    var acc = r.GetShapeDrivenAccessor();
                                    acc.SetLayoutAsFixedNumber(pCount, pArrayLength, true, true, true);
                                }
                                catch { }
                            }
                            summary.RebarElementIds.Add(r.Id.Value);
                            totalVertBars += r.Quantity;
                            if (view3D != null) { try { r.SetUnobscuredInView(view3D, true); } catch { } }
                        }
                    }
                }
            }

            // -----------------------------------------------------------------
            // 2. LATERAL DERECHO (Borde vertical derecho del vano)
            // -----------------------------------------------------------------
            if (hasRightEdge)
            {
                double sInner = op.SMaxFeet + effectiveCoverFeet;
                double tieWidth = Math.Max(0.35, pArrayLength + 2.0 * (rPerim + rStir));
                double halfW = tieWidth / 2.0;
                double sCenter = (sInner + (hasStir ? rStir : 0.0)) + halfW;
                double sFirst = hasStir ? (sCenter - halfW + rStir + rPerim) : (sInner + rPerim);

                // Estribos cerrados verticales
                if (hasStir && jambArrayHeight >= 0.20)
                {
                    CreateOpeningVerticalStirrups(doc, wall, pointAt, sCenter, halfW, halfD, jambZ0, jambZ1,
                        vDir, nVec, isRightHandedZ, seismicStirrupShape, stirrupBarType, stirrupSpacingFeet, stirrupHook135, view3D, summary);
                }

                // Barras longitudinales como conjunto 'Número Fijo' (Fixed Number)
                if (hasPerim && (zPerimTop - zPerimBot) >= 0.25)
                {
                    foreach (double tFace in perimFaces)
                    {
                        var p0 = pointAt(sFirst, tFace, zPerimBot);
                        var p1 = pointAt(sFirst, tFace, zPerimTop);
                        var r = CreateRebarSafe(doc, RebarStyle.Standard, perimBarType, null, null, wall, vDir, new List<Curve> { Line.CreateBound(p0, p1) });
                        if (r != null)
                        {
                            if (pCount > 1 && pArrayLength > 0.005)
                            {
                                try
                                {
                                    var acc = r.GetShapeDrivenAccessor();
                                    acc.SetLayoutAsFixedNumber(pCount, pArrayLength, true, true, true);
                                }
                                catch { }
                            }
                            summary.RebarElementIds.Add(r.Id.Value);
                            totalVertBars += r.Quantity;
                            if (view3D != null) { try { r.SetUnobscuredInView(view3D, true); } catch { } }
                        }
                    }
                }
            }

            // -----------------------------------------------------------------
            // 3. DINTEL SUPERIOR (Borde horizontal superior del vano)
            // -----------------------------------------------------------------
            if (hasTopEdge)
            {
                double zInner = op.ZMaxFeet + effectiveCoverFeet;
                double tieHeight = Math.Max(0.35, pArrayLength + 2.0 * (rPerim + rStir));
                double halfH = tieHeight / 2.0;
                double zCenter = (zInner + (hasStir ? rStir : 0.0)) + halfH;
                double zFirst = hasStir ? (zCenter - halfH + rStir + rPerim) : (zInner + rPerim);

                // Estribos cerrados en dintel
                if (hasStir && lintelSpan >= 0.20)
                {
                    CreateOpeningHorizontalStirrups(doc, wall, pointAt, lintelS0, lintelS1, zCenter, halfH, halfD,
                        vDir, nVec, isRightHandedZ, seismicStirrupShape, stirrupBarType, stirrupSpacingFeet, stirrupHook135, view3D, summary);
                }

                // Barras longitudinales como conjunto 'Número Fijo' (Fixed Number)
                if (hasPerim && (sPerimRight - sPerimLeft) >= 0.25)
                {
                    foreach (double tFace in perimFaces)
                    {
                        var p0 = pointAt(sPerimLeft, tFace, zFirst);
                        var p1 = pointAt(sPerimRight, tFace, zFirst);
                        var r = CreateRebarSafe(doc, RebarStyle.Standard, perimBarType, null, null, wall, XYZ.BasisZ, new List<Curve> { Line.CreateBound(p0, p1) });
                        if (r != null)
                        {
                            if (pCount > 1 && pArrayLength > 0.005)
                            {
                                try
                                {
                                    var acc = r.GetShapeDrivenAccessor();
                                    acc.SetLayoutAsFixedNumber(pCount, pArrayLength, true, true, true);
                                }
                                catch { }
                            }
                            summary.RebarElementIds.Add(r.Id.Value);
                            totalHorizBars += r.Quantity;
                            if (view3D != null) { try { r.SetUnobscuredInView(view3D, true); } catch { } }
                        }
                    }
                }
            }

            // -----------------------------------------------------------------
            // 4. ALFÉIZAR INFERIOR (Borde horizontal inferior, vanos que no llegan al piso)
            // -----------------------------------------------------------------
            if (hasBottomEdge)
            {
                double zInner = op.ZMinFeet - effectiveCoverFeet;
                double tieHeight = Math.Max(0.35, pArrayLength + 2.0 * (rPerim + rStir));
                double halfH = tieHeight / 2.0;
                double zCenter = (zInner - (hasStir ? rStir : 0.0)) - halfH;
                double zFirst = hasStir ? (zCenter + halfH - rStir - rPerim) : (zInner - rPerim);

                // Estribos cerrados en alféizar
                if (hasStir && lintelSpan >= 0.20)
                {
                    CreateOpeningHorizontalStirrups(doc, wall, pointAt, lintelS0, lintelS1, zCenter, halfH, halfD,
                        vDir, nVec, isRightHandedZ, seismicStirrupShape, stirrupBarType, stirrupSpacingFeet, stirrupHook135, view3D, summary);
                }

                // Barras longitudinales como conjunto 'Número Fijo' (Fixed Number)
                if (hasPerim && (sPerimRight - sPerimLeft) >= 0.25)
                {
                    foreach (double tFace in perimFaces)
                    {
                        var p0 = pointAt(sPerimLeft, tFace, zFirst);
                        var p1 = pointAt(sPerimRight, tFace, zFirst);
                        var r = CreateRebarSafe(doc, RebarStyle.Standard, perimBarType, null, null, wall, -XYZ.BasisZ, new List<Curve> { Line.CreateBound(p0, p1) });
                        if (r != null)
                        {
                            if (pCount > 1 && pArrayLength > 0.005)
                            {
                                try
                                {
                                    var acc = r.GetShapeDrivenAccessor();
                                    acc.SetLayoutAsFixedNumber(pCount, pArrayLength, true, true, true);
                                }
                                catch { }
                            }
                            summary.RebarElementIds.Add(r.Id.Value);
                            totalHorizBars += r.Quantity;
                            if (view3D != null) { try { r.SetUnobscuredInView(view3D, true); } catch { } }
                        }
                    }
                }
            }

            // -----------------------------------------------------------------
            // 5. REFUERZO DIAGONAL A 45° EN ESQUINAS DEL VANO (Regla Número Fijo)
            // -----------------------------------------------------------------
            if (request.IncludeOpeningDiagonalBars)
            {
                double cornerClearance = effectiveCoverFeet + 0.08;

                foreach (double tDiag in diagFaces)
                {
                    // Esquina Superior-Izquierda (inclinada / de abajo-izquierda a arriba-derecha)
                    if (hasTopEdge && hasLeftEdge)
                    {
                        double sC = op.SMinFeet - cornerClearance;
                        double zC = op.ZMaxFeet + cornerClearance;
                        XYZ normArray = (-vDir + XYZ.BasisZ).Normalize();
                        CreateClippedDiagonalBar(doc, wall, pointAt, diagBarType, normArray,
                            diagBarsPerCorner, diagStepFeet,
                            sC - diagHalfProj, zC - diagHalfProj,
                            sC + diagHalfProj, zC + diagHalfProj,
                            sMinWall, sMaxWall, zMinWall, zMaxWall,
                            tDiag, view3D, summary);
                    }

                    // Esquina Superior-Derecha (inclinada \ de arriba-izquierda a abajo-derecha)
                    if (hasTopEdge && hasRightEdge)
                    {
                        double sC = op.SMaxFeet + cornerClearance;
                        double zC = op.ZMaxFeet + cornerClearance;
                        XYZ normArray = (vDir + XYZ.BasisZ).Normalize();
                        CreateClippedDiagonalBar(doc, wall, pointAt, diagBarType, normArray,
                            diagBarsPerCorner, diagStepFeet,
                            sC - diagHalfProj, zC + diagHalfProj,
                            sC + diagHalfProj, zC - diagHalfProj,
                            sMinWall, sMaxWall, zMinWall, zMaxWall,
                            tDiag, view3D, summary);
                    }

                    // Esquina Inferior-Izquierda (inclinada \ de arriba-izquierda a abajo-derecha)
                    if (hasBottomEdge && hasLeftEdge)
                    {
                        double sC = op.SMinFeet - cornerClearance;
                        double zC = op.ZMinFeet - cornerClearance;
                        XYZ normArray = (-vDir - XYZ.BasisZ).Normalize();
                        CreateClippedDiagonalBar(doc, wall, pointAt, diagBarType, normArray,
                            diagBarsPerCorner, diagStepFeet,
                            sC - diagHalfProj, zC + diagHalfProj,
                            sC + diagHalfProj, zC - diagHalfProj,
                            sMinWall, sMaxWall, zMinWall, zMaxWall,
                            tDiag, view3D, summary);
                    }

                    // Esquina Inferior-Derecha (inclinada / de abajo-izquierda a arriba-derecha)
                    if (hasBottomEdge && hasRightEdge)
                    {
                        double sC = op.SMaxFeet + cornerClearance;
                        double zC = op.ZMinFeet - cornerClearance;
                        XYZ normArray = (vDir - XYZ.BasisZ).Normalize();
                        CreateClippedDiagonalBar(doc, wall, pointAt, diagBarType, normArray,
                            diagBarsPerCorner, diagStepFeet,
                            sC - diagHalfProj, zC - diagHalfProj,
                            sC + diagHalfProj, zC + diagHalfProj,
                            sMinWall, sMaxWall, zMinWall, zMaxWall,
                            tDiag, view3D, summary);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Crea un conjunto de estribos cerrados verticales en los bordes laterales del vano
    /// garantizando ganchos sísmicos a 135° mediante RebarShape nativo (T1/51).
    /// </summary>
    private static void CreateOpeningVerticalStirrups(
        Document doc,
        Wall wall,
        Func<double, double, double, XYZ> pointAt,
        double sCenter,
        double halfW,
        double halfD,
        double zBot,
        double zTop,
        XYZ vDir,
        XYZ nVec,
        bool isRightHandedZ,
        RebarShape? stirrupShape,
        RebarBarType tieBarType,
        double tieSpacing,
        RebarHookType? stirrupHook,
        View3D? view3D,
        WallRebarCreatedSummary summary)
    {
        try
        {
            double arrayHeight = Math.Max(0.2, zTop - zBot);
            double width = 2.0 * halfW;
            double depth = 2.0 * halfD;

            XYZ origin;
            XYZ xVec;
            XYZ yVec;
            XYZ boxVecX;
            XYZ boxVecY;
            double dimA;
            double dimB;

            if (isRightHandedZ)
            {
                origin = pointAt(sCenter - halfW, -halfD, zBot);
                xVec = vDir;
                yVec = nVec;
                boxVecX = xVec * width;
                boxVecY = yVec * depth;
                dimA = width;
                dimB = depth;
            }
            else
            {
                origin = pointAt(sCenter - halfW, -halfD, zBot);
                xVec = nVec;
                yVec = vDir;
                boxVecX = xVec * depth;
                boxVecY = yVec * width;
                dimA = depth;
                dimB = width;
            }

            Rebar? tieRebar = null;

            // 1. Intentar creación nativa con RebarShape de estribo sísmico (T1/51 con ganchos a 135°)
            if (stirrupShape != null)
            {
                try
                {
                    tieRebar = Rebar.CreateFromRebarShape(doc, stirrupShape, tieBarType, wall, origin, xVec, yVec);
                    if (tieRebar != null)
                    {
                        ApplyStirrupDimensions(tieRebar, origin, boxVecX, boxVecY, dimA, dimB);
                    }
                }
                catch { }
            }

            // 2. Fallback con curvas si no hay shape o falló
            if (tieRebar == null)
            {
                var c1 = pointAt(sCenter - halfW, -halfD, zBot);
                var c2 = pointAt(sCenter + halfW, -halfD, zBot);
                var c3 = pointAt(sCenter + halfW, +halfD, zBot);
                var c4 = pointAt(sCenter - halfW, +halfD, zBot);

                var curves = new List<Curve>
                {
                    Line.CreateBound(c1, c2),
                    Line.CreateBound(c2, c3),
                    Line.CreateBound(c3, c4),
                    Line.CreateBound(c4, c1)
                };

                RebarStyle tieStyle = stirrupHook?.Style ?? RebarStyle.StirrupTie;
                tieRebar = CreateRebarSafe(
                    doc,
                    tieStyle,
                    tieBarType,
                    stirrupHook,
                    stirrupHook,
                    wall,
                    XYZ.BasisZ,
                    curves);
            }

            if (tieRebar != null)
            {
                // Asegurar asignación explícita del gancho a 135° en ambos extremos
                if (stirrupHook != null)
                {
                    try { tieRebar.SetHookTypeId(0, stirrupHook.Id); } catch { }
                    try { tieRebar.SetHookTypeId(1, stirrupHook.Id); } catch { }
                    try { tieRebar.SetHookOrientation(0, RebarHookOrientation.Left); } catch { }
                    try { tieRebar.SetHookOrientation(1, RebarHookOrientation.Right); } catch { }
                }

                try
                {
                    var acc = tieRebar.GetShapeDrivenAccessor();
                    acc.SetLayoutAsMaximumSpacing(tieSpacing, arrayHeight, true, true, true);
                }
                catch { }

                summary.RebarElementIds.Add(tieRebar.Id.Value);
                if (view3D != null) { try { tieRebar.SetUnobscuredInView(view3D, true); } catch { } }
            }
        }
        catch { }
    }

    /// <summary>
    /// Crea un conjunto de estribos cerrados horizontales en el dintel o alféizar del vano
    /// garantizando ganchos sísmicos a 135° mediante RebarShape nativo (T1/51).
    /// </summary>
    private static void CreateOpeningHorizontalStirrups(
        Document doc,
        Wall wall,
        Func<double, double, double, XYZ> pointAt,
        double sStart,
        double sEnd,
        double zCenter,
        double halfH,
        double halfD,
        XYZ vDir,
        XYZ nVec,
        bool isRightHandedZ,
        RebarShape? stirrupShape,
        RebarBarType tieBarType,
        double tieSpacing,
        RebarHookType? stirrupHook,
        View3D? view3D,
        WallRebarCreatedSummary summary)
    {
        try
        {
            double arraySpan = Math.Max(0.2, sEnd - sStart);
            double depth = 2.0 * halfD;
            double height = 2.0 * halfH;

            XYZ origin;
            XYZ xVec;
            XYZ yVec;
            XYZ boxVecX;
            XYZ boxVecY;

            if (isRightHandedZ)
            {
                origin = pointAt(sStart, -halfD, zCenter - halfH);
                xVec = nVec;
                yVec = XYZ.BasisZ;
                boxVecX = xVec * depth;
                boxVecY = yVec * height;
            }
            else
            {
                origin = pointAt(sStart, +halfD, zCenter - halfH);
                xVec = -nVec;
                yVec = XYZ.BasisZ;
                boxVecX = xVec * depth;
                boxVecY = yVec * height;
            }

            Rebar? tieRebar = null;

            // 1. Intentar creación nativa con RebarShape de estribo sísmico (T1/51 con ganchos a 135°)
            if (stirrupShape != null)
            {
                try
                {
                    tieRebar = Rebar.CreateFromRebarShape(doc, stirrupShape, tieBarType, wall, origin, xVec, yVec);
                    if (tieRebar != null)
                    {
                        ApplyStirrupDimensions(tieRebar, origin, boxVecX, boxVecY, depth, height);
                    }
                }
                catch { }
            }

            // 2. Fallback con curvas si no hay shape o falló
            if (tieRebar == null)
            {
                var c1 = pointAt(sStart, -halfD, zCenter - halfH);
                var c2 = pointAt(sStart, +halfD, zCenter - halfH);
                var c3 = pointAt(sStart, +halfD, zCenter + halfH);
                var c4 = pointAt(sStart, -halfD, zCenter + halfH);

                var curves = new List<Curve>
                {
                    Line.CreateBound(c1, c2),
                    Line.CreateBound(c2, c3),
                    Line.CreateBound(c3, c4),
                    Line.CreateBound(c4, c1)
                };

                RebarStyle tieStyle = stirrupHook?.Style ?? RebarStyle.StirrupTie;
                tieRebar = CreateRebarSafe(
                    doc,
                    tieStyle,
                    tieBarType,
                    stirrupHook,
                    stirrupHook,
                    wall,
                    vDir,
                    curves);
            }

            if (tieRebar != null)
            {
                // Asegurar asignación explícita del gancho a 135° en ambos extremos
                if (stirrupHook != null)
                {
                    try { tieRebar.SetHookTypeId(0, stirrupHook.Id); } catch { }
                    try { tieRebar.SetHookTypeId(1, stirrupHook.Id); } catch { }
                    try { tieRebar.SetHookOrientation(0, RebarHookOrientation.Left); } catch { }
                    try { tieRebar.SetHookOrientation(1, RebarHookOrientation.Right); } catch { }
                }

                try
                {
                    var acc = tieRebar.GetShapeDrivenAccessor();
                    acc.SetLayoutAsMaximumSpacing(tieSpacing, arraySpan, true, true, true);
                }
                catch { }

                summary.RebarElementIds.Add(tieRebar.Id.Value);
                if (view3D != null) { try { tieRebar.SetUnobscuredInView(view3D, true); } catch { } }
            }
        }
        catch { }
    }

    /// <summary>
    /// Crea un conjunto de barras diagonales a 45° con regla de 'Número Fijo' (Fixed Number)
    /// recortadas automáticamente contra los límites de recubrimiento del muro.
    /// </summary>
    private static void CreateClippedDiagonalBar(
        Document doc,
        Wall wall,
        Func<double, double, double, XYZ> pointAt,
        RebarBarType diagBarType,
        XYZ normArray,
        int count,
        double stepFeet,
        double s0,
        double z0,
        double s1,
        double z1,
        double sMin,
        double sMax,
        double zMin,
        double zMax,
        double tOffset,
        View3D? view3D,
        WallRebarCreatedSummary summary)
    {
        if (!ClipSegmentToRect(ref s0, ref z0, ref s1, ref z1, sMin, sMax, zMin, zMax))
            return;

        var pA = pointAt(s0, tOffset, z0);
        var pB = pointAt(s1, tOffset, z1);
        if (pA.DistanceTo(pB) < 0.25) return;

        var r = CreateRebarSafe(
            doc,
            RebarStyle.Standard,
            diagBarType,
            null,
            null,
            wall,
            normArray,
            new List<Curve> { Line.CreateBound(pA, pB) });

        if (r != null)
        {
            if (count > 1 && stepFeet > 0.005)
            {
                try
                {
                    double arrayLen = (count - 1) * stepFeet;
                    var acc = r.GetShapeDrivenAccessor();
                    acc.SetLayoutAsFixedNumber(count, arrayLen, true, true, true);
                }
                catch { }
            }

            summary.RebarElementIds.Add(r.Id.Value);
            if (view3D != null)
            {
                try { r.SetUnobscuredInView(view3D, true); } catch { }
            }
        }
    }

    /// <summary>
    /// Recorte paramétrico de Liang-Barsky para mantener cualquier barra dentro del contorno útil del muro.
    /// </summary>
    private static bool ClipSegmentToRect(
        ref double x0, ref double y0,
        ref double x1, ref double y1,
        double xMin, double xMax,
        double yMin, double yMax)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        double t0 = 0.0;
        double t1 = 1.0;

        bool ClipTest(double p, double q, ref double u0, ref double u1)
        {
            if (Math.Abs(p) < 1e-9)
                return q >= 0.0;
            double r = q / p;
            if (p < 0.0)
            {
                if (r > u1) return false;
                if (r > u0) u0 = r;
            }
            else
            {
                if (r < u0) return false;
                if (r < u1) u1 = r;
            }
            return true;
        }

        if (ClipTest(-dx, x0 - xMin, ref t0, ref t1) &&
            ClipTest(dx, xMax - x0, ref t0, ref t1) &&
            ClipTest(-dy, y0 - yMin, ref t0, ref t1) &&
            ClipTest(dy, yMax - y0, ref t0, ref t1))
        {
            if (t1 > t0)
            {
                double nx0 = x0 + t0 * dx;
                double ny0 = y0 + t0 * dy;
                double nx1 = x0 + t1 * dx;
                double ny1 = y0 + t1 * dy;
                x0 = nx0;
                y0 = ny0;
                x1 = nx1;
                y1 = ny1;
                return true;
            }
        }
        return false;
    }
}
