using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Manejador de la herramienta InspectFamily:
/// Inspecciona el documento de familia (.rfa) activo en el Editor de Familias de Revit,
/// devolviendo parámetros del FamilyManager, formas 3D (Extrusion, Blend, Revolution, Sweep, etc.),
/// planos de referencia, cotas, familias anidadas y la selección actual del usuario.
/// Todas las dimensiones y coordenadas se devuelven en METROS y ángulos en GRADOS.
/// </summary>
public static class InspectFamilyHandler
{
    public static InspectFamilyResponse Execute(UIDocument uidoc, InspectFamilyRequest request)
    {
        var doc = EnsureFamilyDocument(uidoc?.Document);

        var selectedIds = uidoc!.Selection.GetElementIds().Select(id => id.Value).ToList();
        HashSet<long>? filterIds = null;

        if (request.SelectedOnly)
        {
            filterIds = selectedIds.ToHashSet();
        }
        else if (request.ElementIds is { Count: > 0 })
        {
            filterIds = request.ElementIds.ToHashSet();
        }

        var response = new InspectFamilyResponse
        {
            Success = true,
            FamilyTitle = doc.Title,
            FamilyCategory = doc.OwnerFamily?.FamilyCategory?.Name ?? "Unknown",
            SelectedElementIds = selectedIds
        };

        // 1. Parámetros y tipos del FamilyManager
        var famMgr = doc.FamilyManager;
        if (famMgr != null)
        {
            response.CurrentTypeName = famMgr.CurrentType?.Name;

            foreach (FamilyType fType in famMgr.Types)
            {
                if (!string.IsNullOrWhiteSpace(fType.Name))
                    response.Types.Add(fType.Name);
            }

            if (request.IncludeParameters)
            {
                foreach (FamilyParameter fp in famMgr.Parameters)
                {
                    response.Parameters.Add(BuildFamilyParameterDto(fp, famMgr.CurrentType));
                }
            }
        }

        // 2. Formas 3D (Extrusion, Blend, Revolution, Sweep, SweptBlend, Form)
        var formsCollector = new FilteredElementCollector(doc)
            .OfClass(typeof(GenericForm))
            .Cast<GenericForm>();

        foreach (var form in formsCollector)
        {
            if (filterIds != null && !filterIds.Contains(form.Id.Value))
                continue;

            response.Forms.Add(BuildFormDto(doc, form, request.IncludeProfileCurves));
        }

        // 3. Planos de referencia
        if (request.IncludeReferencePlanes)
        {
            var refPlanes = new FilteredElementCollector(doc)
                .OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>();

            foreach (var rp in refPlanes)
            {
                if (filterIds != null && !filterIds.Contains(rp.Id.Value))
                    continue;

                response.ReferencePlanes.Add(new ReferencePlaneDto
                {
                    ElementId = rp.Id.Value,
                    Name = rp.Name,
                    BubbleEnd = ToPoint3DMeters(rp.BubbleEnd),
                    FreeEnd = ToPoint3DMeters(rp.FreeEnd),
                    Normal = RoundVector(rp.Normal),
                    IsPinned = rp.Pinned
                });
            }
        }

        // 4. Cotas / Dimensiones
        if (request.IncludeDimensions)
        {
            var dims = new FilteredElementCollector(doc)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>();

            foreach (var dim in dims)
            {
                if (dim is SpotDimension)
                    continue;

                if (filterIds != null && !filterIds.Contains(dim.Id.Value))
                    continue;

                response.Dimensions.Add(BuildDimensionDto(dim));
            }
        }

        // 5. Familias anidadas
        var nested = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();

        foreach (var fi in nested)
        {
            if (filterIds != null && !filterIds.Contains(fi.Id.Value))
                continue;

            Point3D? loc = (fi.Location as LocationPoint) is LocationPoint lp
                ? ToPoint3DMeters(lp.Point)
                : null;

            response.NestedInstances.Add(new NestedInstanceDto
            {
                ElementId = fi.Id.Value,
                FamilyName = fi.Symbol?.FamilyName ?? string.Empty,
                TypeName = fi.Symbol?.Name ?? fi.Name,
                Location = loc,
                BoundingBox = BuildBoundingBoxMeters(fi),
                IsPinned = fi.Pinned
            });
        }

        return response;
    }

