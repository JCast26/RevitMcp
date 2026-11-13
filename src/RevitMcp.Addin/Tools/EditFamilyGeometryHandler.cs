using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Manejador de la herramienta EditFamilyGeometry:
/// Permite modificar geométricamente y paramétricamente los elementos de una familia (.rfa)
/// abierta en el Editor de Familias de Autodesk Revit.
/// Todas las longitudes, desplazamientos y coordenadas se reciben en METROS; los ángulos en GRADOS.
/// </summary>
public static class EditFamilyGeometryHandler
{
    public static EditFamilyGeometryResponse Execute(UIDocument uidoc, EditFamilyGeometryRequest request)
    {
        var doc = InspectFamilyHandler.EnsureFamilyDocument(uidoc?.Document);

        if (doc.IsReadOnly)
            throw new InvalidOperationException("El documento de familia activo está en modo de sólo lectura.");

        if (string.IsNullOrWhiteSpace(request.Operation))
            throw new ArgumentException("El campo 'operation' es obligatorio.", nameof(request.Operation));

        string op = request.Operation.Trim();

        return op.ToLowerInvariant() switch
        {
            "move" => ExecuteMove(uidoc!, doc, request),
            "rotate" => ExecuteRotate(uidoc!, doc, request),
            "mirror" => ExecuteMirror(uidoc!, doc, request),
            "setextrusionbounds" => ExecuteSetBounds(uidoc!, doc, request),
            "scaleprofile" => ExecuteScaleProfile(uidoc!, doc, request),
            "offsetprofile" => ExecuteOffsetProfile(uidoc!, doc, request),
            "replaceextrusionprofile" => ExecuteReplaceProfile(uidoc!, doc, request),
            "createextrusion" => ExecuteCreateExtrusion(uidoc!, doc, request),
            "setfamilyparameter" => ExecuteSetParameter(uidoc!, doc, request),
            "deleteelements" => ExecuteDelete(uidoc!, doc, request),
            "combineelements" or "cutgeometry" => ExecuteCombineElements(uidoc!, doc, request),
            "configuresweptblenddetour" => ExecuteConfigureSweptBlendDetour(uidoc!, doc, request),
            _ => throw new ArgumentException(
                $"Operación desconocida '{request.Operation}'. Operaciones soportadas: " +
                "Move, Rotate, Mirror, SetExtrusionBounds, ScaleProfile, OffsetProfile, " +
                "ReplaceExtrusionProfile, CreateExtrusion, SetFamilyParameter, DeleteElements, " +
                "CombineElements, ConfigureSweptBlendDetour.")
        };
    }

    // =====================================================================
    // Transformaciones 3D: Move, Rotate, Mirror
    // =====================================================================

    private static EditFamilyGeometryResponse ExecuteMove(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        if (request.TranslationMeters == null)
            throw new ArgumentException("Para 'Move' se requiere 'translationMeters' ({x, y, z} en metros).");

        var ids = ResolveTargetIds(uidoc, doc, request);
        XYZ deltaFeet = InspectFamilyHandler.ToXyzFeet(request.TranslationMeters);

        using (var trans = new Transaction(doc, "MCP: Mover elementos de familia"))
        {
            trans.Start();
            PrepareElementsForModification(doc, ids, request.UnpinIfNeeded);
            ElementTransformUtils.MoveElements(doc, ids, deltaFeet);
            trans.Commit();
        }

        return BuildResponse("Move", doc, ids,
            $"Se desplazaron {ids.Count} elemento(s) por {request.TranslationMeters}.");
    }

    private static EditFamilyGeometryResponse ExecuteRotate(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        if (!request.AngleDegrees.HasValue)
            throw new ArgumentException("Para 'Rotate' se requiere 'angleDegrees' en grados.");

        var ids = ResolveTargetIds(uidoc, doc, request);
        double angleRad = UnitConverter.DegreesToRadians(request.AngleDegrees.Value);

        XYZ originFeet = request.AxisOriginMeters != null
            ? InspectFamilyHandler.ToXyzFeet(request.AxisOriginMeters)
            : ComputeCombinedCenterFeet(doc, ids);

        XYZ dir = request.AxisDirection != null
            ? new XYZ(request.AxisDirection.X, request.AxisDirection.Y, request.AxisDirection.Z)
            : XYZ.BasisZ;

        if (dir.GetLength() < 1e-9)
            throw new ArgumentException("El vector 'axisDirection' no puede ser nulo.");

        Line axis = Line.CreateUnbound(originFeet, dir.Normalize());

        using (var trans = new Transaction(doc, "MCP: Rotar elementos de familia"))
        {
            trans.Start();
            PrepareElementsForModification(doc, ids, request.UnpinIfNeeded);
            ElementTransformUtils.RotateElements(doc, ids, axis, angleRad);
            trans.Commit();
        }

        return BuildResponse("Rotate", doc, ids,
            $"Se rotaron {ids.Count} elemento(s) {request.AngleDegrees.Value:F2}°.");
    }

    private static EditFamilyGeometryResponse ExecuteMirror(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        if (request.PlaneNormal == null)
            throw new ArgumentException("Para 'Mirror' se requiere 'planeNormal' ({x, y, z}).");

        var ids = ResolveTargetIds(uidoc, doc, request);
        XYZ originFeet = request.PlaneOriginMeters != null
            ? InspectFamilyHandler.ToXyzFeet(request.PlaneOriginMeters)
            : XYZ.Zero;

        XYZ normal = new XYZ(request.PlaneNormal.X, request.PlaneNormal.Y, request.PlaneNormal.Z);
        if (normal.GetLength() < 1e-9)
            throw new ArgumentException("El vector 'planeNormal' no puede ser nulo.");

        Plane plane = Plane.CreateByNormalAndOrigin(normal.Normalize(), originFeet);

        IList<ElementId> resultIds;
        using (var trans = new Transaction(doc, "MCP: Simetría de elementos de familia"))
        {
            trans.Start();
            if (!request.Copy)
                PrepareElementsForModification(doc, ids, request.UnpinIfNeeded);

            resultIds = ElementTransformUtils.MirrorElements(doc, ids, plane, request.Copy);
            trans.Commit();
        }

        var affected = resultIds.Count > 0 ? resultIds : ids;
        return BuildResponse("Mirror", doc, affected,
            request.Copy
                ? $"Se crearon {affected.Count} copia(s) simétrica(s)."
                : $"Se reflejaron {ids.Count} elemento(s) en el plano indicado.");
    }

    // =====================================================================
    // Edición de cotas/profundidades: SetExtrusionBounds
    // =====================================================================

    private static EditFamilyGeometryResponse ExecuteSetBounds(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        var ids = ResolveTargetIds(uidoc, doc, request);

        using (var trans = new Transaction(doc, "MCP: Modificar límites de forma"))
        {
            trans.Start();
            PrepareElementsForModification(doc, ids, request.UnpinIfNeeded);

            foreach (var id in ids)
            {
                var elem = doc.GetElement(id);
                switch (elem)
                {
                    case Extrusion ext:
                        if (request.StartOffsetMeters.HasValue)
                            ext.StartOffset = UnitConverter.MetersToFeet(request.StartOffsetMeters.Value);
                        if (request.EndOffsetMeters.HasValue)
                            ext.EndOffset = UnitConverter.MetersToFeet(request.EndOffsetMeters.Value);
                        if (request.IsSolid.HasValue)
                            SetFormIsSolid(ext, request.IsSolid.Value);
                        break;

                    case Blend blend:
                        if (request.StartOffsetMeters.HasValue)
                            blend.BottomOffset = UnitConverter.MetersToFeet(request.StartOffsetMeters.Value);
                        if (request.EndOffsetMeters.HasValue)
                            blend.TopOffset = UnitConverter.MetersToFeet(request.EndOffsetMeters.Value);
                        if (request.IsSolid.HasValue)
                            SetFormIsSolid(blend, request.IsSolid.Value);
                        break;

                    case Revolution rev:
                        if (request.StartAngleDegrees.HasValue)
                            rev.StartAngle = UnitConverter.DegreesToRadians(request.StartAngleDegrees.Value);
                        if (request.EndAngleDegrees.HasValue)
                            rev.EndAngle = UnitConverter.DegreesToRadians(request.EndAngleDegrees.Value);
                        if (request.IsSolid.HasValue)
                            SetFormIsSolid(rev, request.IsSolid.Value);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"El elemento {id.Value} ({elem?.GetType().Name}) no es Extrusion, Blend ni Revolution.");
                }
            }

            trans.Commit();
        }

