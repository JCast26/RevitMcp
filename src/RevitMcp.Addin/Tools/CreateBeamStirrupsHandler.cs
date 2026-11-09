using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Motor de modelado de estribos de refuerzo para vigas (Structural Framing).
/// Implementa la distribución sismorresistente estricta en 3 conjuntos:
/// - Conjunto 1 (Inicio): Number with Spacing, separado S0 de la cara del apoyo.
/// - Conjunto 2 (Centro / Resto): Maximum Spacing sobre el vano remanente sin duplicar barras de extremos.
/// - Conjunto 3 (Fin): Number with Spacing, separado S0 de la cara del apoyo opuesto.
/// </summary>
public static class CreateBeamStirrupsHandler
{
    public static CreateBeamStirrupsResponse Execute(Document doc, CreateBeamStirrupsRequest request)
    {
        var response = new CreateBeamStirrupsResponse();

        if (request.BeamElementIds == null || request.BeamElementIds.Count == 0)
        {
            response.Success = false;
            response.Message = "No se especificó ninguna viga para armar.";
            return response;
        }

        // 1. Resolver el RebarBarType
        var barType = ResolveBarType(doc, request.RebarBarTypeName);
        if (barType == null)
        {
            response.Success = false;
            response.Message = $"No se encontró el tipo de barra de refuerzo '{request.RebarBarTypeName}'. Verifique los tipos disponibles en el proyecto.";
            return response;
        }

        // 2. Resolver el RebarShape (Estribo cerrado / StirrupTie)
        var rebarShape = ResolveRebarShape(doc, request.RebarShapeName);
        if (rebarShape == null)
        {
            response.Success = false;
            response.Message = $"No se encontró la forma de estribo '{request.RebarShapeName}'. Asegúrese de tener cargadas familias de formas de estribos en Revit.";
            return response;
        }

        // Convertir parámetros de entrada (en metros) a unidades internas de Revit (pies)
        double s0Feet = UnitConverter.MetersToFeet(Math.Max(0.01, request.FirstStirrupOffsetMeters));
        int n1 = Math.Max(1, request.Zone1Count);
        double s1Feet = UnitConverter.MetersToFeet(Math.Max(0.02, request.Zone1SpacingMeters));

        double s2MaxFeet = UnitConverter.MetersToFeet(Math.Max(0.05, request.Zone2MaxSpacingMeters));

        int n3 = request.IsSymmetric ? n1 : Math.Max(1, request.Zone3Count);
        double s3Feet = request.IsSymmetric ? s1Feet : UnitConverter.MetersToFeet(Math.Max(0.02, request.Zone3SpacingMeters));

        var view3D = doc.ActiveView as View3D;

        using var trans = new Transaction(doc, "BIM Suite - Crear Estribos en Vigas");
        var transStatus = trans.Start();
        if (transStatus != TransactionStatus.Started)
        {
            response.Success = false;
            response.Message = "No se pudo iniciar la transacción en el modelo de Revit.";
            return response;
        }

        try
        {
            foreach (var beamIdVal in request.BeamElementIds)
            {
                var summary = new BeamStirrupsCreatedSummary
                {
                    BeamElementId = beamIdVal
                };

                var elem = doc.GetElement(new ElementId(beamIdVal));
                if (elem is not FamilyInstance beam || beam.Category?.Id.Value != (int)BuiltInCategory.OST_StructuralFraming)
                {
                    summary.Status = "Failed";
                    summary.ErrorMessage = $"El elemento ID {beamIdVal} no es una viga estructural válida.";
                    response.Results.Add(summary);
                    continue;
                }

                summary.BeamName = $"{beam.Symbol.FamilyName}: {beam.Name}";

                // Extraer geometría física real de la viga y luz libre neta
                var geom = BeamGeometryHelper.ExtractGeometry(doc, beam);
                summary.ClearSpanMeters = geom.ClearSpanMeters;

                var xVec = geom.TransverseDirection;
                var yVec = geom.UpDirection;
                var vDir = geom.Direction;
                var clearSpan = geom.ClearSpanFeet;

                var wStirrup = geom.StirrupWidthFeet;
                var hStirrup = geom.StirrupHeightFeet;
                var boxVecX = xVec * wStirrup;
                var boxVecY = yVec * hStirrup;

                // Desplazamiento transversal y vertical desde el eje LocationLine a la esquina inferior-izquierda
                var blOffset = xVec * geom.StirrupTMinFeet + yVec * geom.StirrupUMinFeet;

                double zone1Length = (n1 - 1) * s1Feet;
                double zone3Length = (n3 - 1) * s3Feet;

                // --- CONJUNTO 1: Confinamiento Inicial (Number with Spacing) ---
                var pAxis1 = geom.StartFacePoint + vDir * s0Feet;
                var origin1 = pAxis1 + blOffset;
                var rebar1 = Rebar.CreateFromRebarShape(doc, rebarShape, barType, beam, origin1, xVec, yVec);
                ApplyStirrupDimensions(rebar1, origin1, boxVecX, boxVecY, wStirrup, hStirrup);

                var acc1 = rebar1.GetShapeDrivenAccessor();
                acc1.SetLayoutAsNumberWithSpacing(n1, s1Feet, true, true, true);
                summary.Zone1StirrupsCount = n1;
                summary.RebarElementIds.Add(rebar1.Id.Value);

                if (view3D != null)
                {
                    rebar1.SetUnobscuredInView(view3D, true);
                }

                // --- CONJUNTO 3: Confinamiento Final (Number with Spacing) ---
                // El último estribo de la Zona 3 debe quedar a s0Feet de la cara interior final.
                // Con el array creciendo hacia +vDir, el primer estribo de la Zona 3 se ubica a:
                var pAxis3 = geom.EndFacePoint - vDir * (s0Feet + zone3Length);
                var origin3 = pAxis3 + blOffset;
                var rebar3 = Rebar.CreateFromRebarShape(doc, rebarShape, barType, beam, origin3, xVec, yVec);
                ApplyStirrupDimensions(rebar3, origin3, boxVecX, boxVecY, wStirrup, hStirrup);

                var acc3 = rebar3.GetShapeDrivenAccessor();
                acc3.SetLayoutAsNumberWithSpacing(n3, s3Feet, true, true, true);
                summary.Zone3StirrupsCount = n3;
                summary.RebarElementIds.Add(rebar3.Id.Value);

                if (view3D != null)
                {
                    rebar3.SetUnobscuredInView(view3D, true);
                }

                // --- CONJUNTO 2: Tramo Central / Resto (Maximum Spacing) ---
                double zone1EndDist = s0Feet + zone1Length;
                double zone3StartDist = clearSpan - (s0Feet + zone3Length);
                double gapLength = zone3StartDist - zone1EndDist;

                // Si hay vano remanente entre zonas de confinamiento mayor que el espaciado máximo
                if (gapLength > s2MaxFeet)
                {
                    var pAxis2 = geom.StartFacePoint + vDir * zone1EndDist;
                    var origin2 = pAxis2 + blOffset;
                    var rebar2 = Rebar.CreateFromRebarShape(doc, rebarShape, barType, beam, origin2, xVec, yVec);
                    ApplyStirrupDimensions(rebar2, origin2, boxVecX, boxVecY, wStirrup, hStirrup);

                    var acc2 = rebar2.GetShapeDrivenAccessor();
                    try
                    {
                        // Regla estricta: Maximum Spacing cubriendo el vano intermedio entre el fin de Zona 1 y el inicio de Zona 3
                        // Ambos extremos deben tener apagado el check (includeFirstBar = false e includeLastBar = false)
                        acc2.SetLayoutAsMaximumSpacing(s2MaxFeet, gapLength, true, false, false);
                    }
                    catch
                    {
                        // Si la sobrecarga rechaza false, false directamente, inicializar con true, true
                        acc2.SetLayoutAsMaximumSpacing(s2MaxFeet, gapLength, true, true, true);
                    }

                    // Asegurar explícitamente en el elemento Rebar que ambas casillas queden apagadas (desmarcadas)
                    try
                    {
                        if (rebar2.IncludeFirstBar)
                        {
                            rebar2.IncludeFirstBar = false;
                        }
                    }
                    catch { }

                    try
                    {
                        if (rebar2.IncludeLastBar)
                        {
                            rebar2.IncludeLastBar = false;
                        }
                    }
                    catch { }

                    summary.Zone2StirrupsCount = Math.Max(0, rebar2.Quantity);
                    if (summary.Zone2StirrupsCount == 0 && gapLength > s2MaxFeet)
                    {
                        int numSpaces = (int)Math.Ceiling(gapLength / s2MaxFeet);
                        summary.Zone2StirrupsCount = Math.Max(1, numSpaces - 1);
                    }

                    summary.RebarElementIds.Add(rebar2.Id.Value);

                    if (view3D != null)
                    {
                        rebar2.SetUnobscuredInView(view3D, true);
                    }
                }
                else
                {
                    summary.Zone2StirrupsCount = 0;
                }

                summary.Status = "Success";
                response.Results.Add(summary);
            }

            trans.Commit();
            response.Success = true;
            response.TotalBeamsProcessed = response.Results.Count(r => r.Status == "Success");
            response.Message = $"Se generaron estribos confinados exitosamente en {response.TotalBeamsProcessed} viga(s).";
        }
        catch (Exception ex)
        {
            if (trans.HasStarted()) trans.RollBack();
            response.Success = false;
            response.Message = $"Error al modelar estribos en Revit: {ex.Message}";
        }

        return response;
    }