    internal static Document EnsureFamilyDocument(Document? doc)
    {
        if (doc == null)
            throw new InvalidOperationException("No hay ningún documento activo en Autodesk Revit.");

        if (!doc.IsFamilyDocument)
            throw new InvalidOperationException(
                $"El documento activo '{doc.Title}' es un proyecto (.rvt), no una familia (.rfa). " +
                "Abre la familia en el Editor de Familias (Edit Family) para inspeccionarla o modificarla.");

        return doc;
    }

    internal static BoundingBoxMetersDto? BuildBoundingBoxMeters(Element element)
    {
        var bb = element.get_BoundingBox(null);
        if (bb == null)
            return null;

        var min = ToPoint3DMeters(bb.Min);
        var max = ToPoint3DMeters(bb.Max);
        var size = new Point3D(
            Math.Round(max.X - min.X, 4),
            Math.Round(max.Y - min.Y, 4),
            Math.Round(max.Z - min.Z, 4));
        var center = new Point3D(
            Math.Round((min.X + max.X) * 0.5, 4),
            Math.Round((min.Y + max.Y) * 0.5, 4),
            Math.Round((min.Z + max.Z) * 0.5, 4));

        return new BoundingBoxMetersDto
        {
            Min = min,
            Max = max,
            Size = size,
            Center = center
        };
    }

    internal static Point3D ToPoint3DMeters(XYZ pt) => new(
        Math.Round(UnitConverter.FeetToMeters(pt.X), 4),
        Math.Round(UnitConverter.FeetToMeters(pt.Y), 4),
        Math.Round(UnitConverter.FeetToMeters(pt.Z), 4));

    internal static XYZ ToXyzFeet(Point3D pt) => new(
        UnitConverter.MetersToFeet(pt.X),
        UnitConverter.MetersToFeet(pt.Y),
        UnitConverter.MetersToFeet(pt.Z));

    private static Point3D RoundVector(XYZ v) => new(
        Math.Round(v.X, 6),
        Math.Round(v.Y, 6),
        Math.Round(v.Z, 6));

    private static FamilyParameterDto BuildFamilyParameterDto(FamilyParameter fp, FamilyType? currentType)
    {
        ForgeTypeId dataType = fp.Definition.GetDataType();
        object? val = null;

        if (currentType != null && currentType.HasValue(fp))
        {
            switch (fp.StorageType)
            {
                case StorageType.Double:
                    double? raw = currentType.AsDouble(fp);
                    if (raw.HasValue)
                    {
                        if (dataType == SpecTypeId.Length)
                            val = Math.Round(UnitConverter.FeetToMeters(raw.Value), 4);
                        else if (dataType == SpecTypeId.Area)
                            val = Math.Round(UnitConverter.SquareFeetToSquareMeters(raw.Value), 4);
                        else if (dataType == SpecTypeId.Volume)
                            val = Math.Round(UnitConverter.CubicFeetToCubicMeters(raw.Value), 4);
                        else if (dataType == SpecTypeId.Angle)
                            val = Math.Round(UnitConverter.RadiansToDegrees(raw.Value), 4);
                        else
                            val = currentType.AsValueString(fp) ?? (object)Math.Round(raw.Value, 6);
                    }
                    break;

                case StorageType.Integer:
                    val = currentType.AsInteger(fp);
                    break;

                case StorageType.String:
                    val = currentType.AsString(fp);
                    break;

                case StorageType.ElementId:
                    val = currentType.AsElementId(fp)?.Value;
                    break;
            }
        }

        return new FamilyParameterDto
        {
            Id = fp.Id.Value,
            Name = fp.Definition.Name,
            Group = fp.Definition.GetGroupTypeId().TypeId,
            DataType = dataType.TypeId,
            StorageType = fp.StorageType.ToString(),
            IsInstance = fp.IsInstance,
            IsReporting = fp.IsReporting,
            Formula = string.IsNullOrEmpty(fp.Formula) ? null : fp.Formula,
            Value = val
        };
    }

