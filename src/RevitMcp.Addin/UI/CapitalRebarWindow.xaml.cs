using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI.Selection;
using RevitMcp.Addin.Tools;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;
using WpfColor = System.Windows.Media.Color;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfLine = System.Windows.Shapes.Line;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace RevitMcp.Addin.UI;

public sealed class CapitalSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem)
    {
        return elem is FamilyInstance fi &&
               fi.Category != null &&
               fi.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}

public partial class CapitalRebarWindow : Window
{
    private readonly Document _doc;
    private List<FamilyInstance> _selectedCapitals;
    private CapitalGeometryInfo? _sampleGeom;
    private bool _isInitializing = true;

    public bool UserWantsToPickCapitals { get; private set; }
    public CreateCapitalRebarRequest? BuiltRequest { get; private set; }

    public CapitalRebarWindow(Document doc, List<FamilyInstance> selectedCapitals)
    {
        InitializeComponent();

        _doc = doc;
        _selectedCapitals = selectedCapitals ?? new List<FamilyInstance>();

        LoadProjectCatalogs();
        ApplySavedSettings();
        RefreshSelectionInfo();

        _isInitializing = false;
        Loaded += (_, _) => UpdateDiagrams();
    }

    public void UpdateSelection(List<FamilyInstance> selectedCapitals)
    {
        _selectedCapitals = selectedCapitals ?? new List<FamilyInstance>();
        UserWantsToPickCapitals = false;
        RefreshSelectionInfo();
        UpdateDiagrams();
    }

