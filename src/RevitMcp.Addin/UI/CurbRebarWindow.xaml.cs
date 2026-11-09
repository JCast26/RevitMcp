using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMcp.Addin.Tools;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.UI;

public partial class CurbRebarWindow : Window
{
    private readonly UIDocument _uidoc;
    private readonly Document _doc;
    private List<long> _selectedWallIds;
    private bool _isLoaded;

    public bool UserWantsToPickWalls { get; private set; }
    public CreateCurbRebarRequest? BuiltRequest { get; private set; }

    public CurbRebarWindow(UIDocument uidoc, List<long> selectedWallIds, CreateCurbRebarRequest? cachedRequest = null)
    {
        InitializeComponent();

        _uidoc = uidoc ?? throw new ArgumentNullException(nameof(uidoc));
        _doc = uidoc.Document;
        _selectedWallIds = selectedWallIds ?? new List<long>();

        LoadDocumentRebarData(cachedRequest);
        UpdateWallSelectionInfo();
        _isLoaded = true;

        UpdateCoverUiFromWall();
        UpdateDiagram();
    }

    private void LoadDocumentRebarData(CreateCurbRebarRequest? savedSettings)
    {
        var barTypes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .OrderBy(b => b.Name)
            .ToList();

        cbStirrupBar.ItemsSource = barTypes;
        cbStirrupBar.DisplayMemberPath = "Name";

        cbLongitudinalBar.ItemsSource = barTypes;
        cbLongitudinalBar.DisplayMemberPath = "Name";

        // Ganchos estándar de armadura
        var hookTypes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .OrderBy(h => h.Name)
            .ToList();

        cbCornerHook.ItemsSource = hookTypes;
        cbCornerHook.DisplayMemberPath = "Name";

        // Estribo default: 1/4", 3/8" o #3
        RebarBarType? defaultStir = null;
        if (!string.IsNullOrWhiteSpace(savedSettings?.StirrupBarTypeName))
            defaultStir = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.StirrupBarTypeName, StringComparison.OrdinalIgnoreCase));
        defaultStir ??= barTypes.FirstOrDefault(b => b.Name.Contains("1/4"))
                    ?? barTypes.FirstOrDefault(b => b.Name.Contains("3/8"))
                    ?? barTypes.FirstOrDefault(b => b.Name.Contains("#3"))
                    ?? barTypes.FirstOrDefault();
        if (defaultStir != null) cbStirrupBar.SelectedItem = defaultStir;

        // Longitudinal default: 3/8", 1/2" o #4
        RebarBarType? defaultLong = null;
        if (!string.IsNullOrWhiteSpace(savedSettings?.LongitudinalBarTypeName))
            defaultLong = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.LongitudinalBarTypeName, StringComparison.OrdinalIgnoreCase));
        defaultLong ??= barTypes.FirstOrDefault(b => b.Name.Contains("3/8"))
                    ?? barTypes.FirstOrDefault(b => b.Name.Contains("1/2"))
                    ?? barTypes.FirstOrDefault(b => b.Name.Contains("#4"))
                    ?? barTypes.FirstOrDefault();
        if (defaultLong != null) cbLongitudinalBar.SelectedItem = defaultLong;

        // Gancho en esquina default: Standard - 90 deg.
        RebarHookType? defaultHook = null;
        if (!string.IsNullOrWhiteSpace(savedSettings?.CornerHookTypeName))
            defaultHook = hookTypes.FirstOrDefault(h => h.Name.Equals(savedSettings.CornerHookTypeName, StringComparison.OrdinalIgnoreCase));
        defaultHook ??= hookTypes.FirstOrDefault(h => h.Style == RebarStyle.Standard && (h.Name.Contains("90") || Math.Abs(h.HookAngle - Math.PI / 2.0) < 0.1))
                     ?? hookTypes.FirstOrDefault(h => h.Name.Contains("90"))
                     ?? hookTypes.FirstOrDefault(h => h.Style == RebarStyle.Standard)
                     ?? hookTypes.FirstOrDefault();
        if (defaultHook != null) cbCornerHook.SelectedItem = defaultHook;

        if (savedSettings != null)
        {
            if (savedSettings.StirrupType == CurbStirrupType.ClosedRectangular)
                rbClosedRect.IsChecked = true;
            else
                rbOpenC.IsChecked = true;

            txtStirrupSpacing.Text = (savedSettings.StirrupSpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

            UpdateLongitudinalCountOptions(savedSettings.LongitudinalBarCount);

            chkExtendAtCorners.IsChecked = savedSettings.ExtendAtCorners;
            gridCornerHook.IsEnabled = savedSettings.ExtendAtCorners;

            chkUseWallCover.IsChecked = savedSettings.UseWallCover;
            txtCover.Text = (savedSettings.CoverMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);
        }
        else
        {
            rbOpenC.IsChecked = true;
            UpdateLongitudinalCountOptions(2);
            chkExtendAtCorners.IsChecked = true;
            gridCornerHook.IsEnabled = true;
            chkUseWallCover.IsChecked = true;
            txtCover.Text = "2.5";
            txtStirrupSpacing.Text = "20";
        }
    }

    private void UpdateLongitudinalCountOptions(int preferredCount = -1)
    {
        if (cbLongitudinalCount == null) return;

        if (preferredCount <= 0 && cbLongitudinalCount.SelectedItem is ComboBoxItem curItem && int.TryParse(curItem.Tag?.ToString(), out int curVal))
        {
            preferredCount = curVal;
        }

        cbLongitudinalCount.Items.Clear();

        bool isOpenC = rbOpenC.IsChecked == true;
        if (isOpenC)
        {
            var item2 = new ComboBoxItem { Content = "2 Barras (Superior e Inferior)", Tag = 2 };
            var item3 = new ComboBoxItem { Content = "3 Barras (Superior, Media e Inferior)", Tag = 3 };
            cbLongitudinalCount.Items.Add(item2);
            cbLongitudinalCount.Items.Add(item3);

            cbLongitudinalCount.SelectedItem = (preferredCount == 3) ? item3 : item2;
        }
        else
        {
            var item4 = new ComboBoxItem { Content = "4 Barras en esquinas (2 Sup, 2 Inf - Tipo Viga)", Tag = 4 };
            var item6 = new ComboBoxItem { Content = "6 Barras en esquinas e intermedias (3 por cara)", Tag = 6 };
            cbLongitudinalCount.Items.Add(item4);
            cbLongitudinalCount.Items.Add(item6);

            cbLongitudinalCount.SelectedItem = (preferredCount == 6) ? item6 : item4;
        }
    }

    private void UpdateWallSelectionInfo()
    {
        int count = _selectedWallIds.Count;
        if (count == 0)
        {
            lblSelectionCount.Text = "0 sardinel(es) seleccionado(s)";
            lblSelectionCount.Foreground = System.Windows.Media.Brushes.Gray;
            lblSelectionDims.Text = "Seleccione uno o varios muros de sardinel en Revit.";
            return;
        }

        lblSelectionCount.Text = $"✓ {count} sardinel(es) seleccionado(s)";
        lblSelectionCount.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0284C7"));

        double totalThick = 0.0;
        double totalHeight = 0.0;
        int validCount = 0;

        foreach (var id in _selectedWallIds)
        {
            if (_doc.GetElement(new ElementId(id)) is Wall w)
            {
                try
                {
                    var g = WallGeometryHelper.ExtractGeometry(_doc, w);
                    totalThick += g.ThicknessMeters;
                    totalHeight += g.HeightMeters;
                    validCount++;
                }
                catch { }
            }
        }

        if (validCount > 0)
        {
            double avgThickCm = (totalThick / validCount) * 100.0;
            double avgHeightCm = (totalHeight / validCount) * 100.0;
            lblSelectionDims.Text = $"Espesor promedio: {avgThickCm:0.#} cm • Altura promedio: {avgHeightCm:0.#} cm";

            // Sugerencia inteligente de estribo y cantidad si el usuario no tiene settings previos
            if (!_isLoaded)
            {
                if (avgThickCm <= 16.0)
                {
                    rbOpenC.IsChecked = true;
                    UpdateLongitudinalCountOptions(avgHeightCm > 42.0 ? 3 : 2);
                }
                else
                {
                    rbClosedRect.IsChecked = true;
                    UpdateLongitudinalCountOptions(avgHeightCm > 42.0 ? 6 : 4);
                }
            }
        }
    }

    private void UpdateCoverUiFromWall()
    {
        if (!_isLoaded || txtCover == null || lblCoverInfo == null || chkUseWallCover == null) return;

        if (_selectedWallIds != null && _selectedWallIds.Count > 0 && _doc != null)
        {
            var wallElem = _doc.GetElement(new ElementId(_selectedWallIds[0])) as Wall;
            if (wallElem != null)
            {
                var (coverFeet, coverName) = WallGeometryHelper.ExtractConcreteCover(_doc, wallElem);
                double coverCm = UnitConverter.FeetToMeters(coverFeet) * 100.0;

                if (chkUseWallCover.IsChecked == true)
                {
                    txtCover.Text = coverCm.ToString("0.#", CultureInfo.InvariantCulture);
                    txtCover.IsEnabled = false;
                    lblCoverInfo.Text = $"Asignado en Revit: {coverCm:0.#} cm ({coverName})";
                    lblCoverInfo.Foreground = System.Windows.Media.Brushes.DarkGreen;
                }
                else
                {
                    txtCover.IsEnabled = true;
                    lblCoverInfo.Text = $"Modo manual activo. (En muro: {coverCm:0.#} cm)";
                    lblCoverInfo.Foreground = System.Windows.Media.Brushes.Gray;
                }
                return;
            }
        }

        if (chkUseWallCover.IsChecked == true)
        {
            txtCover.IsEnabled = false;
            lblCoverInfo.Text = "Se leerá automáticamente del muro al seleccionarlo.";
            lblCoverInfo.Foreground = System.Windows.Media.Brushes.SteelBlue;
        }
        else
        {
            txtCover.IsEnabled = true;
            lblCoverInfo.Text = "Modo de recubrimiento manual.";
            lblCoverInfo.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private void UpdateDiagram()
    {
        if (!_isLoaded || vizStirrupLayer == null || vizLongitudinalLayer == null || rectCurb == null) return;

        vizStirrupLayer.Children.Clear();
        vizLongitudinalLayer.Children.Clear();

        bool isOpenC = rbOpenC.IsChecked == true;
        int barCount = 2;
        if (cbLongitudinalCount.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int c))
        {
            barCount = c;
        }

        var magentaBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D946EF"));
        var blueBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0284C7"));
        var darkBlueBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0369A1"));

        double barDiam = 10.0;
        double radius = barDiam / 2.0;

        void DrawBar(double cx, double cy)
        {
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = barDiam,
                Height = barDiam,
                Fill = blueBrush,
                Stroke = darkBlueBrush,
                StrokeThickness = 1.2
            };
            Canvas.SetLeft(ellipse, cx - radius);
            Canvas.SetTop(ellipse, cy - radius);
            vizLongitudinalLayer.Children.Add(ellipse);
        }

        if (isOpenC)
        {
            // 1. Proporción del sardinel delgado (muro esbelto centrado en el Canvas)
            rectCurb.Width = 56;
            rectCurb.Height = 146;
            Canvas.SetLeft(rectCurb, 102);
            Canvas.SetTop(rectCurb, 12);

            // 2. Estribo en "C": barra recta vertical con ganchos a 180° en ambos extremos
            // Centrado exactamente en el espesor del sardinel:
            // - Eje del sardinel: X = 130 (Centro de X=102..158)
            // - Espesor del estribo = 20 px (X=120 a X=140), dejando 18 px de recubrimiento a cada lado
            // - Pata superior doblada hacia abajo (X=140, Y=42 a 28)
            // - Arco de 180° sobre el gancho superior hacia X=120
            // - Barra vertical recta de Y=28 a Y=142 en X=120
            // - Arco de 180° bajo el gancho inferior hacia X=140
            // - Pata inferior doblada hacia arriba (X=140, Y=142 a 128)
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(140, 42), false, false);
                ctx.LineTo(new System.Windows.Point(140, 28), true, false);
                ctx.ArcTo(new System.Windows.Point(120, 28), new System.Windows.Size(10, 10), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new System.Windows.Point(120, 142), true, false);
                ctx.ArcTo(new System.Windows.Point(140, 142), new System.Windows.Size(10, 10), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new System.Windows.Point(140, 128), true, false);
            }
            geom.Freeze();

            var path = new System.Windows.Shapes.Path
            {
                Data = geom,
                Stroke = magentaBrush,
                StrokeThickness = 2.5
            };
            vizStirrupLayer.Children.Add(path);

            // 3. Barras longitudinales centradas en el eje X = 130 y alojadas DENTRO de los ganchos a 180°
            double xBar = 130.0;
            DrawBar(xBar, 28.0);  // Dentro del gancho superior
            DrawBar(xBar, 142.0); // Dentro del gancho inferior

            if (barCount == 3)
            {
                DrawBar(xBar, 85.0); // Barra intermedia en el centro
            }
        }
        else
        {
            // 1. Proporción del sardinel ancho
            rectCurb.Width = 86;
            rectCurb.Height = 146;
            Canvas.SetLeft(rectCurb, 87);
            Canvas.SetTop(rectCurb, 12);

            // 2. Estribo cerrado rectangular con ganchos sísmicos a 135°
            double left = 97;
            double top = 22;
            double width = 66;
            double height = 126;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                Stroke = magentaBrush,
                StrokeThickness = 2.5,
                RadiusX = 4,
                RadiusY = 4
            };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            vizStirrupLayer.Children.Add(rect);

            // Ganchos a 135° en esquina superior izquierda
            vizStirrupLayer.Children.Add(new System.Windows.Shapes.Line { X1 = left, Y1 = top + 14, X2 = left + 12, Y2 = top + 26, Stroke = magentaBrush, StrokeThickness = 2.2 });
            vizStirrupLayer.Children.Add(new System.Windows.Shapes.Line { X1 = left + 14, Y1 = top, X2 = left + 26, Y2 = top + 12, Stroke = magentaBrush, StrokeThickness = 2.2 });

            // 3. Barras longitudinales armadas como VIGA (esquinas de los estribos)
            double xLeft = 107.0;
            double xRight = 153.0;
            double yTop = 32.0;
            double yBottom = 138.0;

            // 4 barras en las 4 esquinas
            DrawBar(xLeft, yTop);
            DrawBar(xRight, yTop);
            DrawBar(xLeft, yBottom);
            DrawBar(xRight, yBottom);

            // Si son 6 barras: se añaden 2 barras intermedias (1 en cada cara lateral)
            if (barCount == 6)
            {
                double yMid = (yTop + yBottom) / 2.0;
                DrawBar(xLeft, yMid);
                DrawBar(xRight, yMid);
            }
        }
    }

    private void StirrupType_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        UpdateLongitudinalCountOptions();
        UpdateDiagram();
    }

    private void Options_Changed(object sender, EventArgs e)
    {
        UpdateDiagram();
    }

    private void Options_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDiagram();
    }

    private void ChkUseWallCover_Changed(object sender, RoutedEventArgs e)
    {
        UpdateCoverUiFromWall();
    }

    private void ChkExtendAtCorners_Changed(object sender, RoutedEventArgs e)
    {
        if (gridCornerHook != null)
        {
            gridCornerHook.IsEnabled = chkExtendAtCorners.IsChecked == true;
        }
        UpdateDiagram();
    }

    private void BtnPickWalls_Click(object sender, RoutedEventArgs e)
    {
        UserWantsToPickWalls = true;
        DialogResult = false;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWallIds == null || _selectedWallIds.Count == 0)
        {
            TaskDialog.Show("BIM Suite - Sardineles", "No hay ningún sardinel seleccionado. Presione 'Seleccionar en Revit' para elegir uno o varios sardineles.");
            return;
        }

        var selStirBar = cbStirrupBar.SelectedItem as RebarBarType;
        if (selStirBar == null)
        {
            TaskDialog.Show("BIM Suite - Sardineles", "Seleccione un tipo de barra para los estribos transversales.");
            return;
        }

        var selLongBar = cbLongitudinalBar.SelectedItem as RebarBarType;
        if (selLongBar == null)
        {
            TaskDialog.Show("BIM Suite - Sardineles", "Seleccione un tipo de barra para el refuerzo longitudinal.");
            return;
        }

        if (!double.TryParse(txtStirrupSpacing.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double stirSpacingCm) || stirSpacingCm <= 0)
        {
            TaskDialog.Show("BIM Suite - Sardineles", "Ingrese un espaciamiento válido para los estribos transversales.");
            return;
        }

        double coverCm = 2.5;
        if (chkUseWallCover.IsChecked != true)
        {
            if (!double.TryParse(txtCover.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out coverCm) || coverCm <= 0)
            {
                TaskDialog.Show("BIM Suite - Sardineles", "Ingrese un recubrimiento de concreto válido (cm).");
                return;
            }
        }

        string cornerHookName = string.Empty;
        if (chkExtendAtCorners.IsChecked == true && cbCornerHook.SelectedItem is RebarHookType selHook)
        {
            cornerHookName = selHook.Name;
        }

        int longCount = 2;
        if (cbLongitudinalCount.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int c))
        {
            longCount = c;
        }

        BuiltRequest = new CreateCurbRebarRequest
        {
            WallElementIds = new List<long>(_selectedWallIds),
            StirrupType = rbClosedRect.IsChecked == true ? CurbStirrupType.ClosedRectangular : CurbStirrupType.OpenC,
            StirrupBarTypeName = selStirBar.Name,
            StirrupSpacingMeters = stirSpacingCm / 100.0,
            LongitudinalBarCount = longCount,
            LongitudinalBarTypeName = selLongBar.Name,
            ExtendAtCorners = chkExtendAtCorners.IsChecked == true,
            CornerHookTypeName = cornerHookName,
            UseWallCover = chkUseWallCover.IsChecked == true,
            CoverMeters = coverCm / 100.0
        };

        CurbRebarSettingsStore.Save(BuiltRequest);
        DialogResult = true;
        Close();
    }
}