    private static FamilyFormDto BuildFormDto(Document doc, GenericForm form, bool includeProfileCurves)
    {
        var dto = new FamilyFormDto
        {
            ElementId = form.Id.Value,
            Kind = form.GetType().Name,
            Name = form.Name,
            IsSolid = form.IsSolid,
            IsPinned = form.Pinned,
            BoundingBox = BuildBoundingBoxMeters(form)
        };

        switch (form)
        {
            case Extrusion ext:
                dto.StartOffsetMeters = Math.Round(UnitConverter.FeetToMeters(ext.StartOffset), 4);
                dto.EndOffsetMeters = Math.Round(UnitConverter.FeetToMeters(ext.EndOffset), 4);
                PopulateSketchInfo(ext.Sketch, dto, includeProfileCurves, isTopProfile: false);
                break;

            case Blend blend:
                dto.StartOffsetMeters = Math.Round(UnitConverter.FeetToMeters(blend.BottomOffset), 4);
                dto.EndOffsetMeters = Math.Round(UnitConverter.FeetToMeters(blend.TopOffset), 4);
                PopulateSketchInfo(blend.BottomSketch, dto, includeProfileCurves, isTopProfile: false);
                if (includeProfileCurves && blend.TopSketch != null)
                {
                    dto.TopProfileLoops = ExtractProfileLoops(blend.TopSketch.Profile);
                }
                break;

            case Revolution rev:
                dto.StartAngleDegrees = Math.Round(UnitConverter.RadiansToDegrees(rev.StartAngle), 4);
                dto.EndAngleDegrees = Math.Round(UnitConverter.RadiansToDegrees(rev.EndAngle), 4);
                PopulateSketchInfo(rev.Sketch, dto, includeProfileCurves, isTopProfile: false);
                break;

            case Sweep sweep:
                if (includeProfileCurves)
                {
                    if (sweep.PathSketch != null)
                        dto.ProfileLoops = ExtractProfileLoops(sweep.PathSketch.Profile);
                    if (sweep.ProfileSketch != null)
                        dto.TopProfileLoops = ExtractProfileLoops(sweep.ProfileSketch.Profile);
                }
                break;

            case SweptBlend sb:
                PopulateSketchInfo(sb.BottomSketch, dto, includeProfileCurves, isTopProfile: false);
                if (includeProfileCurves && sb.TopSketch != null)
                {
                    dto.TopProfileLoops = ExtractProfileLoops(sb.TopSketch.Profile);
                }
                PopulateSweptBlendSymbolInfo(doc, sb, dto);
                break;
        }

        if (form.Combinations != null && !form.Combinations.IsEmpty)
        {
            var combIds = new List<long>();
            foreach (GeomCombination gc in form.Combinations)
            {
                foreach (CombinableElement ce in gc.AllMembers)
                {
                    if (ce.Id != form.Id)
                        combIds.Add(ce.Id.Value);
                }
            }
            if (combIds.Count > 0)
            {
                dto.AssociatedParameters["CombinedWithElementIds"] = string.Join(", ", combIds.Distinct());
            }
        }

        var famMgr = doc.FamilyManager;
        if (famMgr != null)
        {
            foreach (Parameter p in form.Parameters)
            {
                var assoc = famMgr.GetAssociatedFamilyParameter(p);
                if (assoc != null)
                {
                    dto.AssociatedParameters[p.Definition.Name] = assoc.Definition.Name;
                }
            }
        }

        return dto;
    }

