using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

public static class CreateCapitalRebarHandler
{
    public static CreateCapitalRebarResponse Execute(Document doc, CreateCapitalRebarRequest request)
    {
        var response = new CreateCapitalRebarResponse();
        var results = new List<CapitalRebarCreatedSummary>();

        if (request.ColumnElementIds == null || request.ColumnElementIds.Count == 0)
        {
            response.Success = false;
            response.Message = "No se proporcionaron IDs de capiteles (Structural Columns).";
            return response;
        }

        if (request.VerticalSpacingMeters <= 0 || request.StirrupSpacingMeters <= 0)
        {
            response.Success = false;
            response.Message = "Los espaciamientos deben ser mayores a cero.";
            return response;
        }

        var barTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .ToList();

        var hookTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .ToList();

        var rebarShapes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarShape))
            .Cast<RebarShape>()
            .ToList();

        var vertBarType = ResolveBarType(barTypes, request.VerticalBarTypeName, 12.7);
        var stirrupBarType = ResolveBarType(barTypes, request.StirrupBarTypeName, 9.5);
        if (vertBarType == null || stirrupBarType == null)
        {
            response.Success = false;
            response.Message = "No se encontraron tipos de barra (RebarBarType) en el proyecto.";
            return response;
        }

        var vertHookType = ResolveHookType(hookTypes, request.VerticalHookTypeName, RebarStyle.Standard, 90.0);
        var stirrupHookType = ResolveHookType(hookTypes, request.StirrupHookTypeName, RebarStyle.StirrupTie, 135.0);
        var stirrupShape = ResolveStirrupShape(rebarShapes, request.StirrupShapeName);

        var view3D = doc.ActiveView as View3D;

        using var tx = new Transaction(doc, "BIM Suite: Armado de Capiteles");
        var failOpts = tx.GetFailureHandlingOptions();
        failOpts.SetFailuresPreprocessor(new RebarFailuresPreprocessor());
        tx.SetFailureHandlingOptions(failOpts);
        tx.Start();

        try
        {
            foreach (long elemIdVal in request.ColumnElementIds)
            {
                var summary = new CapitalRebarCreatedSummary { ColumnElementId = elemIdVal };
                try
                {
                    var elem = doc.GetElement(new ElementId(elemIdVal));
                    if (elem is not FamilyInstance col ||
                        col.Category == null ||
                        col.Category.Id.Value != (long)BuiltInCategory.OST_StructuralColumns)
                    {
                        summary.Status = "Omitido";
                        summary.ErrorMessage = "El elemento no es una Columna Estructural (Structural Columns).";
                        results.Add(summary);
                        continue;
                    }

                    summary.ColumnName = col.Name;

                    var geom = CapitalGeometryHelper.ExtractGeometry(doc, col);
                    if (geom == null)
                    {
                        summary.Status = "Error";
                        summary.ErrorMessage = "No se pudo extraer la geometría sólida del capitel.";
                        results.Add(summary);
                        continue;
                    }

                    summary.WidthMeters = Math.Round(UnitConverter.FeetToMeters(geom.SizeXFeet), 3);
                    summary.DepthMeters = Math.Round(UnitConverter.FeetToMeters(geom.SizeYFeet), 3);
                    summary.HeightMeters = Math.Round(UnitConverter.FeetToMeters(geom.HeightFeet), 3);

                    double sideCover = request.UseElementCover
                        ? geom.CoverSidesFeet
                        : UnitConverter.MetersToFeet(request.CoverMeters);
                    double topCover = request.UseElementCover ? geom.CoverTopFeet : sideCover;
                    double botCover = request.UseElementCover ? geom.CoverBottomFeet : sideCover;

                    double dVert = vertBarType.BarNominalDiameter > 0
                        ? vertBarType.BarNominalDiameter
                        : vertBarType.BarModelDiameter;
                    double dStir = stirrupBarType.BarNominalDiameter > 0
                        ? stirrupBarType.BarNominalDiameter
                        : stirrupBarType.BarModelDiameter;

                    double stirrupBoxX = geom.SizeXFeet - 2.0 * sideCover;
                    double stirrupBoxY = geom.SizeYFeet - 2.0 * sideCover;
                    if (stirrupBoxX <= 2.0 * dStir + 2.0 * dVert + 0.05 ||
                        stirrupBoxY <= 2.0 * dStir + 2.0 * dVert + 0.05)
                    {
                        summary.Status = "Error";
                        summary.ErrorMessage = "Las dimensiones en planta del capitel son muy pequeñas para el recubrimiento y estribos.";
                        results.Add(summary);
                        continue;
                    }

                    double zBotBar = geom.ZMinFeet + botCover + 0.5 * dVert;
                    double zTopBar = geom.ZMaxFeet - topCover - 0.5 * dVert;
                    if (zTopBar <= zBotBar + dVert + dStir + 0.05)
                    {
                        summary.Status = "Error";
                        summary.ErrorMessage = "La altura libre del capitel es insuficiente para colocar barras verticales.";
                        results.Add(summary);
                        continue;
                    }

                    double zBotStir = geom.ZMinFeet + botCover + dVert + 0.5 * dStir;
                    double zTopStir = geom.ZMaxFeet - topCover - dVert - 0.5 * dStir;
                    if (zTopStir <= zBotStir + 0.02)
                    {
                        zBotStir = zBotBar + 0.5 * dStir;
                        zTopStir = zTopBar - 0.5 * dStir;
                    }

                    var createdRebars = new List<Rebar>();

                    // 1. Estribos cerrados con ganchos a 135°
                    double stirrupSpacingFeet = UnitConverter.MetersToFeet(request.StirrupSpacingMeters);
                    var stirrupRebar = CreateClosedStirrups(
                        doc,
                        col,
                        geom,
                        stirrupBarType,
                        stirrupShape,
                        stirrupHookType,
                        stirrupBoxX,
                        stirrupBoxY,
                        dStir,
                        zBotStir,
                        zTopStir,
                        stirrupSpacingFeet);

                    if (stirrupRebar != null)
                    {
                        createdRebars.Add(stirrupRebar);
                        summary.StirrupsCount += Math.Max(1, stirrupRebar.Quantity);
                    }

                    // 2. Barras verticales en las 4 caras con ganchos a 90° hacia el interior
                    double vertSpacingFeet = UnitConverter.MetersToFeet(request.VerticalSpacingMeters);
                    double halfInnerX = Math.Max(0.02, 0.5 * geom.SizeXFeet - sideCover - dStir - 0.5 * dVert);
                    double halfInnerY = Math.Max(0.02, 0.5 * geom.SizeYFeet - sideCover - dStir - 0.5 * dVert);

                    var c0 = geom.CenterXY - geom.DirX * halfInnerX - geom.DirY * halfInnerY;
                    var c1 = geom.CenterXY + geom.DirX * halfInnerX - geom.DirY * halfInnerY;
                    var c2 = geom.CenterXY + geom.DirX * halfInnerX + geom.DirY * halfInnerY;
                    var c3 = geom.CenterXY - geom.DirX * halfInnerX + geom.DirY * halfInnerY;

                    (XYZ startXY, XYZ endXY, XYZ inwardDir, bool includeFirst, bool includeLast)[] faces =
                    [
                        (c0, c1,  geom.DirY, true,  true),
                        (c1, c2, -geom.DirX, false, false),
                        (c2, c3, -geom.DirY, true,  true),
                        (c3, c0,  geom.DirX, false, false)
                    ];

                    foreach (var (startXY, endXY, inwardDir, includeFirst, includeLast) in faces)
                    {
                        var faceRebar = CreateFaceVerticalBars(
                            doc,
                            col,
                            vertBarType,
                            vertHookType,
                            startXY,
                            endXY,
                            inwardDir,
                            zBotBar,
                            zTopBar,
                            vertSpacingFeet,
                            includeFirst,
                            includeLast);

                        if (faceRebar != null)
                        {
                            createdRebars.Add(faceRebar);
                            summary.VerticalBarsCount += Math.Max(1, faceRebar.Quantity);
                        }
                    }

                    foreach (var r in createdRebars)
                    {
                        summary.RebarElementIds.Add(r.Id.Value);
                        if (view3D != null)
                        {
                            try { r.SetUnobscuredInView(view3D, true); } catch { }
                        }
                    }

                    summary.TotalRebarElementsCount = createdRebars.Count;
                    summary.Status = createdRebars.Count > 0 ? "OK" : "Error";
                    if (createdRebars.Count == 0)
                        summary.ErrorMessage = "No se generaron elementos Rebar para el capitel.";

                    results.Add(summary);
                }
                catch (Exception exElem)
                {
                    summary.Status = "Error";
                    summary.ErrorMessage = exElem.Message;
                    results.Add(summary);
                }
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            if (tx.HasStarted()) tx.RollBack();
            response.Success = false;
            response.Message = $"Error en transacción: {ex.Message}";
            return response;
        }

        response.Results = results;
        response.TotalCapitalsProcessed = results.Count(r => r.Status == "OK");
        response.Success = response.TotalCapitalsProcessed > 0;
        response.Message = response.Success
            ? $"Se armaron {response.TotalCapitalsProcessed} de {results.Count} capitel(es) correctamente."
            : (results.FirstOrDefault()?.ErrorMessage ?? "No se pudo armar ningún capitel.");

        return response;
    }

    private static Rebar? CreateFaceVerticalBars(
        Document doc,
        FamilyInstance host,
        RebarBarType barType,
        RebarHookType? hook90,
        XYZ startXY,
        XYZ endXY,
        XYZ inwardDir,
        double zBotBar,
        double zTopBar,
        double vertSpacingFeet,
        bool includeFirstBar,
        bool includeLastBar)
    {
        XYZ faceVec = endXY - startXY;
        double faceLen = faceVec.GetLength();
        if (faceLen <= 1e-4) return null;

        // Si ambos extremos están apagados y el tramo cabe en un solo espaciamiento,
        // las barras de esquina adyacentes ya cubren este lado sin requerir intermedias.
        if (!includeFirstBar && !includeLastBar && faceLen <= vertSpacingFeet + 1e-4)
            return null;

        XYZ faceDir = faceVec / faceLen;

        // Place initial single bar at start corner; normal = faceDir so both the bar
        // (along BasisZ) and inward 90° hooks (along ±inwardDir) lie in the plane
        // perpendicular to faceDir, and the array distributes along +faceDir.
        // Con normal = faceDir y tangente = +BasisZ, Left apunta hacia (faceDir × BasisZ).
        XYZ hookLeftDir = faceDir.CrossProduct(XYZ.BasisZ);
        var hookOrient = hookLeftDir.DotProduct(inwardDir) >= 0
            ? RebarHookOrientation.Left
            : RebarHookOrientation.Right;

        var pBot = new XYZ(startXY.X, startXY.Y, zBotBar);
        var pTop = new XYZ(startXY.X, startXY.Y, zTopBar);
        var line = Line.CreateBound(pBot, pTop);

        Rebar? rebar = Rebar.CreateFromCurves(
            doc,
            RebarStyle.Standard,
            barType,
            hook90,
            hook90,
            host,
            faceDir,
            new List<Curve> { line },
            hookOrient,
            hookOrient,
            true,
            true);

        if (rebar == null) return null;

        if (hook90 != null)
        {
            if (!EnsureBothHooksPointInward(doc, rebar, inwardDir))
            {
                // Si RebarShapeDefinesHooks ignora SetHookOrientation, invertir tangente de la barra
                // para asegurar geométricamente que ambos ganchos queden hacia el interior.
                var altLine = Line.CreateBound(pTop, pBot);
                var altLeftDir = faceDir.CrossProduct(-XYZ.BasisZ);
                var altOrient = altLeftDir.DotProduct(inwardDir) >= 0
                    ? RebarHookOrientation.Left
                    : RebarHookOrientation.Right;

                var altRebar = Rebar.CreateFromCurves(
                    doc,
                    RebarStyle.Standard,
                    barType,
                    hook90,
                    hook90,
                    host,
                    faceDir,
                    new List<Curve> { altLine },
                    altOrient,
                    altOrient,
                    true,
                    true);

                if (altRebar != null)
                {
                    if (EnsureBothHooksPointInward(doc, altRebar, inwardDir))
                    {
                        doc.Delete(rebar.Id);
                        rebar = altRebar;
                    }
                    else
                    {
                        doc.Delete(altRebar.Id);
                    }
                }
            }
        }

        var accessor = rebar.GetShapeDrivenAccessor();
        try
        {
            accessor.SetLayoutAsMaximumSpacing(vertSpacingFeet, faceLen, true, true, true);
        }
        catch
        {
            int count = Math.Max(2, (int)Math.Ceiling(faceLen / vertSpacingFeet) + 1);
            accessor.SetLayoutAsNumberWithSpacing(count, faceLen / (count - 1), true, true, true);
        }

        doc.Regenerate();

        // Con ambos extremos apagados se requieren al menos 3 posiciones para que exista >= 1 barra intermedia
        if (!includeFirstBar && !includeLastBar && rebar.NumberOfBarPositions < 3)
        {
            doc.Delete(rebar.Id);
            return null;
        }

        ApplyEndBarInclusion(doc, rebar, includeFirstBar, includeLastBar);

        if (rebar.Quantity <= 0)
        {
            doc.Delete(rebar.Id);
            return null;
        }

        return rebar;
    }

    private static void ApplyEndBarInclusion(
        Document doc, Rebar rebar, bool includeFirstBar, bool includeLastBar)
    {
        (Func<bool> getProp, Action<bool> setProp, BuiltInParameter bip, bool desired)[] targets =
        [
            (() => rebar.IncludeFirstBar, v => rebar.IncludeFirstBar = v, BuiltInParameter.REBAR_INCLUDE_FIRST_BAR, includeFirstBar),
            (() => rebar.IncludeLastBar,  v => rebar.IncludeLastBar = v,  BuiltInParameter.REBAR_INCLUDE_LAST_BAR,  includeLastBar)
        ];

        bool changed = false;
        foreach (var (getProp, setProp, _, desired) in targets)
        {
            try
            {
                if (getProp() != desired)
                {
                    setProp(desired);
                    changed = true;
                }
            }
            catch { }
        }

        if (changed)
            doc.Regenerate();

        bool paramChanged = false;
        foreach (var (getProp, _, bip, desired) in targets)
        {
            try
            {
                if (getProp() != desired)
                {
                    var p = rebar.get_Parameter(bip);
                    if (p != null && !p.IsReadOnly)
                    {
                        p.Set(desired ? 1 : 0);
                        paramChanged = true;
                    }
                }
            }
            catch { }
        }

        if (paramChanged)
            doc.Regenerate();
    }

    private static bool EnsureBothHooksPointInward(Document doc, Rebar rebar, XYZ inwardDir)
    {
        doc.Regenerate();

        if (!TryInspectHooksDirection(rebar, inwardDir, out bool startOutward, out bool endOutward))
            return true;

        if (!startOutward && !endOutward)
            return true;

        if (startOutward)
            rebar.SetHookOrientation(0, FlipHookOrientation(rebar.GetHookOrientation(0)));
        if (endOutward)
            rebar.SetHookOrientation(1, FlipHookOrientation(rebar.GetHookOrientation(1)));

        doc.Regenerate();

        if (!TryInspectHooksDirection(rebar, inwardDir, out bool stillStartOutward, out bool stillEndOutward))
            return false;

        return !stillStartOutward && !stillEndOutward;
    }

    private static bool TryInspectHooksDirection(
        Rebar rebar, XYZ inwardDir, out bool startOutward, out bool endOutward)
    {
        startOutward = false;
        endOutward = false;

        IList<Curve>? curves = null;
        try
        {
            curves = rebar.GetCenterlineCurves(
                false,
                false,
                false,
                MultiplanarOption.IncludeOnlyPlanarCurves,
                0);
        }
        catch
        {
            return false;
        }

        if (curves == null || curves.Count < 2) return false;

        Curve? shaft = null;
        double maxDz = -1.0;
        foreach (var c in curves)
        {
            double dz = Math.Abs(c.GetEndPoint(1).Z - c.GetEndPoint(0).Z);
            if (dz > maxDz)
            {
                maxDz = dz;
                shaft = c;
            }
        }

        if (shaft == null) return false;

        XYZ shaftMid = 0.5 * (shaft.GetEndPoint(0) + shaft.GetEndPoint(1));
        XYZ startTip = curves[0].GetEndPoint(0);
        XYZ endTip = curves[^1].GetEndPoint(1);

        var startVecXY = new XYZ(startTip.X - shaftMid.X, startTip.Y - shaftMid.Y, 0.0);
        var endVecXY = new XYZ(endTip.X - shaftMid.X, endTip.Y - shaftMid.Y, 0.0);

        startOutward = startVecXY.GetLength() > 1e-4 && startVecXY.DotProduct(inwardDir) < -1e-4;
        endOutward = endVecXY.GetLength() > 1e-4 && endVecXY.DotProduct(inwardDir) < -1e-4;
        return true;
    }

    private static Rebar? CreateClosedStirrups(
        Document doc,
        FamilyInstance host,
        CapitalGeometryInfo geom,
        RebarBarType barType,
        RebarShape? stirrupShape,
        RebarHookType? hook135,
        double boxX,
        double boxY,
        double dStir,
        double zBotStir,
        double zTopStir,
        double spacingFeet)
    {
        Rebar? rebar = null;
        XYZ origin = geom.CenterXY
            - geom.DirX * (0.5 * boxX)
            - geom.DirY * (0.5 * boxY)
            + XYZ.BasisZ * zBotStir;

        if (stirrupShape != null)
        {
            try
            {
                rebar = Rebar.CreateFromRebarShape(
                    doc,
                    stirrupShape,
                    barType,
                    host,
                    origin,
                    geom.DirX,
                    geom.DirY);

                if (rebar != null)
                {
                    var accessor = rebar.GetShapeDrivenAccessor();
                    bool scaled = false;
                    try
                    {
                        accessor.ScaleToBox(origin, geom.DirX * boxX, geom.DirY * boxY);
                        scaled = true;
                    }
                    catch
                    {
                        scaled = TrySetRectangularShapeDimensions(rebar, boxX, boxY);
                    }

                    if (!scaled)
                    {
                        doc.Delete(rebar.Id);
                        rebar = null;
                    }
                    else if (hook135 != null)
                    {
                        TryAssignClosedStirrupHooks(doc, rebar, hook135, geom.CenterXY);
                    }
                }
            }
            catch
            {
                if (rebar != null)
                {
                    try { doc.Delete(rebar.Id); } catch { }
                    rebar = null;
                }
            }
        }

        if (rebar == null)
        {
            rebar = CreateClosedStirrupFromCurves(
                doc,
                host,
                barType,
                hook135,
                geom.CenterXY,
                geom.DirX,
                geom.DirY,
                boxX,
                boxY,
                dStir,
                zBotStir);
        }

        if (rebar != null)
        {
            double spanZ = Math.Max(0.05, zTopStir - zBotStir);
            var accessor = rebar.GetShapeDrivenAccessor();
            accessor.SetLayoutAsMaximumSpacing(spacingFeet, spanZ, true, true, true);
        }

        return rebar;
    }

    private static Rebar? CreateClosedStirrupFromCurves(
        Document doc,
        FamilyInstance host,
        RebarBarType barType,
        RebarHookType? hook135,
        XYZ centerXY,
        XYZ dirX,
        XYZ dirY,
        double boxX,
        double boxY,
        double dStir,
        double zPlane)
    {
        double hx = Math.Max(0.02, 0.5 * boxX - 0.5 * dStir);
        double hy = Math.Max(0.02, 0.5 * boxY - 0.5 * dStir);

        XYZ p0 = new XYZ(centerXY.X, centerXY.Y, zPlane) - dirX * hx - dirY * hy;
        XYZ p1 = new XYZ(centerXY.X, centerXY.Y, zPlane) + dirX * hx - dirY * hy;
        XYZ p2 = new XYZ(centerXY.X, centerXY.Y, zPlane) + dirX * hx + dirY * hy;
        XYZ p3 = new XYZ(centerXY.X, centerXY.Y, zPlane) - dirX * hx + dirY * hy;

        var curves = new List<Curve>
        {
            Line.CreateBound(p0, p1),
            Line.CreateBound(p1, p2),
            Line.CreateBound(p2, p3),
            Line.CreateBound(p3, p0)
        };

        var rebar = Rebar.CreateFromCurves(
            doc,
            RebarStyle.StirrupTie,
            barType,
            hook135,
            hook135,
            host,
            XYZ.BasisZ,
            curves,
            RebarHookOrientation.Left,
            RebarHookOrientation.Left,
            true,
            true);

        if (rebar != null && hook135 != null)
        {
            EnsureClosedStirrupHooksPointInward(doc, rebar, centerXY);
        }

        return rebar;
    }

    private static void TryAssignClosedStirrupHooks(Document doc, Rebar rebar, RebarHookType hook135, XYZ centerXY)
    {
        try
        {
            if (rebar.GetHookTypeId(0) == ElementId.InvalidElementId)
                rebar.SetHookTypeId(0, hook135.Id);
            if (rebar.GetHookTypeId(1) == ElementId.InvalidElementId)
                rebar.SetHookTypeId(1, hook135.Id);

            EnsureClosedStirrupHooksPointInward(doc, rebar, centerXY);
        }
        catch { }
    }

    private static void EnsureClosedStirrupHooksPointInward(Document doc, Rebar rebar, XYZ centerXY)
    {
        doc.Regenerate();

        IList<Curve>? curves = null;
        try
        {
            curves = rebar.GetCenterlineCurves(
                false,
                false,
                false,
                MultiplanarOption.IncludeOnlyPlanarCurves,
                0);
        }
        catch
        {
            return;
        }

        if (curves == null || curves.Count < 4) return;

        // First segment starts at start hook tip; last segment ends at end hook tip.
        // Vector from hook tip toward centerXY? Wait: a 135° seismic hook starts at an interior tip
        // and runs outward to the corner; so tip must be CLOSER to centerXY than the corner point!
        XYZ startTip = curves[0].GetEndPoint(0);
        XYZ startOther = curves[0].GetEndPoint(1);
        XYZ endTip = curves[^1].GetEndPoint(1);
        XYZ endOther = curves[^1].GetEndPoint(0);

        double dStartTip = HorizontalDistSq(startTip, centerXY);
        double dStartOther = HorizontalDistSq(startOther, centerXY);
        double dEndTip = HorizontalDistSq(endTip, centerXY);
        double dEndOther = HorizontalDistSq(endOther, centerXY);

        bool changed = false;
        if (dStartTip > dStartOther + 1e-5)
        {
            rebar.SetHookOrientation(0, FlipHookOrientation(rebar.GetHookOrientation(0)));
            changed = true;
        }
        if (dEndTip > dEndOther + 1e-5)
        {
            rebar.SetHookOrientation(1, FlipHookOrientation(rebar.GetHookOrientation(1)));
            changed = true;
        }

        if (changed)
            doc.Regenerate();
    }

    private static double HorizontalDistSq(XYZ p, XYZ centerXY)
    {
        double dx = p.X - centerXY.X;
        double dy = p.Y - centerXY.Y;
        return dx * dx + dy * dy;
    }

    private static bool TrySetRectangularShapeDimensions(Rebar rebar, double widthFeet, double heightFeet)
    {
        bool setB = TrySetParam(rebar, ["B", "b", "Ancho", "Width", "A"], widthFeet);
        bool setC = TrySetParam(rebar, ["C", "h", "Peralte", "Altura", "Height", "D"], heightFeet);
        return setB && setC;
    }

    private static bool TrySetParam(Element elem, string[] names, double valueFeet)
    {
        foreach (string name in names)
        {
            var p = elem.LookupParameter(name);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
            {
                if (p.Set(valueFeet)) return true;
            }
        }
        return false;
    }

    private static RebarHookOrientation FlipHookOrientation(RebarHookOrientation current)
        => current == RebarHookOrientation.Left ? RebarHookOrientation.Right : RebarHookOrientation.Left;

    private static RebarBarType? ResolveBarType(
        List<RebarBarType> types, string requestedName, double preferredDiamMm)
    {
        if (types.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var exact = types.FirstOrDefault(t =>
                string.Equals(t.Name, requestedName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        double prefFeet = UnitConverter.MetersToFeet(preferredDiamMm / 1000.0);
        return types
            .OrderBy(t => Math.Abs((t.BarNominalDiameter > 0 ? t.BarNominalDiameter : t.BarModelDiameter) - prefFeet))
            .FirstOrDefault();
    }

    private static RebarHookType? ResolveHookType(
        List<RebarHookType> hooks, string requestedName, RebarStyle style, double preferredAngleDeg)
    {
        if (hooks.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var exact = hooks.FirstOrDefault(h =>
                string.Equals(h.Name, requestedName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        double targetRad = preferredAngleDeg * Math.PI / 180.0;
        var styleMatches = hooks.Where(h => h.Style == style).ToList();
        var pool = styleMatches.Count > 0 ? styleMatches : hooks;

        return pool
            .OrderBy(h => Math.Abs(h.HookAngle - targetRad))
            .ThenBy(h => h.Name.Contains("135") || h.Name.Contains("90") ? 0 : 1)
            .FirstOrDefault();
    }

    private static RebarShape? ResolveStirrupShape(List<RebarShape> shapes, string requestedName)
    {
        if (shapes.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var exact = shapes.FirstOrDefault(s =>
                string.Equals(s.Name, requestedName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        var stirrupPool = shapes
            .Where(s => s.RebarStyle == RebarStyle.StirrupTie &&
                        !string.Equals(s.Name, "00", StringComparison.OrdinalIgnoreCase) &&
                        !s.Name.StartsWith("M_00", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string[] preferred = ["T1", "51", "M_T1", "Estribo", "Stirrup"];
        foreach (string pref in preferred)
        {
            var match = stirrupPool.FirstOrDefault(s =>
                string.Equals(s.Name, pref, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(pref, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return stirrupPool.FirstOrDefault();
    }
}