        return BuildResponse("SetExtrusionBounds", doc, ids,
            $"Límites actualizados para {ids.Count} forma(s).");
    }

    // =====================================================================
    // Edición de croquis (SketchEditScope): ScaleProfile, OffsetProfile, ReplaceExtrusionProfile
    // =====================================================================

    private static EditFamilyGeometryResponse ExecuteScaleProfile(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        double sx = request.ScaleX ?? request.ScaleFactor ?? 1.0;
        double sy = request.ScaleY ?? request.ScaleFactor ?? 1.0;
        if (sx <= 0 || sy <= 0)
            throw new ArgumentException("Los factores de escala 'scaleX'/'scaleY'/'scaleFactor' deben ser mayores a cero.");

        var ids = ResolveTargetIds(uidoc, doc, request);
        bool isUniform = Math.Abs(sx - sy) < 1e-6;

        foreach (var id in ids)
        {
            var sketches = GetEditableSketches(doc, id);
            foreach (var sketch in sketches)
            {
                Plane plane = sketch.SketchPlane.GetPlane();
                XYZ pivotFeet = request.PivotMeters != null
                    ? ProjectPointOntoPlane(InspectFamilyHandler.ToXyzFeet(request.PivotMeters), plane)
                    : ComputeProfileCentroidFeet(sketch.Profile);

                var newLoops = new List<List<Curve>>();
                foreach (CurveArray ca in sketch.Profile)
                {
                    var loop = new List<Curve>();
                    foreach (Curve curve in ca)
                    {
                        loop.AddRange(ScaleCurveInPlane(curve, plane, pivotFeet, sx, sy, isUniform));
                    }
                    newLoops.Add(loop);
                }

                ApplySketchLoops(doc, sketch, newLoops, request.UnpinIfNeeded, id, "MCP: Escalar perfil");
            }
        }

        return BuildResponse("ScaleProfile", doc, ids,
            $"Perfil escalado (scaleX={sx:F3}, scaleY={sy:F3}) en {ids.Count} elemento(s).");
    }

    private static EditFamilyGeometryResponse ExecuteOffsetProfile(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        if (!request.OffsetMeters.HasValue)
            throw new ArgumentException("Para 'OffsetProfile' se requiere 'offsetMeters' en metros.");

        double offsetFeet = UnitConverter.MetersToFeet(request.OffsetMeters.Value);
        if (Math.Abs(offsetFeet) < 1e-6)
            throw new ArgumentException("El valor de 'offsetMeters' es demasiado cercano a cero.");

        var ids = ResolveTargetIds(uidoc, doc, request);

        foreach (var id in ids)
        {
            var sketches = GetEditableSketches(doc, id);
            foreach (var sketch in sketches)
            {
                Plane plane = sketch.SketchPlane.GetPlane();
                var newLoops = new List<List<Curve>>();

                foreach (CurveArray ca in sketch.Profile)
                {
                    var loop = new CurveLoop();
                    foreach (Curve c in ca)
                        loop.Append(c);

                    CurveLoop offsetLoop = OffsetCurveLoopDeterministic(loop, offsetFeet, plane);
                    newLoops.Add(offsetLoop.ToList());
                }

                ApplySketchLoops(doc, sketch, newLoops, request.UnpinIfNeeded, id, "MCP: Desfasar perfil");
            }
        }

        return BuildResponse("OffsetProfile", doc, ids,
            $"Perfil desfasado {request.OffsetMeters.Value:F4} m en {ids.Count} elemento(s).");
    }

    private static EditFamilyGeometryResponse ExecuteReplaceProfile(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        var ids = ResolveTargetIds(uidoc, doc, request);

        foreach (var id in ids)
        {
            var sketches = GetEditableSketches(doc, id);
            var targetSketch = sketches[0]; // Para Blend, BottomSketch; para Extrusion/Revolution, su Sketch
            Plane plane = targetSketch.SketchPlane.GetPlane();
            var newLoops = BuildLoopsFromRequest(request, plane);

            ApplySketchLoops(doc, targetSketch, newLoops, request.UnpinIfNeeded, id, "MCP: Reemplazar perfil");
        }

        return BuildResponse("ReplaceExtrusionProfile", doc, ids,
            $"Perfil reemplazado exitosamente en {ids.Count} elemento(s).");
    }

    // =====================================================================
    // Creación de nueva Extrusión (sólido o vacío)
    // =====================================================================

    private static EditFamilyGeometryResponse ExecuteCreateExtrusion(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        XYZ originFeet = request.SketchPlaneOriginMeters != null
            ? InspectFamilyHandler.ToXyzFeet(request.SketchPlaneOriginMeters)
            : XYZ.Zero;

        XYZ normal = request.SketchPlaneNormal != null
            ? new XYZ(request.SketchPlaneNormal.X, request.SketchPlaneNormal.Y, request.SketchPlaneNormal.Z)
            : XYZ.BasisZ;

        if (normal.GetLength() < 1e-9)
            throw new ArgumentException("El vector 'sketchPlaneNormal' no puede ser nulo.");

        Plane plane = Plane.CreateByNormalAndOrigin(normal.Normalize(), originFeet);
        var loops = BuildLoopsFromRequest(request, plane);

        var profile = new CurveArrArray();
        foreach (var loop in loops)
        {
            var ca = new CurveArray();
            foreach (var c in loop)
                ca.Append(c);
            profile.Append(ca);
        }

        bool isSolid = request.IsSolid ?? true;
        double startFeet = UnitConverter.MetersToFeet(request.StartOffsetMeters ?? 0.0);
        double endFeet = UnitConverter.MetersToFeet(request.EndOffsetMeters ?? 1.0);

        if (Math.Abs(endFeet - startFeet) < 1e-5)
            throw new ArgumentException("La diferencia entre 'endOffsetMeters' y 'startOffsetMeters' no puede ser cero.");

        Extrusion created;
        var combinedIds = new List<ElementId>();
        using (var trans = new Transaction(doc, "MCP: Crear extrusión en familia"))
        {
            trans.Start();
            var sketchPlane = SketchPlane.Create(doc, plane);
            double initialEnd = Math.Abs(endFeet) > 1e-5 ? endFeet : (endFeet - startFeet);
            created = doc.FamilyCreate.NewExtrusion(isSolid, profile, sketchPlane, initialEnd);
            created.StartOffset = startFeet;
            created.EndOffset = endFeet;

            // Si es un vacío (isSolid == false) y se indicó elementId / elementIds, cortar automáticamente el sólido objetivo
            if (!isSolid && (request.ElementId.HasValue || request.ElementIds is { Count: > 0 } || request.UseSelection))
            {
                doc.Regenerate();
                var targetIds = ResolveTargetIds(uidoc, doc, request);
                var comb = new CombinableElementArray();
                foreach (var tid in targetIds)
                {
                    if (doc.GetElement(tid) is CombinableElement ce && tid != created.Id)
                    {
                        comb.Append(ce);
                        combinedIds.Add(tid);
                    }
                }
                if (comb.Size > 0)
                {
                    comb.Append(created);
                    doc.CombineElements(comb);
                }
            }

            trans.Commit();
        }

        var ids = new List<ElementId> { created.Id };
        ids.AddRange(combinedIds);
        return BuildResponse("CreateExtrusion", doc, ids,
            combinedIds.Count > 0
                ? $"Extrusión vacía creada (Id: {created.Id.Value}) y combinada en corte con {combinedIds.Count} sólido(s)."
                : $"Extrusión {(isSolid ? "sólida" : "vacía")} creada con éxito (Id: {created.Id.Value}).");
    }

    // =====================================================================
    // Combinación geométrica (Cut / Join) y Quiebre Paramétrico en SweptBlend
    // =====================================================================

    private static EditFamilyGeometryResponse ExecuteCombineElements(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        var ids = ResolveTargetIds(uidoc, doc, request);
        if (ids.Count < 2)
            throw new ArgumentException("Para 'CombineElements' se requieren al menos 2 ElementId en 'elementIds' (sólido y vacío).");

        using (var trans = new Transaction(doc, "MCP: Combinar/Cortar elementos de familia"))
        {
            trans.Start();
            var comb = new CombinableElementArray();
            foreach (var id in ids)
            {
                if (doc.GetElement(id) is not CombinableElement ce)
                    throw new InvalidOperationException($"El elemento {id.Value} no es un CombinableElement.");
                comb.Append(ce);
            }
            doc.CombineElements(comb);
            trans.Commit();
        }

        return BuildResponse("CombineElements", doc, ids,
            $"Se combinaron geométricamente {ids.Count} elementos.");
    }

    private static EditFamilyGeometryResponse ExecuteConfigureSweptBlendDetour(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        // 1. Resolver el SweptBlend objetivo (por defecto elementId, selección o 5626051)
        ElementId targetId;
        if (request.ElementId.HasValue)
        {
            targetId = new ElementId(request.ElementId.Value);
        }
        else if (request.ElementIds is { Count: > 0 })
        {
            targetId = new ElementId(request.ElementIds[0]);
        }
        else if (uidoc.Selection.GetElementIds().Count > 0)
        {
            targetId = uidoc.Selection.GetElementIds().First();
        }
        else
        {
            targetId = new ElementId(5626051L);
        }

        if (doc.GetElement(targetId) is not SweptBlend sb)
            throw new InvalidOperationException($"El elemento {targetId.Value} no es un SweptBlend válido en la familia activa.");

        var famMgr = doc.FamilyManager
            ?? throw new InvalidOperationException("El documento no contiene un FamilyManager.");

        double offsetMeters = request.OffsetMeters ?? request.Rectangle?.DepthMeters ?? 0.200;
        double widthMeters = request.NumericValue ?? request.Rectangle?.WidthMeters ?? 0.200;

        if (offsetMeters <= 0 || widthMeters <= 0)
            throw new ArgumentException("Los valores de 'Offset Post' y 'Width Post' deben ser mayores que cero.");

        double offsetFeet = UnitConverter.MetersToFeet(offsetMeters);
        double widthFeet = UnitConverter.MetersToFeet(widthMeters);

        var affectedIds = new List<ElementId> { sb.Id };
        int createdDimsCount = 0;
        int alignedEdgesCount = 0;
        string alignmentStrategy = "None";
        ElementId voidExtrusionId = ElementId.InvalidElementId;
        var edgeTargetPlanes = new List<(XYZ MidPoint, bool IsVertical, double CoordFeet, ReferencePlane Plane)>();

        ViewPlan planView = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate && v.GenLevel != null && Math.Abs(v.ViewDirection.Z) > 0.99)
            ?? new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate)
            ?? throw new InvalidOperationException("No se encontró una vista de planta (ViewPlan) en la familia.");

        using (var trans = new Transaction(doc, "MCP: Modelar quiebre paramétrico en tapajunta"))
        {
            trans.Start();

            if (famMgr.CurrentType == null)
                famMgr.NewType("Default");

            var origType = famMgr.CurrentType;

            var thicknessParam = famMgr.get_Parameter("Thickness")
                ?? throw new InvalidOperationException("No se encontró el parámetro 'Thickness' en la familia.");

            double thicknessFeet = origType?.AsDouble(thicknessParam) ?? UnitConverter.MetersToFeet(0.010);

            // 2. Crear o recuperar los parámetros de ejemplar: Offset Post, Width Post, Total Depth Post
            var offsetPostParam = famMgr.get_Parameter("Offset Post")
                ?? famMgr.AddParameter("Offset Post", GroupTypeId.Geometry, SpecTypeId.Length, true);

            var widthPostParam = famMgr.get_Parameter("Width Post")
                ?? famMgr.AddParameter("Width Post", GroupTypeId.Geometry, SpecTypeId.Length, true);

            var totalDepthParam = famMgr.get_Parameter("Total Depth Post")
                ?? famMgr.AddParameter("Total Depth Post", GroupTypeId.Geometry, SpecTypeId.Length, true);

            // Asignar valores iniciales de 200mm en todos los tipos de la familia para evitar 0 en regeneraciones
            foreach (FamilyType ft in famMgr.Types)
            {
                if (string.IsNullOrWhiteSpace(ft.Name)) continue;
                famMgr.CurrentType = ft;
                famMgr.Set(offsetPostParam, offsetFeet);
                famMgr.Set(widthPostParam, widthFeet);
            }
            if (origType != null)
                famMgr.CurrentType = origType;

            famMgr.Set(offsetPostParam, offsetFeet);
            famMgr.Set(widthPostParam, widthFeet);
            famMgr.SetFormula(totalDepthParam, "Offset Post + 2 * Thickness");

            // 3. Limpiar elementos auxiliares previos si la operación se re-ejecuta
            var oldVoidIds = new List<ElementId>();
            if (sb.Combinations != null && !sb.Combinations.IsEmpty)
            {
                foreach (GeomCombination gc in sb.Combinations)
                {
                    foreach (CombinableElement ce in gc.AllMembers)
                    {
                        if (ce.Id != sb.Id && ce is Extrusion oldExt && !oldExt.IsSolid)
                            oldVoidIds.Add(oldExt.Id);
                    }
                }
            }
            if (oldVoidIds.Count > 0)
                doc.Delete(oldVoidIds);

            string[] helperPlaneNames =
            {
                "Post_Inner_Left", "Post_Inner_Right", "Post_Outer_Left", "Post_Outer_Right",
                "Post_Wing_Front", "Post_Detour_Inner", "Post_Detour_Outer"
            };
            var oldPlaneIds = new FilteredElementCollector(doc)
                .OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>()
                .Where(rp => helperPlaneNames.Contains(rp.Name, StringComparer.OrdinalIgnoreCase))
                .Select(rp => rp.Id)
                .ToList();
            if (oldPlaneIds.Count > 0)
                doc.Delete(oldPlaneIds);

            // 4. Duplicar el FamilySymbol del perfil de 5626051 y asociar su espesor a 'Total Depth Post'
            FamilySymbol? origSymbol = sb.BottomProfileSymbol?.Profile;
            if (origSymbol == null)
            {
                var p1Id = sb.get_Parameter(BuiltInParameter.PROFILE1_FAM_TYPE)?.AsElementId();
                if (p1Id != null && p1Id != ElementId.InvalidElementId)
                    origSymbol = doc.GetElement(p1Id) as FamilySymbol;
            }

            if (origSymbol == null)
                throw new InvalidOperationException($"No se pudo obtener el FamilySymbol de perfil del SweptBlend {sb.Id.Value}.");

            const string newSymbolName = "Perfil_Exterior_Poste";
            FamilySymbol newSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Family.Id == origSymbol.Family.Id && s.Name.Equals(newSymbolName, StringComparison.OrdinalIgnoreCase))
                ?? (FamilySymbol)origSymbol.Duplicate(newSymbolName);

            foreach (Parameter origParam in origSymbol.Parameters)
            {
                var assoc = famMgr.GetAssociatedFamilyParameter(origParam);
                if (assoc == null) continue;

                var newParam = newSymbol.LookupParameter(origParam.Definition.Name);
                if (newParam == null || newParam.IsReadOnly || !famMgr.CanElementParameterBeAssociated(newParam))
                    continue;

                if (assoc.Id == thicknessParam.Id || assoc.Definition.Name.Equals("Thickness", StringComparison.OrdinalIgnoreCase))
                {
                    famMgr.AssociateElementParameterToFamilyParameter(newParam, totalDepthParam);
                }
                else
                {
                    famMgr.AssociateElementParameterToFamilyParameter(newParam, assoc);
                }
            }

            var prof1Param = sb.get_Parameter(BuiltInParameter.PROFILE1_FAM_TYPE);
            if (prof1Param != null && !prof1Param.IsReadOnly)
                prof1Param.Set(newSymbol.Id);

            var prof2Param = sb.get_Parameter(BuiltInParameter.PROFILE2_FAM_TYPE);
            if (prof2Param != null && !prof2Param.IsReadOnly)
                prof2Param.Set(newSymbol.Id);

            try
            {
                if (sb.BottomProfileSymbol != null && sb.BottomProfileSymbol.Profile?.Id != newSymbol.Id)
                    sb.BottomProfileSymbol.Profile = newSymbol;
                if (sb.TopProfileSymbol != null && sb.TopProfileSymbol.Profile?.Id != newSymbol.Id)
                    sb.TopProfileSymbol.Profile = newSymbol;
            }
            catch
            {
                // Ignorar si ya fue actualizado por BuiltInParameter
            }

            doc.Regenerate();

            // Verificar que el bounding box de sb se haya expandido en -Y
            var sbBox = sb.get_BoundingBox(null)
                ?? throw new InvalidOperationException("No se pudo obtener el BoundingBox del SweptBlend tras actualizar el perfil.");

            double expectedTotalDepthFeet = offsetFeet + 2.0 * thicknessFeet;
            if (Math.Abs((sbBox.Max.Y - sbBox.Min.Y) - expectedTotalDepthFeet) > 1e-3)
            {
                // Fallback seguro si origSymbol es exclusivo de este elemento
                foreach (Parameter origParam in origSymbol.Parameters)
                {
                    var assoc = famMgr.GetAssociatedFamilyParameter(origParam);
                    if (assoc != null && (assoc.Id == thicknessParam.Id || assoc.Definition.Name.Equals("Thickness", StringComparison.OrdinalIgnoreCase)))
                    {
                        famMgr.AssociateElementParameterToFamilyParameter(origParam, totalDepthParam);
                    }
                }
                doc.Regenerate();
                sbBox = sb.get_BoundingBox(null)!;
            }

            // 5. Localizar planos de referencia existentes y crear los 7 planos de referencia del quiebre
            var allRefPlanes = new FilteredElementCollector(doc)
                .OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>()
                .ToList();

            var rpCenterX = allRefPlanes.FirstOrDefault(r => r.Name.Equals("Center (Left/Right)", StringComparison.OrdinalIgnoreCase))
                ?? allRefPlanes.FirstOrDefault(r => Math.Abs(r.Normal.X) > 0.99 && Math.Abs(r.BubbleEnd.X) < 1e-4)
                ?? throw new InvalidOperationException("No se encontró el plano de referencia central X ('Center (Left/Right)').");

            var rpCenterY = allRefPlanes.FirstOrDefault(r => r.Name.Equals("Center (Front/Back)", StringComparison.OrdinalIgnoreCase))
                ?? allRefPlanes.FirstOrDefault(r => Math.Abs(r.Normal.Y) > 0.99 && Math.Abs(r.BubbleEnd.Y) < 1e-4)
                ?? throw new InvalidOperationException("No se encontró el plano de referencia central Y ('Center (Front/Back)').");

            double xLeftEnd = sbBox.Min.X;
            double xRightEnd = sbBox.Max.X;

            var rpLeftEnd = allRefPlanes.FirstOrDefault(r => Math.Abs(r.Normal.X) > 0.99 && Math.Abs(r.BubbleEnd.X - xLeftEnd) < 1e-3);
            var rpRightEnd = allRefPlanes.FirstOrDefault(r => Math.Abs(r.Normal.X) > 0.99 && Math.Abs(r.BubbleEnd.X - xRightEnd) < 1e-3);

            double halfWidth = widthFeet * 0.5;
            double xInnerLeft = -halfWidth;
            double xInnerRight = halfWidth;
            double xOuterLeft = -(halfWidth + thicknessFeet);
            double xOuterRight = halfWidth + thicknessFeet;

            double yWingFront = -thicknessFeet;
            double yDetourInner = -(thicknessFeet + offsetFeet);
            double yDetourOuter = -(2.0 * thicknessFeet + offsetFeet);

            ReferencePlane CreateVerticalPlane(double xFeet, string name)
            {
                var rp = doc.FamilyCreate.NewReferencePlane(
                    new XYZ(xFeet, 2.0, 0.0),
                    new XYZ(xFeet, -2.0, 0.0),
                    XYZ.BasisZ,
                    planView);
                rp.Name = name;
                affectedIds.Add(rp.Id);
                return rp;
            }

            ReferencePlane CreateHorizontalPlane(double yFeet, string name)
            {
                var rp = doc.FamilyCreate.NewReferencePlane(
                    new XYZ(2.0, yFeet, 0.0),
                    new XYZ(-2.0, yFeet, 0.0),
                    XYZ.BasisZ,
                    planView);
                rp.Name = name;
                affectedIds.Add(rp.Id);
                return rp;
            }

            var rpInnerLeft = CreateVerticalPlane(xInnerLeft, "Post_Inner_Left");
            var rpInnerRight = CreateVerticalPlane(xInnerRight, "Post_Inner_Right");
            var rpOuterLeft = CreateVerticalPlane(xOuterLeft, "Post_Outer_Left");
            var rpOuterRight = CreateVerticalPlane(xOuterRight, "Post_Outer_Right");

            var rpWingFront = CreateHorizontalPlane(yWingFront, "Post_Wing_Front");
            var rpDetourInner = CreateHorizontalPlane(yDetourInner, "Post_Detour_Inner");
            var rpDetourOuter = CreateHorizontalPlane(yDetourOuter, "Post_Detour_Outer");

            doc.Regenerate();

            // 6. Crear las cotas paramétricas en Ref. Level (EQ, Width Post, Offset Post, Thickness)
            void TryCreateLabeledDimension(ReferencePlane rp1, ReferencePlane rp2, Line dimLine, FamilyParameter labelParam)
            {
                try
                {
                    var refs = new ReferenceArray();
                    refs.Append(rp1.GetReference());
                    refs.Append(rp2.GetReference());
                    var dim = doc.FamilyCreate.NewLinearDimension(planView, dimLine, refs);
                    if (dim != null)
                    {
                        dim.FamilyLabel = labelParam;
                        createdDimsCount++;
                        affectedIds.Add(dim.Id);
                    }
                }
                catch (Exception ex)
                {
                    RevitExternalEventHandler.LogTrace($"TryCreateLabeledDimension('{labelParam.Definition.Name}') warning: {ex.Message}");
                }
            }

            // 6.1 Simetría EQ respecto a Center (Left/Right)
            try
            {
                var eqRefs = new ReferenceArray();
                eqRefs.Append(rpInnerLeft.GetReference());
                eqRefs.Append(rpCenterX.GetReference());
                eqRefs.Append(rpInnerRight.GetReference());
                var eqDim = doc.FamilyCreate.NewLinearDimension(
                    planView,
                    Line.CreateBound(new XYZ(xOuterLeft - 0.5, -1.2, 0.0), new XYZ(xOuterRight + 0.5, -1.2, 0.0)),
                    eqRefs);
                if (eqDim != null)
                {
                    eqDim.AreSegmentsEqual = true;
                    createdDimsCount++;
                    affectedIds.Add(eqDim.Id);
                }
            }
            catch (Exception ex)
            {
                RevitExternalEventHandler.LogTrace($"EQ Dimension warning: {ex.Message}");
            }

            // 6.2 Width Post (luz libre interior X)
            TryCreateLabeledDimension(
                rpInnerLeft,
                rpInnerRight,
                Line.CreateBound(new XYZ(xInnerLeft, -1.5, 0.0), new XYZ(xInnerRight, -1.5, 0.0)),
                widthPostParam);

            // 6.3 Espesor de patas laterales (Thickness)
            TryCreateLabeledDimension(
                rpOuterLeft,
                rpInnerLeft,
                Line.CreateBound(new XYZ(xOuterLeft, -1.8, 0.0), new XYZ(xInnerLeft, -1.8, 0.0)),
                thicknessParam);

            TryCreateLabeledDimension(
                rpInnerRight,
                rpOuterRight,
                Line.CreateBound(new XYZ(xInnerRight, -1.8, 0.0), new XYZ(xOuterRight, -1.8, 0.0)),
                thicknessParam);

            // 6.4 Espesor de alas en Y (Thickness), Offset Post (luz libre interior Y) y espesor frontal (Thickness)
            TryCreateLabeledDimension(
                rpWingFront,
                rpCenterY,
                Line.CreateBound(new XYZ(-1.2, yWingFront, 0.0), new XYZ(-1.2, 0.0, 0.0)),
                thicknessParam);

            TryCreateLabeledDimension(
                rpDetourInner,
                rpWingFront,
                Line.CreateBound(new XYZ(-1.5, yDetourInner, 0.0), new XYZ(-1.5, yWingFront, 0.0)),
                offsetPostParam);

            TryCreateLabeledDimension(
                rpDetourOuter,
                rpDetourInner,
                Line.CreateBound(new XYZ(-1.8, yDetourOuter, 0.0), new XYZ(-1.8, yDetourInner, 0.0)),
                thicknessParam);

            // 7. Crear la extrusión vacía (Void Extrusion) con los 3 bucles de recorte
            var profile = new CurveArrArray();

            void AddRectLoop(double minX, double maxX, double minY, double maxY,
                ReferencePlane? rpMinX, ReferencePlane? rpMaxX, ReferencePlane? rpMinY, ReferencePlane? rpMaxY)
            {
                var p0 = new XYZ(minX, minY, 0.0);
                var p1 = new XYZ(maxX, minY, 0.0);
                var p2 = new XYZ(maxX, maxY, 0.0);
                var p3 = new XYZ(minX, maxY, 0.0);

                var ca = new CurveArray();
                ca.Append(Line.CreateBound(p0, p1)); // Borde inferior (minY)
                ca.Append(Line.CreateBound(p1, p2)); // Borde derecho (maxX)
                ca.Append(Line.CreateBound(p2, p3)); // Borde superior (maxY)
                ca.Append(Line.CreateBound(p3, p0)); // Borde izquierdo (minX)
                profile.Append(ca);

                if (rpMinY != null) edgeTargetPlanes.Add(((p0 + p1) * 0.5, false, minY, rpMinY));
                if (rpMaxX != null) edgeTargetPlanes.Add(((p1 + p2) * 0.5, true, maxX, rpMaxX));
                if (rpMaxY != null) edgeTargetPlanes.Add(((p2 + p3) * 0.5, false, maxY, rpMaxY));
                if (rpMinX != null) edgeTargetPlanes.Add(((p3 + p0) * 0.5, true, minX, rpMinX));
            }

            // Bucle 1: Recorte exterior izquierdo
            AddRectLoop(xLeftEnd, xOuterLeft, yDetourOuter, yWingFront, rpLeftEnd, rpOuterLeft, rpDetourOuter, rpWingFront);
            // Bucle 2: Recorte exterior derecho
            AddRectLoop(xOuterRight, xRightEnd, yDetourOuter, yWingFront, rpOuterRight, rpRightEnd, rpDetourOuter, rpWingFront);
            // Bucle 3: Recorte interior central (alojamiento del poste)
            AddRectLoop(xInnerLeft, xInnerRight, yDetourInner, 0.0, rpInnerLeft, rpInnerRight, rpDetourInner, rpCenterY);

            var sketchPlane = planView.SketchPlane
                ?? SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));

            double voidStartFeet = UnitConverter.MetersToFeet(-1.0);
            double voidEndFeet = UnitConverter.MetersToFeet(5.0);

            var voidExt = doc.FamilyCreate.NewExtrusion(false, profile, sketchPlane, voidEndFeet);
            voidExt.StartOffset = voidStartFeet;
            voidExt.EndOffset = voidEndFeet;
            voidExtrusionId = voidExt.Id;
            affectedIds.Add(voidExt.Id);

            // Asociar visibilidad del vacío a 'Exterior' si aplica
            var extVisParam = famMgr.get_Parameter("Exterior");
            var voidVisParam = voidExt.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            if (extVisParam != null && voidVisParam != null && !voidVisParam.IsReadOnly && famMgr.CanElementParameterBeAssociated(voidVisParam))
            {
                try { famMgr.AssociateElementParameterToFamilyParameter(voidVisParam, extVisParam); } catch { }
            }

            doc.Regenerate();

            // 8. Intentar alinear bordes del croquis o caras 3D del vacío a los planos de referencia
            alignedEdgesCount = TryAlignExtrusionToPlanes(doc, planView, voidExt, edgeTargetPlanes, out alignmentStrategy);

            // 9. Combinar geométricamente (Cut Geometry) el SweptBlend 5626051 con el Void Extrusion
            bool alreadyCombined = false;
            if (sb.Combinations != null && !sb.Combinations.IsEmpty)
            {
                foreach (GeomCombination gc in sb.Combinations)
                {
                    foreach (CombinableElement ce in gc.AllMembers)
                    {
                        if (ce.Id == voidExt.Id)
                            alreadyCombined = true;
                    }
                }
            }

            if (!alreadyCombined)
            {
                var comb = new CombinableElementArray();
                comb.Append(sb);
                comb.Append(voidExt);
                doc.CombineElements(comb);
            }

            doc.Regenerate();
            trans.Commit();
        }

        // 10. Si el alineamiento directo fuera de SketchEditScope no bloqueó los 12 bordes, hacerlo dentro de SketchEditScope
        if (alignedEdgesCount < 10 && voidExtrusionId != ElementId.InvalidElementId)
        {
            if (doc.GetElement(voidExtrusionId) is Extrusion voidExt && voidExt.Sketch != null)
            {
                try
                {
                    using var sketchScope = new SketchEditScope(doc, "MCP: Restringir croquis del vacío a planos de referencia");
                    sketchScope.Start(voidExt.Sketch.Id);

                    using (var alignTrans = new Transaction(doc, "MCP: Alinear líneas de croquis a planos"))
                    {
                        alignTrans.Start();
                        int scopeAligned = AlignSketchModelCurvesToPlanes(doc, planView, voidExt.Sketch, edgeTargetPlanes);
                        if (scopeAligned > alignedEdgesCount)
                        {
                            alignedEdgesCount = scopeAligned;
                            alignmentStrategy = "SketchEditScope";
                        }
                        alignTrans.Commit();
                    }

                    sketchScope.Commit(new RebarFailuresPreprocessor());
                }
                catch (Exception ex)
                {
                    RevitExternalEventHandler.LogTrace($"SketchEditScope alignment warning: {ex.Message}");
                }
            }
        }

        return BuildResponse("ConfigureSweptBlendDetour", doc, new List<ElementId> { sb.Id },
            $"Quiebre paramétrico configurado exitosamente en elemento {sb.Id.Value}: " +
            $"Offset Post = {offsetMeters * 1000:F0} mm (Ejemplar), Width Post = {widthMeters * 1000:F0} mm (Ejemplar), " +
            $"Total Depth Post = 'Offset Post + 2 * Thickness', perfil '{new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s => s.Name == "Perfil_Exterior_Poste")?.Name}', " +
            $"{createdDimsCount} cotas paramétricas creadas, {alignedEdgesCount} bordes alineados ({alignmentStrategy}) y corte vacío aplicado (Void Id: {voidExtrusionId.Value}).");
    }

    private static int TryAlignExtrusionToPlanes(
        Document doc,
        ViewPlan planView,
        Extrusion voidExt,
        List<(XYZ MidPoint, bool IsVertical, double CoordFeet, ReferencePlane Plane)> edgeTargetPlanes,
        out string strategy)
    {
        // Intento 1: Alinear ModelCurves del Sketch directamente
        if (voidExt.Sketch != null)
        {
            int count = AlignSketchModelCurvesToPlanes(doc, planView, voidExt.Sketch, edgeTargetPlanes);
            if (count >= 10)
            {
                strategy = "SketchCurves";
                return count;
            }
        }

        // Intento 2: Alinear caras planas verticales 3D del Extrusion a los ReferencePlanes
        int faceAligned = 0;
        try
        {
            var geomElem = voidExt.get_Geometry(new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = true,
                View = planView
            });

            if (geomElem != null)
            {
                foreach (GeometryObject gObj in geomElem)
                {
                    if (gObj is not Solid solid) continue;
                    foreach (Face face in solid.Faces)
                    {
                        if (face is not PlanarFace pf || pf.Reference == null) continue;
                        if (Math.Abs(pf.FaceNormal.Z) > 0.1) continue; // Sólo caras verticales

                        bool isVertEdge = Math.Abs(pf.FaceNormal.X) > 0.9; // Normal en X => borde paralelo a Y
                        BoundingBoxUV uvBox = pf.GetBoundingBox();
                        XYZ faceCenter = pf.Evaluate((uvBox.Min + uvBox.Max) * 0.5);
                        XYZ faceCenterXY = new XYZ(faceCenter.X, faceCenter.Y, 0.0);

                        var match = edgeTargetPlanes
                            .Where(t => t.IsVertical == isVertEdge &&
                                        Math.Abs((isVertEdge ? faceCenter.X : faceCenter.Y) - t.CoordFeet) < 1e-3)
                            .OrderBy(t => faceCenterXY.DistanceTo(t.MidPoint))
                            .FirstOrDefault();

                        if (match.Plane != null && faceCenterXY.DistanceTo(match.MidPoint) < 0.5)
                        {
                            try
                            {
                                doc.FamilyCreate.NewAlignment(planView, match.Plane.GetReference(), pf.Reference);
                                faceAligned++;
                            }
                            catch (Exception ex)
                            {
                                RevitExternalEventHandler.LogTrace($"Face alignment warning: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RevitExternalEventHandler.LogTrace($"TryAlignExtrusionToPlanes (3D faces) warning: {ex.Message}");
        }

        strategy = faceAligned > 0 ? "PlanarFaces3D" : "None";
        return faceAligned;
    }

    private static int AlignSketchModelCurvesToPlanes(
        Document doc,
        ViewPlan planView,
        Sketch sketch,
        List<(XYZ MidPoint, bool IsVertical, double CoordFeet, ReferencePlane Plane)> edgeTargetPlanes)
    {
        int aligned = 0;
        var modelCurves = sketch.GetAllElements()
            .Select(id => doc.GetElement(id))
            .OfType<ModelCurve>()
            .ToList();

        foreach (var mc in modelCurves)
        {
            var curve = mc.GeometryCurve;
            if (curve == null || !curve.IsBound) continue;

            var cRef = curve.Reference;
            if (cRef == null) continue;

            XYZ mid = curve.Evaluate(0.5, true);
            XYZ dir = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
            bool isVert = Math.Abs(dir.Y) > 0.9; // Línea paralela al eje Y (X constante)

            var match = edgeTargetPlanes
                .Where(t => t.IsVertical == isVert &&
                            Math.Abs((isVert ? mid.X : mid.Y) - t.CoordFeet) < 1e-3)
                .OrderBy(t => mid.DistanceTo(t.MidPoint))
                .FirstOrDefault();

            if (match.Plane == null) continue;

            try
            {
                doc.FamilyCreate.NewAlignment(planView, match.Plane.GetReference(), cRef);
                aligned++;
            }
            catch
            {
                try
                {
                    doc.FamilyCreate.NewAlignment(null, match.Plane.GetReference(), cRef);
                    aligned++;
                }
                catch (Exception ex)
                {
                    RevitExternalEventHandler.LogTrace($"Sketch curve alignment warning: {ex.Message}");
                }
            }
        }

        return aligned;
    }

    // =====================================================================
    // Parámetros de familia / elemento y eliminación
    // =====================================================================

    private static EditFamilyGeometryResponse ExecuteSetParameter(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ParameterName))
            throw new ArgumentException("Para 'SetFamilyParameter' se requiere 'parameterName'.");

        // Atajo para ejecutar la configuración completa del quiebre desde el enum existente 'SetFamilyParameter'
        if (request.ParameterName.Equals("ConfigureSweptBlendDetour", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteConfigureSweptBlendDetour(uidoc, doc, request);
        }

        var famMgr = doc.FamilyManager
            ?? throw new InvalidOperationException("El documento no contiene un FamilyManager.");

        // Caso A: Si se indicó elementId / useSelection, intentar parámetro directo del elemento
        if (request.ElementId.HasValue || request.ElementIds is { Count: > 0 } || request.UseSelection)
        {
            var ids = ResolveTargetIds(uidoc, doc, request);
            using (var trans = new Transaction(doc, "MCP: Modificar parámetro de elemento"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(id);
                    var param = elem?.LookupParameter(request.ParameterName)
                        ?? throw new InvalidOperationException(
                            $"No se encontró el parámetro '{request.ParameterName}' en el elemento {id.Value}.");

                    if (!string.IsNullOrWhiteSpace(request.Formula))
                    {
                        var targetFamParam = famMgr.get_Parameter(request.Formula)
                            ?? throw new InvalidOperationException($"No existe el parámetro de familia '{request.Formula}' para asociar.");
                        famMgr.AssociateElementParameterToFamilyParameter(param, targetFamParam);
                    }
                    else
                    {
                        if (param.IsReadOnly)
                            throw new InvalidOperationException(
                                $"El parámetro '{request.ParameterName}' en el elemento {id.Value} es de sólo lectura.");

                        AssignElementParameterValue(param, request);
                    }
                }
                trans.Commit();
            }

            return BuildResponse("SetFamilyParameter", doc, ids,
                $"Parámetro '{request.ParameterName}' actualizado en {ids.Count} elemento(s).");
        }

        // Caso B: Parámetro del FamilyManager (creándolo automáticamente si no existe)
        FamilyParameter? famParam = famMgr.get_Parameter(request.ParameterName);

        using (var trans = new Transaction(doc, "MCP: Modificar parámetro de familia"))
        {
            trans.Start();

            if (!string.IsNullOrWhiteSpace(request.FamilyTypeName))
            {
                FamilyType? targetType = null;
                foreach (FamilyType ft in famMgr.Types)
                {
                    if (ft.Name.Equals(request.FamilyTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetType = ft;
                        break;
                    }
                }
                if (targetType == null)
                    throw new InvalidOperationException($"No se encontró el tipo de familia '{request.FamilyTypeName}'.");

                famMgr.CurrentType = targetType;
            }
            else if (famMgr.CurrentType == null)
            {
                famMgr.NewType("Default");
            }

            if (famParam == null)
            {
                bool isInstance = !string.Equals(request.StringValue, "Type", StringComparison.OrdinalIgnoreCase);
                famParam = famMgr.AddParameter(request.ParameterName, GroupTypeId.Geometry, SpecTypeId.Length, isInstance);
            }

            if (request.Formula != null)
            {
                famMgr.SetFormula(famParam, string.IsNullOrWhiteSpace(request.Formula) ? null : request.Formula);
            }
            else if (request.NumericValue.HasValue || request.IntegerValue.HasValue || request.BooleanValue.HasValue ||
                     (request.StringValue != null && !request.StringValue.Equals("Type", StringComparison.OrdinalIgnoreCase) && !request.StringValue.Equals("Instance", StringComparison.OrdinalIgnoreCase)))
            {
                AssignFamilyManagerValue(famMgr, famParam, request);
            }

            trans.Commit();
        }

        return new EditFamilyGeometryResponse
        {
            Success = true,
            Operation = "SetFamilyParameter",
            AffectedElementIds = new List<long> { famParam!.Id.Value },
            Message = $"Parámetro de familia '{request.ParameterName}' actualizado correctamente."
        };
    }

    private static EditFamilyGeometryResponse ExecuteDelete(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        var ids = ResolveTargetIds(uidoc, doc, request);

        ICollection<ElementId> deleted;
        using (var trans = new Transaction(doc, "MCP: Eliminar elementos de familia"))
        {
            trans.Start();
            PrepareElementsForModification(doc, ids, request.UnpinIfNeeded);
            deleted = doc.Delete(ids);
            trans.Commit();
        }

        return new EditFamilyGeometryResponse
        {
            Success = true,
            Operation = "DeleteElements",
            AffectedElementIds = deleted.Select(x => x.Value).ToList(),
            Message = $"Se eliminaron {deleted.Count} elemento(s) de la familia."
        };
    }

    // =====================================================================
    // Helpers geométricos compartidos
    // =====================================================================

    private static List<ElementId> ResolveTargetIds(UIDocument uidoc, Document doc, EditFamilyGeometryRequest request)
    {
        var rawIds = new HashSet<long>();
        if (request.ElementId.HasValue)
            rawIds.Add(request.ElementId.Value);

        if (request.ElementIds != null)
        {
            foreach (var id in request.ElementIds)
                rawIds.Add(id);
        }

        if (rawIds.Count == 0 && request.UseSelection)
        {
            foreach (var id in uidoc.Selection.GetElementIds())
                rawIds.Add(id.Value);
        }

        if (rawIds.Count == 0)
            throw new ArgumentException("Debes especificar 'elementId', 'elementIds' o activar 'useSelection: true' con un elemento seleccionado en Revit.");

        var resolved = new List<ElementId>(rawIds.Count);
        foreach (var rawId in rawIds)
        {
            var eid = new ElementId(rawId);
            if (doc.GetElement(eid) == null)
                throw new InvalidOperationException($"No existe ningún elemento con ElementId {rawId} en la familia activa.");
            resolved.Add(eid);
        }

        return resolved;
    }

    private static void SetFormIsSolid(GenericForm form, bool isSolid)
    {
        var param = form.get_Parameter(BuiltInParameter.ELEMENT_IS_CUTTING);
        if (param == null || param.IsReadOnly)
            throw new InvalidOperationException($"No es posible cambiar Solid/Void en el elemento {form.Id.Value}.");

        // En Revit: 0 = Solid, 1 = Void
        param.Set(isSolid ? 0 : 1);
    }

    private static void PrepareElementsForModification(Document doc, IEnumerable<ElementId> ids, bool unpinIfNeeded)
    {
        foreach (var id in ids)
        {
            var elem = doc.GetElement(id);
            if (elem != null && elem.Pinned)
            {
                if (!unpinIfNeeded)
                    throw new InvalidOperationException(
                        $"El elemento {id.Value} está bloqueado (Pinned). Pasa 'unpinIfNeeded: true' si deseas desbloquearlo automáticamente.");

                elem.Pinned = false;
            }
        }
    }

    private static List<Sketch> GetEditableSketches(Document doc, ElementId elementId)
    {
        var elem = doc.GetElement(elementId);
        return elem switch
        {
            Extrusion ext when ext.Sketch != null => new List<Sketch> { ext.Sketch },
            Blend blend => new[] { blend.BottomSketch, blend.TopSketch }.Where(s => s != null).ToList()!,
            SweptBlend sb => new[] { sb.BottomSketch, sb.TopSketch }.Where(s => s != null).ToList()!,
            Revolution rev when rev.Sketch != null => new List<Sketch> { rev.Sketch },
            _ => throw new InvalidOperationException(
                $"El elemento {elementId.Value} ({elem?.GetType().Name}) no tiene un croquis editable compatible.")
        };
    }

    private static void ApplySketchLoops(
        Document doc,
        Sketch sketch,
        List<List<Curve>> newLoops,
        bool unpinIfNeeded,
        ElementId ownerId,
        string txName)
    {
        if (newLoops.Count == 0)
            throw new ArgumentException("El nuevo perfil debe contener al menos un bucle cerrado.");

        var owner = doc.GetElement(ownerId);
        if (owner != null && owner.Pinned)
        {
            if (!unpinIfNeeded)
                throw new InvalidOperationException(
                    $"El elemento {ownerId.Value} está bloqueado (Pinned). Pasa 'unpinIfNeeded: true' para editar su perfil.");

            using var unpinTx = new Transaction(doc, "MCP: Desbloquear elemento");
            unpinTx.Start();
            owner.Pinned = false;
            unpinTx.Commit();
        }

        using var sketchScope = new SketchEditScope(doc, txName);
        sketchScope.Start(sketch.Id);

        using (var trans = new Transaction(doc, txName))
        {
            trans.Start();

            var oldCurveIds = sketch.GetAllElements()
                .Where(eid => doc.GetElement(eid) is CurveElement)
                .ToList();

            if (oldCurveIds.Count > 0)
                doc.Delete(oldCurveIds);

            foreach (var loop in newLoops)
            {
                foreach (var curve in loop)
                    doc.FamilyCreate.NewModelCurve(curve, sketch.SketchPlane);
            }

            trans.Commit();
        }

        sketchScope.Commit(new RebarFailuresPreprocessor());
    }

    private static List<List<Curve>> BuildLoopsFromRequest(EditFamilyGeometryRequest request, Plane plane)
    {
        if (request.ProfileLoops is { Count: > 0 })
        {
            var loops = new List<List<Curve>>();
            foreach (var rawPts in request.ProfileLoops)
            {
                if (rawPts.Count < 3)
                    throw new ArgumentException("Cada bucle en 'profileLoops' requiere al menos 3 puntos.");

                var pts = rawPts
                    .Select(p => ProjectPointOntoPlane(InspectFamilyHandler.ToXyzFeet(p), plane))
                    .ToList();

                // Si el último punto coincide con el primero, evitar segmento degenerado
                if (pts.Count > 3 && pts[0].DistanceTo(pts[^1]) < 1e-5)
                    pts.RemoveAt(pts.Count - 1);

                var loop = new List<Curve>(pts.Count);
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % pts.Count];
                    if (a.DistanceTo(b) < 1e-5)
                        throw new ArgumentException("Dos vértices consecutivos en 'profileLoops' coinciden.");
                    loop.Add(Line.CreateBound(a, b));
                }
                loops.Add(loop);
            }
            return loops;
        }

        if (request.Rectangle != null)
        {
            var r = request.Rectangle;
            double minU, minV, maxU, maxV;

            if (r.WidthMeters.HasValue && r.DepthMeters.HasValue)
            {
                if (r.WidthMeters.Value <= 0 || r.DepthMeters.Value <= 0)
                    throw new ArgumentException("'widthMeters' y 'depthMeters' deben ser mayores que cero.");

                double cx = 0, cy = 0;
                if (r.CenterMeters != null)
                {
                    XYZ centerFeet = ProjectPointOntoPlane(InspectFamilyHandler.ToXyzFeet(r.CenterMeters), plane);
                    XYZ rel = centerFeet - plane.Origin;
                    cx = rel.DotProduct(plane.XVec);
                    cy = rel.DotProduct(plane.YVec);
                }

                double hw = UnitConverter.MetersToFeet(r.WidthMeters.Value) * 0.5;
                double hd = UnitConverter.MetersToFeet(r.DepthMeters.Value) * 0.5;
                minU = cx - hw;
                maxU = cx + hw;
                minV = cy - hd;
                maxV = cy + hd;
            }
            else if (r.MinX.HasValue && r.MinY.HasValue && r.MaxX.HasValue && r.MaxY.HasValue)
            {
                minU = UnitConverter.MetersToFeet(r.MinX.Value);
                minV = UnitConverter.MetersToFeet(r.MinY.Value);
                maxU = UnitConverter.MetersToFeet(r.MaxX.Value);
                maxV = UnitConverter.MetersToFeet(r.MaxY.Value);
                if (maxU <= minU || maxV <= minV)
                    throw new ArgumentException("En 'rectangle', maxX debe ser mayor que minX y maxY mayor que minY.");
            }
            else
            {
                throw new ArgumentException("En 'rectangle' especifica ('widthMeters' y 'depthMeters') o ('minX', 'minY', 'maxX', 'maxY').");
            }

            var p0 = plane.Origin + plane.XVec * minU + plane.YVec * minV;
            var p1 = plane.Origin + plane.XVec * maxU + plane.YVec * minV;
            var p2 = plane.Origin + plane.XVec * maxU + plane.YVec * maxV;
            var p3 = plane.Origin + plane.XVec * minU + plane.YVec * maxV;

            return new List<List<Curve>>
            {
                new()
                {
                    Line.CreateBound(p0, p1),
                    Line.CreateBound(p1, p2),
                    Line.CreateBound(p2, p3),
                    Line.CreateBound(p3, p0)
                }
            };
        }

        if (request.Circle != null)
        {
            if (request.Circle.RadiusMeters <= 0)
                throw new ArgumentException("El radio 'circle.radiusMeters' debe ser mayor que cero.");

            double radiusFeet = UnitConverter.MetersToFeet(request.Circle.RadiusMeters);
            XYZ centerFeet = request.Circle.CenterMeters != null
                ? ProjectPointOntoPlane(InspectFamilyHandler.ToXyzFeet(request.Circle.CenterMeters), plane)
                : plane.Origin;

            var arc1 = Arc.Create(centerFeet, radiusFeet, 0, Math.PI, plane.XVec, plane.YVec);
            var arc2 = Arc.Create(centerFeet, radiusFeet, Math.PI, 2 * Math.PI, plane.XVec, plane.YVec);

            return new List<List<Curve>> { new() { arc1, arc2 } };
        }

        throw new ArgumentException("Debes especificar 'profileLoops', 'rectangle' o 'circle' para definir la geometría 2D.");
    }

    private static XYZ ProjectPointOntoPlane(XYZ point, Plane plane)
    {
        XYZ diff = point - plane.Origin;
        double dist = diff.DotProduct(plane.Normal);
        return point - plane.Normal * dist;
    }

    private static XYZ ComputeProfileCentroidFeet(CurveArrArray profile)
    {
        var pts = new List<XYZ>();
        foreach (CurveArray ca in profile)
        {
            foreach (Curve c in ca)
                pts.Add(c.GetEndPoint(0));
        }

        if (pts.Count == 0)
            return XYZ.Zero;

        XYZ sum = XYZ.Zero;
        foreach (var p in pts)
            sum += p;
        return sum / pts.Count;
    }

    private static IEnumerable<Curve> ScaleCurveInPlane(
        Curve curve,
        Plane plane,
        XYZ pivot,
        double sx,
        double sy,
        bool isUniform)
    {
        XYZ ScalePt(XYZ p)
        {
            XYZ d = p - pivot;
            double u = d.DotProduct(plane.XVec);
            double v = d.DotProduct(plane.YVec);
            return pivot + plane.XVec * (u * sx) + plane.YVec * (v * sy);
        }

        if (curve is Line line)
            return new Curve[] { Line.CreateBound(ScalePt(line.GetEndPoint(0)), ScalePt(line.GetEndPoint(1))) };

        if (curve is Arc arc)
        {
            if (!isUniform)
                throw new InvalidOperationException(
                    "El escalado no uniforme (scaleX != scaleY) no está soportado para arcos circulares; usa un 'scaleFactor' uniforme o 'ReplaceExtrusionProfile'.");

            if (!arc.IsBound || arc.GetEndPoint(0).DistanceTo(arc.GetEndPoint(1)) < 1e-5)
            {
                XYZ scaledCenter = ScalePt(arc.Center);
                double scaledRadius = arc.Radius * sx;
                return new Curve[]
                {
                    Arc.Create(scaledCenter, scaledRadius, 0, Math.PI, plane.XVec, plane.YVec),
                    Arc.Create(scaledCenter, scaledRadius, Math.PI, 2 * Math.PI, plane.XVec, plane.YVec)
                };
            }

            XYZ p0 = ScalePt(arc.GetEndPoint(0));
            XYZ p1 = ScalePt(arc.GetEndPoint(1));
            XYZ pMid = ScalePt(arc.Evaluate(0.5, true));
            return new Curve[] { Arc.Create(p0, p1, pMid) };
        }

        throw new InvalidOperationException($"Tipo de curva '{curve.GetType().Name}' no soportado para escalado automático.");
    }

    private static CurveLoop OffsetCurveLoopDeterministic(CurveLoop loop, double offsetFeet, Plane plane)
    {
        double origArea = ComputeSignedArea2D(loop, plane);
        var posLoop = CurveLoop.CreateViaOffset(loop, offsetFeet, plane.Normal);
        var negLoop = CurveLoop.CreateViaOffset(loop, -offsetFeet, plane.Normal);

        double posArea = Math.Abs(ComputeSignedArea2D(posLoop, plane));
        double negArea = Math.Abs(ComputeSignedArea2D(negLoop, plane));
        double origAbs = Math.Abs(origArea);

        // offsetFeet > 0 debe agrandar el área; offsetFeet < 0 debe reducirla
        if (offsetFeet > 0)
            return posArea >= negArea && posArea >= origAbs ? posLoop : negLoop;
        else
            return posArea <= negArea && posArea <= origAbs ? posLoop : negLoop;
    }

    private static double ComputeSignedArea2D(CurveLoop loop, Plane plane)
    {
        var pts = new List<XYZ>();
        foreach (Curve c in loop)
        {
            var tess = c.Tessellate();
            for (int i = 0; i < tess.Count - 1; i++)
                pts.Add(tess[i]);
        }

        if (pts.Count < 3)
            return 0;

        double area = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            XYZ a = pts[i] - plane.Origin;
            XYZ b = pts[(i + 1) % pts.Count] - plane.Origin;
            double ax = a.DotProduct(plane.XVec);
            double ay = a.DotProduct(plane.YVec);
            double bx = b.DotProduct(plane.XVec);
            double by = b.DotProduct(plane.YVec);
            area += (ax * by) - (bx * ay);
        }

        return area * 0.5;
    }

    private static XYZ ComputeCombinedCenterFeet(Document doc, IEnumerable<ElementId> ids)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        bool any = false;

        foreach (var id in ids)
        {
            var bb = doc.GetElement(id)?.get_BoundingBox(null);
            if (bb == null) continue;
            any = true;
            minX = Math.Min(minX, bb.Min.X);
            minY = Math.Min(minY, bb.Min.Y);
            minZ = Math.Min(minZ, bb.Min.Z);
            maxX = Math.Max(maxX, bb.Max.X);
            maxY = Math.Max(maxY, bb.Max.Y);
            maxZ = Math.Max(maxZ, bb.Max.Z);
        }

        return any
            ? new XYZ((minX + maxX) * 0.5, (minY + maxY) * 0.5, (minZ + maxZ) * 0.5)
            : XYZ.Zero;
    }

    private static void AssignElementParameterValue(Parameter param, EditFamilyGeometryRequest request)
    {
        ForgeTypeId dataType = param.Definition.GetDataType();

        switch (param.StorageType)
        {
            case StorageType.Double:
                if (!request.NumericValue.HasValue)
                    throw new ArgumentException($"El parámetro '{param.Definition.Name}' es numérico; proporciona 'numericValue'.");
                param.Set(ConvertInputDoubleToInternal(request.NumericValue.Value, dataType));
                break;

            case StorageType.Integer:
                if (request.BooleanValue.HasValue)
                    param.Set(request.BooleanValue.Value ? 1 : 0);
                else if (request.IntegerValue.HasValue)
                    param.Set(request.IntegerValue.Value);
                else
                    throw new ArgumentException($"El parámetro '{param.Definition.Name}' es entero/booleano; proporciona 'integerValue' o 'booleanValue'.");
                break;

            case StorageType.String:
                if (request.StringValue == null)
                    throw new ArgumentException($"El parámetro '{param.Definition.Name}' es texto; proporciona 'stringValue'.");
                param.Set(request.StringValue);
                break;

            case StorageType.ElementId:
                if (!request.IntegerValue.HasValue)
                    throw new ArgumentException($"El parámetro '{param.Definition.Name}' es ElementId; proporciona 'integerValue'.");
                param.Set(new ElementId((long)request.IntegerValue.Value));
                break;

            default:
                throw new InvalidOperationException($"Tipo de almacenamiento '{param.StorageType}' no soportado directamente.");
        }
    }

    private static void AssignFamilyManagerValue(FamilyManager famMgr, FamilyParameter fp, EditFamilyGeometryRequest request)
    {
        ForgeTypeId dataType = fp.Definition.GetDataType();

        switch (fp.StorageType)
        {
            case StorageType.Double:
                if (!request.NumericValue.HasValue)
                    throw new ArgumentException($"El parámetro '{fp.Definition.Name}' es numérico; proporciona 'numericValue'.");
                famMgr.Set(fp, ConvertInputDoubleToInternal(request.NumericValue.Value, dataType));
                break;

            case StorageType.Integer:
                if (request.BooleanValue.HasValue)
                    famMgr.Set(fp, request.BooleanValue.Value ? 1 : 0);
                else if (request.IntegerValue.HasValue)
                    famMgr.Set(fp, request.IntegerValue.Value);
                else
                    throw new ArgumentException($"El parámetro '{fp.Definition.Name}' es entero/booleano; proporciona 'integerValue' o 'booleanValue'.");
                break;

            case StorageType.String:
                if (request.StringValue == null)
                    throw new ArgumentException($"El parámetro '{fp.Definition.Name}' es texto; proporciona 'stringValue'.");
                famMgr.Set(fp, request.StringValue);
                break;

            case StorageType.ElementId:
                if (!request.IntegerValue.HasValue)
                    throw new ArgumentException($"El parámetro '{fp.Definition.Name}' es ElementId; proporciona 'integerValue'.");
                famMgr.Set(fp, new ElementId((long)request.IntegerValue.Value));
                break;

            default:
                throw new InvalidOperationException($"Tipo de almacenamiento '{fp.StorageType}' no soportado directamente.");
        }
    }

    private static double ConvertInputDoubleToInternal(double value, ForgeTypeId dataType)
    {
        if (dataType == SpecTypeId.Length)
            return UnitConverter.MetersToFeet(value);
        if (dataType == SpecTypeId.Area)
            return UnitConverter.SquareMetersToSquareFeet(value);
        if (dataType == SpecTypeId.Volume)
            return UnitConverter.CubicMetersToCubicFeet(value);
        if (dataType == SpecTypeId.Angle)
            return UnitConverter.DegreesToRadians(value);

        return value;
    }

    private static EditFamilyGeometryResponse BuildResponse(
        string operation,
        Document doc,
        IList<ElementId> affectedIds,
        string message)
    {
        BoundingBoxMetersDto? bb = affectedIds.Count == 1
            ? (doc.GetElement(affectedIds[0]) is Element el ? InspectFamilyHandler.BuildBoundingBoxMeters(el) : null)
            : null;

        return new EditFamilyGeometryResponse
        {
            Success = true,
            Operation = operation,
            AffectedElementIds = affectedIds.Select(id => id.Value).ToList(),
            Message = message,
            UpdatedBoundingBox = bb
        };
    }
}