    private static void PopulateSweptBlendSymbolInfo(Document doc, SweptBlend sb, FamilyFormDto dto)
    {
        FamilySymbol? sym1 = sb.BottomProfileSymbol?.Profile;
        if (sym1 == null)
        {
            var p1Id = sb.get_Parameter(BuiltInParameter.PROFILE1_FAM_TYPE)?.AsElementId();
            if (p1Id != null && p1Id != ElementId.InvalidElementId)
                sym1 = doc.GetElement(p1Id) as FamilySymbol;
        }

        FamilySymbol? sym2 = sb.TopProfileSymbol?.Profile;
        if (sym2 == null)
        {
            var p2Id = sb.get_Parameter(BuiltInParameter.PROFILE2_FAM_TYPE)?.AsElementId();
            if (p2Id != null && p2Id != ElementId.InvalidElementId)
                sym2 = doc.GetElement(p2Id) as FamilySymbol;
        }

        if (sym1 != null)
        {
            dto.AssociatedParameters["Profile1Symbol"] = $"{sym1.FamilyName} : {sym1.Name} (Id: {sym1.Id.Value})";
            var famMgr = doc.FamilyManager;
            if (famMgr != null)
            {
                foreach (Parameter p in sym1.Parameters)
                {
                    var assoc = famMgr.GetAssociatedFamilyParameter(p);
                    if (assoc != null)
                    {
                        dto.AssociatedParameters[$"ProfileSymbol.{p.Definition.Name}"] = assoc.Definition.Name;
                    }
                }
            }
        }

        if (sym2 != null)
        {
            dto.AssociatedParameters["Profile2Symbol"] = $"{sym2.FamilyName} : {sym2.Name} (Id: {sym2.Id.Value})";
        }
    }

    private static void PopulateSketchInfo(Sketch? sketch, FamilyFormDto dto, bool includeProfileCurves, bool isTopProfile)
    {
        if (sketch == null)
            return;

        if (!isTopProfile && sketch.SketchPlane != null)
        {
            var plane = sketch.SketchPlane.GetPlane();
            dto.SketchPlaneOrigin = ToPoint3DMeters(plane.Origin);
            dto.SketchPlaneNormal = RoundVector(plane.Normal);
        }

        if (includeProfileCurves)
        {
            var loops = ExtractProfileLoops(sketch.Profile);
            if (isTopProfile)
                dto.TopProfileLoops = loops;
            else
                dto.ProfileLoops = loops;
        }
    }

    private static List<List<SketchSegmentDto>> ExtractProfileLoops(CurveArrArray? profile)
    {
        var result = new List<List<SketchSegmentDto>>();
        if (profile == null)
            return result;

        foreach (CurveArray curveArray in profile)
        {
            var loop = new List<SketchSegmentDto>();
            foreach (Curve curve in curveArray)
            {
                var seg = new SketchSegmentDto
                {
                    CurveType = curve.GetType().Name,
                    Start = curve.IsBound ? ToPoint3DMeters(curve.GetEndPoint(0)) : new Point3D(),
                    End = curve.IsBound ? ToPoint3DMeters(curve.GetEndPoint(1)) : new Point3D(),
                    MidPoint = curve.IsBound ? ToPoint3DMeters(curve.Evaluate(0.5, true)) : null
                };

                if (curve is Arc arc)
                {
                    seg.Center = ToPoint3DMeters(arc.Center);
                    seg.RadiusMeters = Math.Round(UnitConverter.FeetToMeters(arc.Radius), 4);
                    if (!curve.IsBound)
                    {
                        seg.Start = ToPoint3DMeters(arc.Center + arc.XDirection * arc.Radius);
                        seg.End = seg.Start;
                    }
                }

                loop.Add(seg);
            }
            result.Add(loop);
        }

        return result;
    }

    private static FamilyDimensionDto BuildDimensionDto(Dimension dim)
    {
        double? valMeters = null;
        if (dim.Value.HasValue)
        {
            valMeters = dim.DimensionShape == DimensionShape.Angular
                ? Math.Round(UnitConverter.RadiansToDegrees(dim.Value.Value), 4)
                : Math.Round(UnitConverter.FeetToMeters(dim.Value.Value), 4);
        }

        bool? isLocked = null;
        try
        {
            if (dim.IsLocked is bool locked)
                isLocked = locked;
        }
        catch
        {
            // Dimensiones multisegmento no exponen IsLocked único
        }

        string? labelName = null;
        try
        {
            labelName = dim.FamilyLabel?.Definition?.Name;
        }
        catch
        {
            // Algunas cotas del sistema no soportan FamilyLabel
        }

        return new FamilyDimensionDto
        {
            ElementId = dim.Id.Value,
            ValueMeters = valMeters,
            IsLocked = isLocked,
            LabelParameterName = labelName
        };
    }
}