    private static void ApplyStirrupDimensions(Rebar rebar, XYZ origin, XYZ boxVecX, XYZ boxVecY, double widthFeet, double heightFeet)
    {
        var acc = rebar.GetShapeDrivenAccessor();
        bool scaled = false;
        try
        {
            // ScaleToBox es el método oficial de la API de Revit para adaptar y ajustar
            // la forma del estribo a la caja rectangular de la sección de la viga
            acc.ScaleToBox(origin, boxVecX, boxVecY);
            scaled = true;
        }
        catch
        {
            scaled = false;
        }

        // Si ScaleToBox no fue soportado por la forma particular, ajustar parámetros de instancia comunes
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
        catch
        {
            // Ignorar si el parámetro no existe en esta forma de estribo
        }
    }

    private static RebarBarType? ResolveBarType(Document doc, string name)
    {
        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .ToList();

        if (string.IsNullOrWhiteSpace(name))
        {
            var saved = BeamStirrupsSettingsStore.Load();
            if (!string.IsNullOrWhiteSpace(saved?.RebarBarTypeName))
            {
                var match = types.FirstOrDefault(t => t.Name.Equals(saved.RebarBarTypeName, StringComparison.OrdinalIgnoreCase))
                         ?? types.FirstOrDefault(t => t.Name.Contains(saved.RebarBarTypeName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return types.FirstOrDefault(t => t.Name.Contains("3/8") || t.Name.Contains("10") || t.Name.Contains("#3"))
                ?? types.FirstOrDefault();
        }

        return types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault();
    }

    private static RebarShape? ResolveRebarShape(Document doc, string name)
    {
        var shapes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarShape))
            .Cast<RebarShape>()
            .ToList();

        var stirrupShapes = shapes.Where(s => s.RebarStyle == RebarStyle.StirrupTie).ToList();
        var pool = stirrupShapes.Any() ? stirrupShapes : shapes;

        if (string.IsNullOrWhiteSpace(name))
        {
            var saved = BeamStirrupsSettingsStore.Load();
            if (!string.IsNullOrWhiteSpace(saved?.RebarShapeName))
            {
                var match = pool.FirstOrDefault(s => s.Name.Equals(saved.RebarShapeName, StringComparison.OrdinalIgnoreCase))
                         ?? pool.FirstOrDefault(s => s.Name.Contains(saved.RebarShapeName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return pool.FirstOrDefault(s => s.Name.Contains("T1") || s.Name.Contains("51") || s.Name.Contains("Stirrup"))
                ?? pool.FirstOrDefault();
        }

        return pool.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? pool.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? pool.FirstOrDefault();
    }
}