    private void LoadProjectCatalogs()
    {
        var barTypes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .OrderBy(b => b.BarNominalDiameter > 0 ? b.BarNominalDiameter : b.BarModelDiameter)
            .ThenBy(b => b.Name)
            .ToList();

        CmbVertBarType.Items.Clear();
        CmbStirrupBarType.Items.Clear();
        foreach (var bt in barTypes)
        {
            CmbVertBarType.Items.Add(bt.Name);
            CmbStirrupBarType.Items.Add(bt.Name);
        }

        var hookTypes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .OrderBy(h => h.Style)
            .ThenBy(h => h.HookAngle)
            .ThenBy(h => h.Name)
            .ToList();

        CmbVertHookType.Items.Clear();
        foreach (var ht in hookTypes.Where(h => h.Style == RebarStyle.Standard).Concat(hookTypes.Where(h => h.Style != RebarStyle.Standard)))
        {
            CmbVertHookType.Items.Add(ht.Name);
        }

        CmbStirrupHookType.Items.Clear();
        foreach (var ht in hookTypes.Where(h => h.Style == RebarStyle.StirrupTie).Concat(hookTypes.Where(h => h.Style != RebarStyle.StirrupTie)))
        {
            CmbStirrupHookType.Items.Add(ht.Name);
        }

        var stirrupShapes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarShape))
            .Cast<RebarShape>()
            .Where(s => s.RebarStyle == RebarStyle.StirrupTie &&
                        !string.Equals(s.Name, "00", StringComparison.OrdinalIgnoreCase) &&
                        !s.Name.StartsWith("M_00", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name)
            .ToList();

        CmbStirrupShape.Items.Clear();
        foreach (var sh in stirrupShapes)
        {
            CmbStirrupShape.Items.Add(sh.Name);
        }
        if (CmbStirrupShape.Items.Count == 0)
        {
            CmbStirrupShape.Items.Add("Automática (por curvas)");
        }
    }

    private void ApplySavedSettings()
    {
        var saved = CapitalRebarSettingsStore.Load();

        SelectOrClosestBar(CmbVertBarType, saved.VerticalBarTypeName, 12.7);
        SelectOrClosestBar(CmbStirrupBarType, saved.StirrupBarTypeName, 9.5);
        SelectOrPreferredHook(CmbVertHookType, saved.VerticalHookTypeName, 90.0);
        SelectOrPreferredHook(CmbStirrupHookType, saved.StirrupHookTypeName, 135.0);
        SelectOrPreferredShape(CmbStirrupShape, saved.StirrupShapeName);

        TxtVertSpacing.Text = saved.VerticalSpacingMeters.ToString("0.00", CultureInfo.InvariantCulture);
        TxtStirrupSpacing.Text = saved.StirrupSpacingMeters.ToString("0.00", CultureInfo.InvariantCulture);
        ChkUseElementCover.IsChecked = saved.UseElementCover;
        TxtCoverMeters.Text = saved.CoverMeters.ToString("0.000", CultureInfo.InvariantCulture);
        TxtCoverMeters.IsEnabled = !saved.UseElementCover;
    }

    private void SelectOrClosestBar(ComboBox cmb, string savedName, double targetMm)
    {
        if (cmb.Items.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(savedName))
        {
            foreach (object item in cmb.Items)
            {
                if (string.Equals(item?.ToString(), savedName, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedItem = item;
                    return;
                }
            }
        }

        double targetFeet = UnitConverter.MetersToFeet(targetMm / 1000.0);
        var barTypes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .ToList();

        var best = barTypes
            .OrderBy(t => Math.Abs((t.BarNominalDiameter > 0 ? t.BarNominalDiameter : t.BarModelDiameter) - targetFeet))
            .FirstOrDefault();

        if (best != null)
        {
            cmb.SelectedItem = best.Name;
        }
        if (cmb.SelectedIndex < 0) cmb.SelectedIndex = 0;
    }

    private void SelectOrPreferredHook(ComboBox cmb, string savedName, double preferredDeg)
    {
        if (cmb.Items.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(savedName))
        {
            foreach (object item in cmb.Items)
            {
                if (string.Equals(item?.ToString(), savedName, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedItem = item;
                    return;
                }
            }
        }

        string tag = preferredDeg.ToString("0", CultureInfo.InvariantCulture);
        foreach (object item in cmb.Items)
        {
            string s = item?.ToString() ?? "";
            if (s.Contains(tag, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedItem = item;
                return;
            }
        }

        var hooks = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .ToList();

        double targetRad = preferredDeg * Math.PI / 180.0;
        var best = hooks.OrderBy(h => Math.Abs(h.HookAngle - targetRad)).FirstOrDefault();
        if (best != null) cmb.SelectedItem = best.Name;
        if (cmb.SelectedIndex < 0) cmb.SelectedIndex = 0;
    }

    private void SelectOrPreferredShape(ComboBox cmb, string savedName)
    {
        if (cmb.Items.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(savedName))
        {
            foreach (object item in cmb.Items)
            {
                if (string.Equals(item?.ToString(), savedName, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedItem = item;
                    return;
                }
            }
        }

        string[] preferred = ["T1", "51", "M_T1", "Estribo", "Stirrup"];
        foreach (string p in preferred)
        {
            foreach (object item in cmb.Items)
            {
                string s = item?.ToString() ?? "";
                if (string.Equals(s, p, StringComparison.OrdinalIgnoreCase) ||
                    s.Contains(p, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedItem = item;
                    return;
                }
            }
        }

        cmb.SelectedIndex = 0;
    }

    private void RefreshSelectionInfo()
    {
        if (_selectedCapitals.Count == 0)
        {
            _sampleGeom = null;
            LblSelectionStatus.Text = "Sin capiteles seleccionados. Haz clic en 'Seleccionar en Revit'.";
            LblSelectionStatus.Foreground = new SolidColorBrush(WpfColor.FromRgb(185, 28, 28));
            LblElementCoverInfo.Text = string.Empty;
            BtnApply.IsEnabled = false;
            return;
        }

        LblSelectionStatus.Foreground = new SolidColorBrush(WpfColor.FromRgb(15, 23, 42));
        LblSelectionStatus.Text = _selectedCapitals.Count == 1
            ? $"1 capitel seleccionado ({_selectedCapitals[0].Name})"
            : $"{_selectedCapitals.Count} capiteles seleccionados";

        _sampleGeom = CapitalGeometryHelper.ExtractGeometry(_doc, _selectedCapitals[0]);
        if (_sampleGeom != null)
        {
            double covCm = UnitConverter.FeetToMeters(_sampleGeom.CoverSidesFeet) * 100.0;
            LblElementCoverInfo.Text = $"Revit: {covCm:0.0} cm ({_sampleGeom.CoverTypeName})";
            if (_isInitializing)
            {
                // Pre-populate manual fallback box with element cover if user unchecks later
                double covM = Math.Round(UnitConverter.FeetToMeters(_sampleGeom.CoverSidesFeet), 3);
                if (covM > 0) TxtCoverMeters.Text = covM.ToString("0.000", CultureInfo.InvariantCulture);
            }
        }
        else
        {
            LblElementCoverInfo.Text = string.Empty;
        }

        BtnApply.IsEnabled = true;
    }

    private void DiagramCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isInitializing) UpdateDiagrams();
    }

    private void ConfigControl_Changed(object sender, EventArgs e)
    {
        if (!_isInitializing) UpdateDiagrams();
    }

    private void ChkUseElementCover_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtCoverMeters != null)
            TxtCoverMeters.IsEnabled = ChkUseElementCover.IsChecked != true;

        if (!_isInitializing) UpdateDiagrams();
    }

    private void UpdateDiagrams()
    {
        double wM = _sampleGeom != null ? UnitConverter.FeetToMeters(_sampleGeom.SizeXFeet) : 1.20;
        double dM = _sampleGeom != null ? UnitConverter.FeetToMeters(_sampleGeom.SizeYFeet) : 1.20;
        double hM = _sampleGeom != null ? UnitConverter.FeetToMeters(_sampleGeom.HeightFeet) : 0.65;

        double coverM = (ChkUseElementCover.IsChecked == true && _sampleGeom != null)
            ? UnitConverter.FeetToMeters(_sampleGeom.CoverSidesFeet)
            : ParsePositiveOrDefault(TxtCoverMeters.Text, 0.04);

        double sVertM = ParsePositiveOrDefault(TxtVertSpacing.Text, 0.20);
        double sStirM = ParsePositiveOrDefault(TxtStirrupSpacing.Text, 0.15);

        double innerW = Math.Max(0.10, wM - 2.0 * coverM - 0.03);
        double innerD = Math.Max(0.10, dM - 2.0 * coverM - 0.03);
        double innerH = Math.Max(0.10, hM - 2.0 * coverM - 0.03);

        int nPerFaceX = Math.Max(1, (int)Math.Ceiling(innerW / sVertM));
        int nPerFaceY = Math.Max(1, (int)Math.Ceiling(innerD / sVertM));
        int totalVertBars = 2 * nPerFaceX + 2 * nPerFaceY;
        int totalStirrups = Math.Max(2, (int)Math.Ceiling(innerH / sStirM) + 1);

        DrawPlanDiagram(wM, dM, coverM, nPerFaceX, nPerFaceY);
        DrawElevationDiagram(wM, hM, coverM, nPerFaceX, totalStirrups);

        string prefix = _sampleGeom != null ? "Capitel ref." : "Ejemplo";
        LblDiagramInfo.Text = $"{prefix}: {wM:0.00} × {dM:0.00} × {hM:0.00} m  |  Recubr.: {coverM * 100:0.0} cm\n" +
                              $"Estimado: ~{totalVertBars} barras vert. (gancho 90°) y ~{totalStirrups} estribos (135°).";
    }

    private void DrawPlanDiagram(double wM, double dM, double coverM, int nPerFaceX, int nPerFaceY)
    {
        CanvasPlanta.Children.Clear();
        double cw = CanvasPlanta.ActualWidth;
        double ch = CanvasPlanta.ActualHeight;
        if (cw < 40 || ch < 40) return;

        double pad = 24.0;
        double scale = Math.Min((cw - 2 * pad) / Math.Max(0.2, wM), (ch - 2 * pad) / Math.Max(0.2, dM));
        double rw = wM * scale;
        double rd = dM * scale;
        double ox = 0.5 * (cw - rw);
        double oy = 0.5 * (ch - rd);

        // Concrete contour
        var concRect = new WpfRectangle
        {
            Width = rw,
            Height = rd,
            Stroke = new SolidColorBrush(WpfColor.FromRgb(100, 116, 139)),
            StrokeThickness = 1.6,
            Fill = new SolidColorBrush(WpfColor.FromRgb(241, 245, 249))
        };
        Canvas.SetLeft(concRect, ox);
        Canvas.SetTop(concRect, oy);
        CanvasPlanta.Children.Add(concRect);

        // Closed stirrup perimeter (green)
        double covPx = Math.Clamp(coverM * scale, 5.0, Math.Min(rw, rd) * 0.2);
        double sw = rw - 2 * covPx;
        double sd = rd - 2 * covPx;
        double sx = ox + covPx;
        double sy = oy + covPx;

        var greenBrush = new SolidColorBrush(WpfColor.FromRgb(22, 163, 74));
        var stirRect = new WpfRectangle
        {
            Width = Math.Max(10, sw),
            Height = Math.Max(10, sd),
            RadiusX = 4,
            RadiusY = 4,
            Stroke = greenBrush,
            StrokeThickness = 2.0,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(stirRect, sx);
        Canvas.SetTop(stirRect, sy);
        CanvasPlanta.Children.Add(stirRect);

        // 135° seismic hooks at bottom-left corner
        double hookTail = Math.Min(16.0, Math.Min(sw, sd) * 0.16);
        CanvasPlanta.Children.Add(new WpfLine
        {
            X1 = sx + 2, Y1 = sy + sd - 2,
            X2 = sx + 2 + hookTail, Y2 = sy + sd - 2 - hookTail * 0.65,
            Stroke = greenBrush, StrokeThickness = 1.8
        });
        CanvasPlanta.Children.Add(new WpfLine
        {
            X1 = sx + 2, Y1 = sy + sd - 2,
            X2 = sx + 2 + hookTail * 0.65, Y2 = sy + sd - 2 - hookTail,
            Stroke = greenBrush, StrokeThickness = 1.8
        });

        // Caras opuestas X con inicio y fin activos (0..nPerFaceX); caras opuestas Y sin inicio ni fin (1..nPerFaceY-1)
        double inset = 3.5;
        double ix0 = sx + inset;
        double ix1 = sx + sw - inset;
        double iy0 = sy + inset;
        double iy1 = sy + sd - inset;
        double hook90Len = Math.Min(Math.Min(sw, sd) * 0.24, 34.0);

        var redBrush = new SolidColorBrush(WpfColor.FromRgb(220, 38, 38));

        // Bottom face (left->right, includeFirst=true, includeLast=true, hook upward)
        for (int i = 0; i <= nPerFaceX; i++)
        {
            double px = ix0 + (ix1 - ix0) * i / nPerFaceX;
            DrawPlanBarAndHook(px, iy1, 0, -hook90Len, redBrush);
        }
        // Right face (bottom->top, includeFirst=false, includeLast=false, hook leftward)
        for (int i = 1; i < nPerFaceY; i++)
        {
            double py = iy1 - (iy1 - iy0) * i / nPerFaceY;
            DrawPlanBarAndHook(ix1, py, -hook90Len, 0, redBrush);
        }
        // Top face (right->left, includeFirst=true, includeLast=true, hook downward)
        for (int i = 0; i <= nPerFaceX; i++)
        {
            double px = ix1 - (ix1 - ix0) * i / nPerFaceX;
            DrawPlanBarAndHook(px, iy0, 0, hook90Len, redBrush);
        }
        // Left face (top->bottom, includeFirst=false, includeLast=false, hook rightward)
        for (int i = 1; i < nPerFaceY; i++)
        {
            double py = iy0 + (iy1 - iy0) * i / nPerFaceY;
            DrawPlanBarAndHook(ix0, py, hook90Len, 0, redBrush);
        }

        // Dimensions label
        var dimText = new TextBlock
        {
            Text = $"Planta: {wM:0.00} × {dM:0.00} m",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(71, 85, 105))
        };
        Canvas.SetLeft(dimText, ox);
        Canvas.SetTop(dimText, Math.Max(2, oy - 16));
        CanvasPlanta.Children.Add(dimText);
    }

    private void DrawPlanBarAndHook(double x, double y, double dx, double dy, Brush brush)
    {
        CanvasPlanta.Children.Add(new WpfLine
        {
            X1 = x, Y1 = y,
            X2 = x + dx, Y2 = y + dy,
            Stroke = brush,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        double r = 2.6;
        var dot = new WpfEllipse { Width = 2 * r, Height = 2 * r, Fill = brush };
        Canvas.SetLeft(dot, x - r);
        Canvas.SetTop(dot, y - r);
        CanvasPlanta.Children.Add(dot);
    }

    private void DrawElevationDiagram(double wM, double hM, double coverM, int nPerFaceX, int nStirrups)
    {
        CanvasElevacion.Children.Clear();
        double cw = CanvasElevacion.ActualWidth;
        double ch = CanvasElevacion.ActualHeight;
        if (cw < 40 || ch < 40) return;

        double pad = 24.0;
        double scale = Math.Min((cw - 2 * pad) / Math.Max(0.2, wM), (ch - 2 * pad) / Math.Max(0.2, hM));
        double rw = wM * scale;
        double rh = hM * scale;
        double ox = 0.5 * (cw - rw);
        double oy = 0.5 * (ch - rh);

        var concRect = new WpfRectangle
        {
            Width = rw,
            Height = rh,
            Stroke = new SolidColorBrush(WpfColor.FromRgb(100, 116, 139)),
            StrokeThickness = 1.6,
            Fill = new SolidColorBrush(WpfColor.FromRgb(241, 245, 249))
        };
        Canvas.SetLeft(concRect, ox);
        Canvas.SetTop(concRect, oy);
        CanvasElevacion.Children.Add(concRect);

        double covPx = Math.Clamp(coverM * scale, 5.0, Math.Min(rw, rh) * 0.22);
        double sx = ox + covPx;
        double sy = oy + covPx;
        double sw = rw - 2 * covPx;
        double sh = rh - 2 * covPx;

        // Vertical bars + 90° inward hooks
        var redBrush = new SolidColorBrush(WpfColor.FromRgb(220, 38, 38));
        double vx0 = sx + 3.0;
        double vx1 = sx + sw - 3.0;
        double hookLen = Math.Min(sw * 0.22, 26.0);

        int countDraw = Math.Min(12, Math.Max(2, nPerFaceX + 1));
        for (int i = 0; i < countDraw; i++)
        {
            double px = vx0 + (vx1 - vx0) * i / (countDraw - 1);
            CanvasElevacion.Children.Add(new WpfLine
            {
                X1 = px, Y1 = sy + 2,
                X2 = px, Y2 = sy + sh - 2,
                Stroke = redBrush,
                StrokeThickness = 1.8
            });

            // Side outermost bars show clear inward 90° hooks in elevation
            if (i == 0 || i == countDraw - 1)
            {
                double dir = (i == 0) ? hookLen : -hookLen;
                CanvasElevacion.Children.Add(new WpfLine
                {
                    X1 = px, Y1 = sy + 2, X2 = px + dir, Y2 = sy + 2,
                    Stroke = redBrush, StrokeThickness = 1.8
                });
                CanvasElevacion.Children.Add(new WpfLine
                {
                    X1 = px, Y1 = sy + sh - 2, X2 = px + dir, Y2 = sy + sh - 2,
                    Stroke = redBrush, StrokeThickness = 1.8
                });
            }
        }

        // Closed horizontal stirrups
        var greenBrush = new SolidColorBrush(WpfColor.FromRgb(22, 163, 74));
        int drawStirrups = Math.Min(14, Math.Max(2, nStirrups));
        double stirTop = sy + 6.0;
        double stirBot = sy + sh - 6.0;
        if (stirBot > stirTop)
        {
            for (int i = 0; i < drawStirrups; i++)
            {
                double py = stirTop + (stirBot - stirTop) * i / (drawStirrups - 1);
                CanvasElevacion.Children.Add(new WpfLine
                {
                    X1 = sx, Y1 = py,
                    X2 = sx + sw, Y2 = py,
                    Stroke = greenBrush,
                    StrokeThickness = 1.8
                });
            }
        }

        var dimText = new TextBlock
        {
            Text = $"Elevación: H = {hM:0.00} m",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(71, 85, 105))
        };
        Canvas.SetLeft(dimText, ox);
        Canvas.SetTop(dimText, Math.Max(2, oy - 16));
        CanvasElevacion.Children.Add(dimText);
    }

    private static double ParsePositiveOrDefault(string? raw, double fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        string normalized = raw.Trim().Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 1e-4)
            return v;
        return fallback;
    }

    private static bool TryParsePositive(string? raw, out double value)
    {
        value = 0.0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string normalized = raw.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 1e-4;
    }

    private void BtnPickCapitals_Click(object sender, RoutedEventArgs e)
    {
        UserWantsToPickCapitals = true;
        DialogResult = false;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        UserWantsToPickCapitals = false;
        DialogResult = false;
        Close();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCapitals.Count == 0)
        {
            MessageBox.Show("Debe seleccionar al menos un capitel (Structural Columns).",
                "BIM Suite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParsePositive(TxtVertSpacing.Text, out double vertSpacingM))
        {
            MessageBox.Show("Ingrese un espaciamiento válido mayor a cero para las barras verticales.",
                "BIM Suite", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtVertSpacing.Focus();
            return;
        }

        if (!TryParsePositive(TxtStirrupSpacing.Text, out double stirrupSpacingM))
        {
            MessageBox.Show("Ingrese un espaciamiento válido mayor a cero para los estribos.",
                "BIM Suite", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtStirrupSpacing.Focus();
            return;
        }

        bool useElemCover = ChkUseElementCover.IsChecked == true;
        double coverM = 0.04;
        if (!useElemCover && !TryParsePositive(TxtCoverMeters.Text, out coverM))
        {
            MessageBox.Show("Ingrese un valor de recubrimiento manual válido mayor a cero.",
                "BIM Suite", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtCoverMeters.Focus();
            return;
        }

        string shapeName = CmbStirrupShape.SelectedItem?.ToString() ?? "";
        if (shapeName.StartsWith("Automática", StringComparison.OrdinalIgnoreCase))
            shapeName = string.Empty;

        var request = new CreateCapitalRebarRequest
        {
            ColumnElementIds = _selectedCapitals.Select(c => c.Id.Value).Distinct().ToList(),
            VerticalBarTypeName = CmbVertBarType.SelectedItem?.ToString() ?? string.Empty,
            VerticalSpacingMeters = vertSpacingM,
            VerticalHookTypeName = CmbVertHookType.SelectedItem?.ToString() ?? string.Empty,
            StirrupBarTypeName = CmbStirrupBarType.SelectedItem?.ToString() ?? string.Empty,
            StirrupShapeName = shapeName,
            StirrupHookTypeName = CmbStirrupHookType.SelectedItem?.ToString() ?? string.Empty,
            StirrupSpacingMeters = stirrupSpacingM,
            UseElementCover = useElemCover,
            CoverMeters = coverM
        };

        CapitalRebarSettingsStore.Save(request);
        BuiltRequest = request;
        UserWantsToPickCapitals = false;
        DialogResult = true;
        Close();
    }
}
