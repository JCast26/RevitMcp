using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMcp.Addin.Tools;
using RevitMcp.Core.Models;
using RevitMcp.Core.Units;

namespace RevitMcp.Addin.UI;

/// <summary>
/// Elemento de visualización clara para el desplegable de ganchos de refuerzo.
/// </summary>
public class HookOptionItem
{
    public string DisplayName { get; set; } = string.Empty;
    public RebarHookType? HookType { get; set; }
    public override string ToString() => DisplayName;
}

/// <summary>
/// Filtro de selección interactiva para aceptar únicamente elementos de la categoría Muros (Walls).
/// </summary>
public class WallSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem)
    {
        return elem is Wall || elem.Category?.Id.Value == (int)BuiltInCategory.OST_Walls;
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}

/// <summary>
/// Ventana interactiva de configuración de armadura de refuerzo en muros (BIM Suite).
/// Desacopla la UI de WPF de las operaciones de modificación de Revit.
/// </summary>
public partial class WallRebarWindow : Window
{
    private readonly UIDocument _uidoc;
    private readonly Document _doc;
    private readonly List<long> _selectedWallIds;
    private readonly bool _isLoaded;

    public bool UserWantsToPickWalls { get; private set; }
    public CreateWallRebarRequest? BuiltRequest { get; private set; }

    public WallRebarWindow(UIDocument uidoc, List<long> initialWallIds, CreateWallRebarRequest? cachedRequest = null)
    {
        // 1. Asignar dependencias críticas ANTES de InitializeComponent para evitar NullReferenceException
        _uidoc = uidoc;
        _doc = uidoc.Document;
        _selectedWallIds = initialWallIds ?? new List<long>();

        // 2. Inicializar componentes visuales de WPF
        InitializeComponent();
        _isLoaded = true;

        var settingsToRestore = cachedRequest ?? WallRebarSettingsStore.Load();

        LoadDocumentRebarData(settingsToRestore);
        UpdateSelectionUi();

        if (settingsToRestore != null)
        {
            RestoreCachedInputs(settingsToRestore);
        }

        UpdateCoverUiFromWall();
    }

    private void UpdateSelectionUi()
    {
        if (lblSelectionStatus == null) return;

        bool hasInclinedWall = false;

        if (_selectedWallIds.Count > 0)
        {
            int openingsCount = WallGeometryHelper.CountOpeningsInWalls(_doc, _selectedWallIds);

            // Detección automática de muros inclinados de escalera (perfil editado)
            foreach (var wallId in _selectedWallIds)
            {
                if (_doc.GetElement(new ElementId(wallId)) is Wall w && WallGeometryHelper.IsInclinedProfileWall(_doc, w))
                {
                    hasInclinedWall = true;
                    break;
                }
            }

            if (hasInclinedWall)
            {
                lblSelectionStatus.Text = $"✓ {_selectedWallIds.Count} muro(s) • Muro inclinado de escalera detectado";
            }
            else if (openingsCount > 0)
            {
                lblSelectionStatus.Text = $"✓ {_selectedWallIds.Count} muro(s) • {openingsCount} abertura(s) detectada(s)";
            }
            else
            {
                lblSelectionStatus.Text = $"✓ {_selectedWallIds.Count} muro(s) rectangular(es) estándar";
            }
            lblSelectionStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
        }
        else
        {
            lblSelectionStatus.Text = "0 muros seleccionados (requerido)";
            lblSelectionStatus.Foreground = System.Windows.Media.Brushes.Crimson;
        }

        UpdateSectionVisibilities();
        UpdateOpeningDiagramLayers();
        UpdateInclinedStairDiagram();
    }

    private void LoadDocumentRebarData(CreateWallRebarRequest? savedSettings = null)
    {
        var barTypes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .OrderBy(b => b.Name)
            .ToList();

        cbVerticalBar.ItemsSource = barTypes;
        cbVerticalBar.DisplayMemberPath = "Name";

        cbHorizontalBar.ItemsSource = barTypes;
        cbHorizontalBar.DisplayMemberPath = "Name";

        // Vertical default: 1/2" o guardado previo
        RebarBarType? defaultVert = null;
        if (!string.IsNullOrWhiteSpace(savedSettings?.VerticalBarTypeName))
        {
            defaultVert = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.VerticalBarTypeName, StringComparison.OrdinalIgnoreCase))
                       ?? barTypes.FirstOrDefault(b => b.Name.Contains(savedSettings.VerticalBarTypeName, StringComparison.OrdinalIgnoreCase));
        }
        if (defaultVert == null)
        {
            defaultVert = barTypes.FirstOrDefault(b => b.Name.Contains("1/2") || b.Name.Contains("12") || b.Name.Contains("#4"))
                       ?? barTypes.FirstOrDefault();
        }
        if (defaultVert != null) cbVerticalBar.SelectedItem = defaultVert;

        // Horizontal default: 3/8" o guardado previo
        RebarBarType? defaultHoriz = null;
        if (!string.IsNullOrWhiteSpace(savedSettings?.HorizontalBarTypeName))
        {
            defaultHoriz = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.HorizontalBarTypeName, StringComparison.OrdinalIgnoreCase))
                        ?? barTypes.FirstOrDefault(b => b.Name.Contains(savedSettings.HorizontalBarTypeName, StringComparison.OrdinalIgnoreCase));
        }
        if (defaultHoriz == null)
        {
            defaultHoriz = barTypes.FirstOrDefault(b => b.Name.Contains("3/8") || b.Name.Contains("10") || b.Name.Contains("#3"))
                        ?? barTypes.FirstOrDefault();
        }
        if (defaultHoriz != null) cbHorizontalBar.SelectedItem = defaultHoriz;

        // Grapas default: 3/8" o guardado previo
        cbCrosstieBar.ItemsSource = barTypes;
        cbCrosstieBar.DisplayMemberPath = "Name";
        RebarBarType? defaultTie = null;
        if (!string.IsNullOrWhiteSpace(savedSettings?.CrosstieBarTypeName))
        {
            defaultTie = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.CrosstieBarTypeName, StringComparison.OrdinalIgnoreCase))
                      ?? barTypes.FirstOrDefault(b => b.Name.Contains(savedSettings.CrosstieBarTypeName, StringComparison.OrdinalIgnoreCase));
        }
        if (defaultTie == null)
        {
            defaultTie = barTypes.FirstOrDefault(b => b.Name.Contains("3/8") || b.Name.Contains("10") || b.Name.Contains("#3"))
                      ?? barTypes.FirstOrDefault();
        }
        if (defaultTie != null) cbCrosstieBar.SelectedItem = defaultTie;

        // Opciones de ganchos compatibles con armadura longitudinal (estilo Standard)
        var allHookTypes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .ToList();

        // Filtrar estrictamente ganchos de estilo Standard (evita el error 'The Rebar's hooks do not match its Rebar Shape')
        var standardHooks = allHookTypes.Where(h => h.Style == RebarStyle.Standard).ToList();
        var hookTypes = standardHooks.Count > 0
            ? standardHooks.OrderBy(h => h.Name).ToList()
            : allHookTypes.Where(h => !h.Name.Contains("Stirrup", StringComparison.OrdinalIgnoreCase) && !h.Name.Contains("Estribo", StringComparison.OrdinalIgnoreCase)).OrderBy(h => h.Name).ToList();

        var hookItems = new List<HookOptionItem>
        {
            new() { DisplayName = "Sin gancho (Barra recta)", HookType = null }
        };

        foreach (var h in hookTypes)
        {
            string label;
            string lower = h.Name.ToLowerInvariant();
            if (lower.Contains("90") || Math.Abs(h.HookAngle - Math.PI / 2.0) < 0.05)
            {
                label = $"Gancho 90° ({h.Name})";
            }
            else if (lower.Contains("135") || Math.Abs(h.HookAngle - 2.356) < 0.05)
            {
                label = $"Gancho 135° - Sísmico ({h.Name})";
            }
            else if (lower.Contains("180") || Math.Abs(h.HookAngle - Math.PI) < 0.05)
            {
                label = $"Gancho 180° ({h.Name})";
            }
            else
            {
                label = $"Gancho: {h.Name}";
            }

            hookItems.Add(new HookOptionItem { DisplayName = label, HookType = h });
        }

        cbHorizontalHook.ItemsSource = hookItems;
        cbHorizontalHook.SelectedIndex = 0;

        if (!string.IsNullOrWhiteSpace(savedSettings?.HorizontalHookTypeName))
        {
            var matchHook = hookItems.FirstOrDefault(hi => hi.HookType != null &&
                (hi.HookType.Name.Equals(savedSettings.HorizontalHookTypeName, StringComparison.OrdinalIgnoreCase) ||
                 hi.DisplayName.Contains(savedSettings.HorizontalHookTypeName, StringComparison.OrdinalIgnoreCase)));

            if (matchHook != null) cbHorizontalHook.SelectedItem = matchHook;
        }

        // Gancho Vertical
        cbVerticalHook.ItemsSource = hookItems;
        cbVerticalHook.SelectedIndex = 0;

        if (!string.IsNullOrWhiteSpace(savedSettings?.VerticalHookTypeName))
        {
            var matchVHook = hookItems.FirstOrDefault(hi => hi.HookType != null &&
                (hi.HookType.Name.Equals(savedSettings.VerticalHookTypeName, StringComparison.OrdinalIgnoreCase) ||
                 hi.DisplayName.Contains(savedSettings.VerticalHookTypeName, StringComparison.OrdinalIgnoreCase)));

            if (matchVHook != null) cbVerticalHook.SelectedItem = matchVHook;
        }

        if (cbVerticalHookPosition != null && savedSettings != null)
        {
            cbVerticalHookPosition.SelectedIndex = Math.Clamp(savedSettings.VerticalHookPosition, 0, 2);
            cbVerticalHookPosition.IsEnabled = (cbVerticalHook.SelectedItem as HookOptionItem)?.HookType != null;
        }

        // -----------------------------------------------------------------
        // 6. Controles de Aberturas y Refuerzo de Vanos
        // -----------------------------------------------------------------
        if (cbOpeningMeshHook != null)
        {
            cbOpeningMeshHook.ItemsSource = hookItems;
            cbOpeningMeshHook.SelectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(savedSettings?.OpeningMeshHookTypeName))
            {
                var matchOpHook = hookItems.FirstOrDefault(hi => hi.HookType != null &&
                    (hi.HookType.Name.Equals(savedSettings.OpeningMeshHookTypeName, StringComparison.OrdinalIgnoreCase) ||
                     hi.DisplayName.Contains(savedSettings.OpeningMeshHookTypeName, StringComparison.OrdinalIgnoreCase)));
                if (matchOpHook != null) cbOpeningMeshHook.SelectedItem = matchOpHook;
            }
        }

        if (cbOpeningPerimeterBar != null)
        {
            cbOpeningPerimeterBar.ItemsSource = barTypes;
            cbOpeningPerimeterBar.DisplayMemberPath = "Name";
            RebarBarType? defPerim = null;
            if (!string.IsNullOrWhiteSpace(savedSettings?.OpeningPerimeterBarTypeName))
            {
                defPerim = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.OpeningPerimeterBarTypeName, StringComparison.OrdinalIgnoreCase));
            }
            cbOpeningPerimeterBar.SelectedItem = defPerim ?? defaultVert;
        }

        if (cbOpeningDiagonalBar != null)
        {
            cbOpeningDiagonalBar.ItemsSource = barTypes;
            cbOpeningDiagonalBar.DisplayMemberPath = "Name";
            RebarBarType? defDiag = null;
            if (!string.IsNullOrWhiteSpace(savedSettings?.OpeningDiagonalBarTypeName))
            {
                defDiag = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.OpeningDiagonalBarTypeName, StringComparison.OrdinalIgnoreCase));
            }
            cbOpeningDiagonalBar.SelectedItem = defDiag ?? defaultVert;
        }

        if (cbOpeningStirrupBar != null)
        {
            cbOpeningStirrupBar.ItemsSource = barTypes;
            cbOpeningStirrupBar.DisplayMemberPath = "Name";
            RebarBarType? defStir = null;
            if (!string.IsNullOrWhiteSpace(savedSettings?.OpeningStirrupBarTypeName))
            {
                defStir = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.OpeningStirrupBarTypeName, StringComparison.OrdinalIgnoreCase));
            }
            cbOpeningStirrupBar.SelectedItem = defStir ?? defaultTie;
        }

        // 7. Refuerzo de Muros Inclinados de Escalera
        if (cbInclinedTopBar != null)
        {
            cbInclinedTopBar.ItemsSource = barTypes;
            cbInclinedTopBar.DisplayMemberPath = "Name";
            RebarBarType? defTop = null;
            if (!string.IsNullOrWhiteSpace(savedSettings?.InclinedTopBarTypeName))
            {
                defTop = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.InclinedTopBarTypeName, StringComparison.OrdinalIgnoreCase));
            }
            cbInclinedTopBar.SelectedItem = defTop ?? defaultVert;
        }
    }

    private void RestoreCachedInputs(CreateWallRebarRequest cached)
    {
        if (cached == null) return;

        if (rbOneMesh != null && rbTwoMeshes != null)
        {
            if (cached.NumberOfMeshes == 1) rbOneMesh.IsChecked = true;
            else rbTwoMeshes.IsChecked = true;
        }

        if (chkBoundaryElements != null)
        {
            chkBoundaryElements.IsChecked = cached.IncludeBoundaryElements || cached.Typology == WallRebarTypology.BoundaryElements;
            if (gridBoundaryControls != null)
                gridBoundaryControls.IsEnabled = chkBoundaryElements.IsChecked == true;
        }

        if (txtVerticalSpacing != null)
            txtVerticalSpacing.Text = (cached.VerticalSpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        if (txtHorizontalSpacing != null)
            txtHorizontalSpacing.Text = (cached.HorizontalSpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        if (chkUseWallCover != null)
            chkUseWallCover.IsChecked = cached.UseWallCover;

        if (txtCover != null)
            txtCover.Text = (cached.CoverMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        if (chkHorizOutside != null)
            chkHorizOutside.IsChecked = cached.HorizontalBarsOutside;

        if (chkCrossties != null)
            chkCrossties.IsChecked = cached.IncludeCrossties;

        if (txtCrosstieSpacing != null)
        {
            txtCrosstieSpacing.IsEnabled = cached.IncludeCrossties;
            txtCrosstieSpacing.Text = (cached.CrosstieSpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);
        }

        if (cbCrosstieBar != null)
        {
            cbCrosstieBar.IsEnabled = cached.IncludeCrossties;
            if (!string.IsNullOrWhiteSpace(cached.CrosstieBarTypeName))
            {
                var barTypes = cbCrosstieBar.ItemsSource as IEnumerable<RebarBarType>;
                var match = barTypes?.FirstOrDefault(b => b.Name.Equals(cached.CrosstieBarTypeName, StringComparison.OrdinalIgnoreCase));
                if (match != null) cbCrosstieBar.SelectedItem = match;
            }
        }

        if (cbCrosstiePattern != null)
        {
            cbCrosstiePattern.IsEnabled = cached.IncludeCrossties;
            int idx = (cached.CrosstiePlanPattern == 2) ? 1 : ((cached.CrosstiePlanPattern == 0) ? 2 : 0);
            cbCrosstiePattern.SelectedIndex = idx;
        }

        if (cbCrosstieHooks != null)
        {
            cbCrosstieHooks.IsEnabled = cached.IncludeCrossties;
            cbCrosstieHooks.SelectedIndex = Math.Clamp(cached.CrosstieHookMode, 0, 3);
        }

        if (cbVerticalHook != null && !string.IsNullOrWhiteSpace(cached.VerticalHookTypeName))
        {
            var hookItems = cbVerticalHook.ItemsSource as IEnumerable<HookOptionItem>;
            var matchVHook = hookItems?.FirstOrDefault(hi => hi.HookType != null &&
                (hi.HookType.Name.Equals(cached.VerticalHookTypeName, StringComparison.OrdinalIgnoreCase) ||
                 hi.DisplayName.Contains(cached.VerticalHookTypeName, StringComparison.OrdinalIgnoreCase)));

            if (matchVHook != null) cbVerticalHook.SelectedItem = matchVHook;
        }

        if (cbVerticalHookPosition != null)
        {
            cbVerticalHookPosition.SelectedIndex = Math.Clamp(cached.VerticalHookPosition, 0, 2);
            cbVerticalHookPosition.IsEnabled = (cbVerticalHook?.SelectedItem as HookOptionItem)?.HookType != null;
        }

        if (txtBoundaryLength != null)
            txtBoundaryLength.Text = (cached.BoundaryLengthMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        if (txtBoundarySpacing != null)
            txtBoundarySpacing.Text = (cached.BoundaryTieSpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        // Restaurar configuración de aberturas
        if (cbOpeningMeshHook != null)
        {
            var hookItems = cbOpeningMeshHook.ItemsSource as IEnumerable<HookOptionItem>;
            if (!string.IsNullOrWhiteSpace(cached.OpeningMeshHookTypeName))
            {
                var matchOpHook = hookItems?.FirstOrDefault(hi => hi.HookType != null &&
                    (hi.HookType.Name.Equals(cached.OpeningMeshHookTypeName, StringComparison.OrdinalIgnoreCase) ||
                     hi.DisplayName.Contains(cached.OpeningMeshHookTypeName, StringComparison.OrdinalIgnoreCase)));
                if (matchOpHook != null) cbOpeningMeshHook.SelectedItem = matchOpHook;
            }
            else
            {
                cbOpeningMeshHook.SelectedIndex = 0;
            }
        }

        if (chkOpeningPerimeter != null)
            chkOpeningPerimeter.IsChecked = cached.IncludeOpeningPerimeterBars;

        if (cbOpeningPerimeterBar != null && !string.IsNullOrWhiteSpace(cached.OpeningPerimeterBarTypeName))
        {
            var barTypes = cbOpeningPerimeterBar.ItemsSource as IEnumerable<RebarBarType>;
            var match = barTypes?.FirstOrDefault(b => b.Name.Equals(cached.OpeningPerimeterBarTypeName, StringComparison.OrdinalIgnoreCase));
            if (match != null) cbOpeningPerimeterBar.SelectedItem = match;
        }

        if (txtOpeningPerimeterCount != null)
            txtOpeningPerimeterCount.Text = Math.Max(1, cached.OpeningPerimeterBarsPerLayer).ToString(CultureInfo.InvariantCulture);

        if (txtOpeningPerimeterSpacing != null)
            txtOpeningPerimeterSpacing.Text = (cached.OpeningPerimeterSpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        if (txtOpeningPerimeterLd != null)
            txtOpeningPerimeterLd.Text = (cached.OpeningPerimeterAnchorageMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        if (chkOpeningDiagonal != null)
            chkOpeningDiagonal.IsChecked = cached.IncludeOpeningDiagonalBars;

        if (cbOpeningDiagonalBar != null && !string.IsNullOrWhiteSpace(cached.OpeningDiagonalBarTypeName))
        {
            var barTypes = cbOpeningDiagonalBar.ItemsSource as IEnumerable<RebarBarType>;
            var match = barTypes?.FirstOrDefault(b => b.Name.Equals(cached.OpeningDiagonalBarTypeName, StringComparison.OrdinalIgnoreCase));
            if (match != null) cbOpeningDiagonalBar.SelectedItem = match;
        }

        if (txtOpeningDiagonalCount != null)
            txtOpeningDiagonalCount.Text = Math.Max(1, cached.OpeningDiagonalBarsPerCorner).ToString(CultureInfo.InvariantCulture);

        if (txtOpeningDiagonalLength != null)
            txtOpeningDiagonalLength.Text = (cached.OpeningDiagonalLengthMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        if (chkOpeningStirrups != null)
            chkOpeningStirrups.IsChecked = cached.IncludeOpeningEdgeStirrups;

        if (cbOpeningStirrupBar != null && !string.IsNullOrWhiteSpace(cached.OpeningStirrupBarTypeName))
        {
            var barTypes = cbOpeningStirrupBar.ItemsSource as IEnumerable<RebarBarType>;
            var match = barTypes?.FirstOrDefault(b => b.Name.Equals(cached.OpeningStirrupBarTypeName, StringComparison.OrdinalIgnoreCase));
            if (match != null) cbOpeningStirrupBar.SelectedItem = match;
        }

        if (txtOpeningStirrupSpacing != null)
            txtOpeningStirrupSpacing.Text = (cached.OpeningStirrupSpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        if (chkIncludeInclinedTopBar != null)
            chkIncludeInclinedTopBar.IsChecked = cached.IncludeInclinedTopBar;

        if (cbInclinedTopBar != null && !string.IsNullOrWhiteSpace(cached.InclinedTopBarTypeName))
        {
            var barTypes = cbInclinedTopBar.ItemsSource as IEnumerable<RebarBarType>;
            var match = barTypes?.FirstOrDefault(b => b.Name.Equals(cached.InclinedTopBarTypeName, StringComparison.OrdinalIgnoreCase));
            if (match != null) cbInclinedTopBar.SelectedItem = match;
        }

        UpdateSectionVisibilities();
        UpdateOpeningDiagramLayers();
        UpdateInclinedStairDiagram();
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
                    lblCoverInfo.Text = $"Recubrimiento asignado en Revit: {coverCm:0.#} cm ({coverName})";
                    lblCoverInfo.Foreground = System.Windows.Media.Brushes.DarkGreen;
                }
                else
                {
                    txtCover.IsEnabled = true;
                    lblCoverInfo.Text = $"Modo manual activo. (Asignado en muro: {coverCm:0.#} cm - {coverName})";
                    lblCoverInfo.Foreground = System.Windows.Media.Brushes.Gray;
                }
                return;
            }
        }

        // Si no hay muros seleccionados todavía
        if (chkUseWallCover.IsChecked == true)
        {
            txtCover.IsEnabled = false;
            lblCoverInfo.Text = "Se leerá automáticamente del muro al seleccionarlo en Revit.";
            lblCoverInfo.Foreground = System.Windows.Media.Brushes.SteelBlue;
        }
        else
        {
            txtCover.IsEnabled = true;
            lblCoverInfo.Text = "Modo de recubrimiento manual personalizado.";
            lblCoverInfo.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private void ChkUseWallCover_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        UpdateCoverUiFromWall();
    }

    private void BtnReadWallCover_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        if (_selectedWallIds == null || _selectedWallIds.Count == 0)
        {
            TaskDialog.Show("BIM Suite - Recubrimiento", "No hay ningún muro seleccionado aún. Seleccione primero uno o varios muros.");
            return;
        }

        var wallElem = _doc?.GetElement(new ElementId(_selectedWallIds[0])) as Wall;
        if (wallElem != null)
        {
            var (coverFeet, coverName) = WallGeometryHelper.ExtractConcreteCover(_doc!, wallElem);
            double coverCm = UnitConverter.FeetToMeters(coverFeet) * 100.0;
            if (txtCover != null) txtCover.Text = coverCm.ToString("0.#", CultureInfo.InvariantCulture);
            if (lblCoverInfo != null)
            {
                lblCoverInfo.Text = $"Recubrimiento detectado del muro: {coverCm:0.#} cm ({coverName})";
                lblCoverInfo.Foreground = System.Windows.Media.Brushes.DarkGreen;
            }
        }
    }

    private void BtnPickWalls_Click(object sender, RoutedEventArgs e)
    {
        UserWantsToPickWalls = true;
        BuiltRequest = BuildRequestFromInputs();
        DialogResult = false;
        Close();
    }

    private void RbMeshes_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded || chkCrossties == null || rbTwoMeshes == null) return;
        bool isTwo = rbTwoMeshes.IsChecked == true;
        chkCrossties.IsEnabled = isTwo;
        if (!isTwo)
        {
            chkCrossties.IsChecked = false;
        }
        if (txtCrosstieSpacing != null) txtCrosstieSpacing.IsEnabled = isTwo && (chkCrossties.IsChecked == true);
        if (cbCrosstieBar != null) cbCrosstieBar.IsEnabled = isTwo && (chkCrossties.IsChecked == true);
    }

    private void ChkBoundaryElements_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        if (gridBoundaryControls != null)
        {
            gridBoundaryControls.IsEnabled = chkBoundaryElements?.IsChecked == true;
        }
    }

    private void UpdateSectionVisibilities()
    {
        if (!_isLoaded) return;

        bool isInclined = false;
        if (_selectedWallIds.Count > 0)
        {
            foreach (var wallId in _selectedWallIds)
            {
                if (_doc.GetElement(new ElementId(wallId)) is Wall w && WallGeometryHelper.IsInclinedProfileWall(_doc, w))
                {
                    isInclined = true;
                    break;
                }
            }
        }

        if (grpInclinedStair != null)
            grpInclinedStair.Visibility = isInclined ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        if (grpOpenings != null)
        {
            if (isInclined)
            {
                grpOpenings.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                int openingsCount = (_selectedWallIds.Count > 0) ? WallGeometryHelper.CountOpeningsInWalls(_doc, _selectedWallIds) : 0;
                grpOpenings.Visibility = (openingsCount > 0) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
        }
    }

    private void InclinedStairOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        if (gridInclinedTopBar != null)
            gridInclinedTopBar.IsEnabled = chkIncludeInclinedTopBar?.IsChecked == true;
        UpdateInclinedStairDiagram();
    }

    private void UpdateInclinedStairDiagram()
    {
        if (!_isLoaded || cvsInclinedStair == null || vizInclinedVertBars == null ||
            vizInclinedHorizBars == null || vizInclinedTopBar == null) return;

        vizInclinedVertBars.Children.Clear();
        vizInclinedHorizBars.Children.Clear();
        vizInclinedTopBar.Children.Clear();

        double xLeft = 25.0;
        double xRight = 375.0;
        double yBase = 160.0;
        double yTopLeft = 35.0;
        double yTopRight = 115.0;

        double GetYTopAtX(double x)
        {
            double factor = (x - xLeft) / (xRight - xLeft);
            return yTopLeft + factor * (yTopRight - yTopLeft);
        }

        double GetXMaxAtY(double y)
        {
            if (y >= yTopRight) return xRight;
            if (y <= yTopLeft) return xLeft;
            double factor = (y - yTopLeft) / (yTopRight - yTopLeft);
            return xLeft + factor * (xRight - xLeft);
        }

        var blueBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0284C7"));
        var greenBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#16A34A"));
        var redBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DC2626"));

        // 1. Barras Verticales con Varying Rebar Set (se adaptan dinámicamente al perfil inclinado)
        int numVertBars = 14;
        double xStep = (xRight - xLeft - 24.0) / (numVertBars - 1);
        for (int i = 0; i < numVertBars; i++)
        {
            double x = (xLeft + 12.0) + i * xStep;
            double yTopBar = GetYTopAtX(x) + 7.0; // Recubrimiento superior
            double yBotBar = yBase - 6.0;         // Recubrimiento inferior

            vizInclinedVertBars.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x,
                Y1 = yBotBar,
                X2 = x,
                Y2 = yTopBar,
                Stroke = blueBrush,
                StrokeThickness = 2.2,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round
            });
        }

        // 2. Barras Horizontales (Alternativa 1: Acortamiento progresivo conforme aumenta la elevación)
        double yHorizStart = yBase - 14.0;
        double yHorizEnd = yTopLeft + 14.0;
        int numHorizBars = 7;
        double yStep = (yHorizStart - yHorizEnd) / (numHorizBars - 1);

        for (int j = 0; j < numHorizBars; j++)
        {
            double y = yHorizStart - j * yStep;
            double xMax = GetXMaxAtY(y) - 8.0;
            double xMin = xLeft + 8.0;

            if (xMax > xMin + 15.0)
            {
                vizInclinedHorizBars.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = xMin,
                    Y1 = y,
                    X2 = xMax,
                    Y2 = y,
                    Stroke = greenBrush,
                    StrokeThickness = 1.8,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 1.5 }
                });
            }
        }

        // 3. Barra perimetral superior (Dintel inclinado)
        if (chkIncludeInclinedTopBar?.IsChecked == true)
        {
            double dx = xRight - xLeft;
            double dy = yTopRight - yTopLeft;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double ux = dx / len;
            double uy = dy / len;

            double nx = -uy;
            double ny = ux;
            if (ny < 0) { nx = -nx; ny = -ny; }

            double coverOffset = 7.0;
            double x1 = (xLeft + 10.0 * ux) + nx * coverOffset;
            double y1 = (yTopLeft + 10.0 * uy) + ny * coverOffset;
            double x2 = (xRight - 10.0 * ux) + nx * coverOffset;
            double y2 = (yTopRight - 10.0 * uy) + ny * coverOffset;

            vizInclinedTopBar.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = redBrush,
                StrokeThickness = 2.6,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round
            });
        }
    }

    private void CbVerticalHook_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || cbVerticalHookPosition == null || cbVerticalHook == null) return;
        var selected = cbVerticalHook.SelectedItem as HookOptionItem;
        cbVerticalHookPosition.IsEnabled = (selected?.HookType != null);
    }

    private void ChkCrossties_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded || chkCrossties == null) return;
        bool isChecked = chkCrossties.IsChecked == true;
        if (txtCrosstieSpacing != null) txtCrosstieSpacing.IsEnabled = isChecked;
        if (cbCrosstieBar != null) cbCrosstieBar.IsEnabled = isChecked;
        if (cbCrosstiePattern != null) cbCrosstiePattern.IsEnabled = isChecked;
        if (cbCrosstieHooks != null) cbCrosstieHooks.IsEnabled = isChecked;
    }

    private void OpeningOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        UpdateOpeningDiagramLayers();
    }

    private void OpeningCount_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoaded) return;
        UpdateOpeningDiagramLayers();
    }

    private void UpdateOpeningDiagramLayers()
    {
        if (!_isLoaded) return;

        bool hasPerim = chkOpeningPerimeter?.IsChecked == true;
        bool hasDiag = chkOpeningDiagonal?.IsChecked == true;
        bool hasStir = chkOpeningStirrups?.IsChecked == true;
        bool hasMeshHook = (cbOpeningMeshHook?.SelectedItem as HookOptionItem)?.HookType != null;

        if (gridOpeningPerimeter != null) gridOpeningPerimeter.IsEnabled = hasPerim;
        if (gridOpeningDiagonal != null) gridOpeningDiagonal.IsEnabled = hasDiag;
        if (gridOpeningStirrups != null) gridOpeningStirrups.IsEnabled = hasStir;

        if (vizMeshHooks != null)
            vizMeshHooks.Visibility = hasMeshHook ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        if (vizEdgeStirrups != null)
            vizEdgeStirrups.Visibility = hasStir ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        // --- Actualizar dinámicamente barras perimetrales en el esquema ---
        if (vizPerimeterBars != null)
        {
            vizPerimeterBars.Visibility = hasPerim ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            vizPerimeterBars.Children.Clear();

            if (hasPerim)
            {
                int pCount = 2;
                if (txtOpeningPerimeterCount != null && int.TryParse(txtOpeningPerimeterCount.Text.Trim(), out int parsedP))
                    pCount = Math.Clamp(parsedP, 1, 4);

                var blueBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0284C7"));
                double step = 5.0;

                for (int k = 0; k < pCount; k++)
                {
                    double dTop = 57.0 - k * step;
                    double dBot = 132.0 + k * step;
                    double dLeft = 130.0 - k * step;
                    double dRight = 280.0 + k * step;

                    // Dintel Superior
                    vizPerimeterBars.Children.Add(new System.Windows.Shapes.Line { X1 = 85, Y1 = dTop, X2 = 325, Y2 = dTop, Stroke = blueBrush, StrokeThickness = 2.0 });
                    // Alféizar Inferior
                    vizPerimeterBars.Children.Add(new System.Windows.Shapes.Line { X1 = 85, Y1 = dBot, X2 = 325, Y2 = dBot, Stroke = blueBrush, StrokeThickness = 2.0 });
                    // Lateral Izquierdo
                    vizPerimeterBars.Children.Add(new System.Windows.Shapes.Line { X1 = dLeft, Y1 = 26, X2 = dLeft, Y2 = 163, Stroke = blueBrush, StrokeThickness = 2.0 });
                    // Lateral Derecho
                    vizPerimeterBars.Children.Add(new System.Windows.Shapes.Line { X1 = dRight, Y1 = 26, X2 = dRight, Y2 = 163, Stroke = blueBrush, StrokeThickness = 2.0 });
                }

                // Ajustar visualmente los estribos en el esquema para que envuelvan al paquete de barras sin invadir el vano
                if (hasStir && vizEdgeStirrups != null)
                {
                    double stirThick = (pCount - 1) * step + 8.0;
                    double topY = 60.0 - stirThick;
                    double botY = 129.0;
                    double leftX = 133.0 - stirThick;
                    double rightX = 277.0;

                    foreach (var child in vizEdgeStirrups.Children)
                    {
                        if (child is System.Windows.Shapes.Rectangle rect && rect.Name != null)
                        {
                            if (rect.Name.StartsWith("stTop"))
                            {
                                Canvas.SetTop(rect, topY);
                                rect.Height = stirThick;
                            }
                            else if (rect.Name.StartsWith("stBot"))
                            {
                                Canvas.SetTop(rect, botY);
                                rect.Height = stirThick;
                            }
                            else if (rect.Name.StartsWith("stLeft"))
                            {
                                Canvas.SetLeft(rect, leftX);
                                rect.Width = stirThick;
                            }
                            else if (rect.Name.StartsWith("stRight"))
                            {
                                Canvas.SetLeft(rect, rightX);
                                rect.Width = stirThick;
                            }
                        }
                    }
                }
            }
        }

        // --- Actualizar dinámicamente barras diagonales a 45° en el esquema ---
        if (vizDiagonalBars != null)
        {
            vizDiagonalBars.Visibility = hasDiag ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            vizDiagonalBars.Children.Clear();

            if (hasDiag)
            {
                int dCount = 1;
                if (txtOpeningDiagonalCount != null && int.TryParse(txtOpeningDiagonalCount.Text.Trim(), out int parsedD))
                    dCount = Math.Clamp(parsedD, 1, 4);

                var orangeBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EA580C"));
                double diagStep = 6.0;

                for (int k = 0; k < dCount; k++)
                {
                    double u = k * diagStep;
                    // Esquina Superior Izquierda (inclinada /, contenida holgadamente dentro del muro)
                    vizDiagonalBars.Children.Add(new System.Windows.Shapes.Line { X1 = 106 - u, Y1 = 82 - u, X2 = 154 - u, Y2 = 34 - u, Stroke = orangeBrush, StrokeThickness = 2.2 });
                    // Esquina Superior Derecha (inclinada \, contenida holgadamente dentro del muro)
                    vizDiagonalBars.Children.Add(new System.Windows.Shapes.Line { X1 = 256 + u, Y1 = 34 - u, X2 = 304 + u, Y2 = 82 - u, Stroke = orangeBrush, StrokeThickness = 2.2 });
                    // Esquina Inferior Izquierda (inclinada \, contenida holgadamente dentro del muro)
                    vizDiagonalBars.Children.Add(new System.Windows.Shapes.Line { X1 = 106 - u, Y1 = 107 + u, X2 = 154 - u, Y2 = 155 + u, Stroke = orangeBrush, StrokeThickness = 2.2 });
                    // Esquina Inferior Derecha (inclinada /, contenida holgadamente dentro del muro)
                    vizDiagonalBars.Children.Add(new System.Windows.Shapes.Line { X1 = 256 + u, Y1 = 155 + u, X2 = 304 + u, Y2 = 107 + u, Stroke = orangeBrush, StrokeThickness = 2.2 });
                }
            }
        }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWallIds == null || _selectedWallIds.Count == 0)
        {
            TaskDialog.Show("BIM Suite - Validación", "Debe seleccionar al menos un muro en el modelo.");
            return;
        }

        var request = BuildRequestFromInputs();
        if (request == null) return;

        BuiltRequest = request;
        WallRebarSettingsStore.Save(request);

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private CreateWallRebarRequest? BuildRequestFromInputs()
    {
        if (txtVerticalSpacing == null || txtHorizontalSpacing == null || txtCover == null) return null;

        if (!TryParseDouble(txtVerticalSpacing.Text, out double sVCm) || sVCm <= 0)
        {
            TaskDialog.Show("BIM Suite - Validación", "El espaciamiento vertical (Sv) debe ser un número positivo.");
            return null;
        }

        if (!TryParseDouble(txtHorizontalSpacing.Text, out double sHCm) || sHCm <= 0)
        {
            TaskDialog.Show("BIM Suite - Validación", "El espaciamiento horizontal (Sh) debe ser un número positivo.");
            return null;
        }

        if (!TryParseDouble(txtCover.Text, out double coverCm) || coverCm < 0)
        {
            TaskDialog.Show("BIM Suite - Validación", "El recubrimiento de concreto debe ser un número positivo.");
            return null;
        }

        string? crosstieBarName = null;
        double crosstieSpacingMeters = 0.40;
        int crosstiePlanPattern = 1;
        int crosstieHookMode = 0;
        if (chkCrossties?.IsChecked == true)
        {
            if (cbCrosstieBar?.SelectedItem is RebarBarType ctBar)
            {
                crosstieBarName = ctBar.Name;
            }
            if (txtCrosstieSpacing != null)
            {
                if (!TryParseDouble(txtCrosstieSpacing.Text, out double ctCm) || ctCm <= 0)
                {
                    TaskDialog.Show("BIM Suite - Validación", "El espaciamiento vertical de grapas debe ser un número positivo.");
                    return null;
                }
                crosstieSpacingMeters = ctCm / 100.0;
            }

            int patIdx = cbCrosstiePattern?.SelectedIndex ?? 0;
            if (patIdx == 1) crosstiePlanPattern = 2; // Dejando 2 barras
            else if (patIdx == 2) crosstiePlanPattern = 0; // En cada barra (consecutiva)
            else crosstiePlanPattern = 1; // Dejando 1 barra (alternada)

            crosstieHookMode = Math.Clamp(cbCrosstieHooks?.SelectedIndex ?? 0, 0, 3);
        }

        bool incBoundary = chkBoundaryElements?.IsChecked == true;
        double boundaryLenMeters = 0.40;
        double boundaryTieSpacingMeters = 0.10;
        if (incBoundary && txtBoundaryLength != null && txtBoundarySpacing != null)
        {
            if (!TryParseDouble(txtBoundaryLength.Text, out double blCm) || blCm <= 0)
            {
                TaskDialog.Show("BIM Suite - Validación", "La longitud del elemento de borde debe ser positiva.");
                return null;
            }
            if (!TryParseDouble(txtBoundarySpacing.Text, out double bsCm) || bsCm <= 0)
            {
                TaskDialog.Show("BIM Suite - Validación", "El espaciamiento de estribos de borde debe ser positivo.");
                return null;
            }
            boundaryLenMeters = blCm / 100.0;
            boundaryTieSpacingMeters = bsCm / 100.0;
        }

        var vertBar = cbVerticalBar?.SelectedItem as RebarBarType;
        if (vertBar == null)
        {
            TaskDialog.Show("BIM Suite - Validación", "Debe seleccionar un tipo de barra para el refuerzo vertical. Asegúrese de que el proyecto tenga tipos de barra de refuerzo cargados.");
            return null;
        }

        var vHookItem = cbVerticalHook?.SelectedItem as HookOptionItem;
        int vHookPos = cbVerticalHookPosition?.SelectedIndex ?? 0;

        var horizBar = cbHorizontalBar?.SelectedItem as RebarBarType;
        if (horizBar == null)
        {
            TaskDialog.Show("BIM Suite - Validación", "Debe seleccionar un tipo de barra para el refuerzo horizontal.");
            return null;
        }

        var hookItem = cbHorizontalHook?.SelectedItem as HookOptionItem;

        // Configuración de refuerzo en aberturas
        var opMeshHookItem = cbOpeningMeshHook?.SelectedItem as HookOptionItem;
        bool incOpPerimeter = chkOpeningPerimeter?.IsChecked == true;
        var opPerimBar = cbOpeningPerimeterBar?.SelectedItem as RebarBarType ?? vertBar;
        int opPerimCount = 2;
        if (txtOpeningPerimeterCount != null && int.TryParse(txtOpeningPerimeterCount.Text.Trim(), out int parsedPCount) && parsedPCount > 0)
        {
            opPerimCount = Math.Clamp(parsedPCount, 1, 6);
        }
        double opPerimLdMeters = 0.50;
        if (txtOpeningPerimeterLd != null && TryParseDouble(txtOpeningPerimeterLd.Text, out double parsedLdCm) && parsedLdCm > 0)
        {
            opPerimLdMeters = parsedLdCm / 100.0;
        }

        double opPerimSpacingMeters = 0.10;
        if (txtOpeningPerimeterSpacing != null && TryParseDouble(txtOpeningPerimeterSpacing.Text, out double parsedPSpCm) && parsedPSpCm > 0)
        {
            opPerimSpacingMeters = Math.Clamp(parsedPSpCm, 3.0, 50.0) / 100.0;
        }

        bool incOpDiagonal = chkOpeningDiagonal?.IsChecked == true;
        var opDiagBar = cbOpeningDiagonalBar?.SelectedItem as RebarBarType ?? vertBar;
        int opDiagCount = 1;
        if (txtOpeningDiagonalCount != null && int.TryParse(txtOpeningDiagonalCount.Text.Trim(), out int parsedDCount) && parsedDCount > 0)
        {
            opDiagCount = Math.Clamp(parsedDCount, 1, 4);
        }
        double opDiagLenMeters = 0.90;
        if (txtOpeningDiagonalLength != null && TryParseDouble(txtOpeningDiagonalLength.Text, out double parsedDLenCm) && parsedDLenCm > 0)
        {
            opDiagLenMeters = parsedDLenCm / 100.0;
        }

        bool incOpStirrups = chkOpeningStirrups?.IsChecked == true;
        var opStirBar = cbOpeningStirrupBar?.SelectedItem as RebarBarType ?? horizBar;
        double opStirSpacingMeters = 0.10;
        if (txtOpeningStirrupSpacing != null && TryParseDouble(txtOpeningStirrupSpacing.Text, out double parsedStirSpCm) && parsedStirSpCm > 0)
        {
            opStirSpacingMeters = parsedStirSpCm / 100.0;
        }

        bool incInclinedTop = chkIncludeInclinedTopBar?.IsChecked == true;
        var inclinedTopBar = cbInclinedTopBar?.SelectedItem as RebarBarType ?? vertBar;

        bool isInclined = false;
        if (_selectedWallIds.Count > 0)
        {
            foreach (var wallId in _selectedWallIds)
            {
                if (_doc.GetElement(new ElementId(wallId)) is Wall w && WallGeometryHelper.IsInclinedProfileWall(_doc, w))
                {
                    isInclined = true;
                    break;
                }
            }
        }

        WallRebarTypology derivedTypology = WallRebarTypology.StandardMesh;
        if (isInclined) derivedTypology = WallRebarTypology.InclinedStairWall;
        else if (incBoundary) derivedTypology = WallRebarTypology.BoundaryElements;
        else if (hookItem?.HookType != null) derivedTypology = WallRebarTypology.EndHooks;

        return new CreateWallRebarRequest
        {
            WallElementIds = new List<long>(_selectedWallIds ?? Enumerable.Empty<long>()),
            NumberOfMeshes = rbOneMesh?.IsChecked == true ? 1 : 2,
            Typology = derivedTypology,
            IncludeBoundaryElements = incBoundary,
            VerticalBarTypeName = vertBar?.Name ?? string.Empty,
            VerticalSpacingMeters = sVCm / 100.0,
            VerticalTopOffsetMeters = coverCm / 100.0,
            VerticalBottomOffsetMeters = coverCm / 100.0,
            VerticalHookTypeName = vHookItem?.HookType?.Name,
            VerticalHookPosition = vHookPos,
            HorizontalBarTypeName = horizBar?.Name ?? string.Empty,
            HorizontalSpacingMeters = sHCm / 100.0,
            HorizontalStartOffsetMeters = coverCm / 100.0,
            HorizontalEndOffsetMeters = coverCm / 100.0,
            HorizontalHookTypeName = hookItem?.HookType?.Name,
            HorizontalBarsOutside = chkHorizOutside?.IsChecked == true,
            UseWallCover = chkUseWallCover?.IsChecked == true,
            CoverMeters = coverCm / 100.0,
            IncludeCrossties = chkCrossties?.IsChecked == true,
            CrosstieBarTypeName = crosstieBarName,
            CrosstieSpacingMeters = crosstieSpacingMeters,
            CrosstiePlanPattern = crosstiePlanPattern,
            CrosstieHookMode = crosstieHookMode,
            BoundaryLengthMeters = boundaryLenMeters,
            BoundaryTieSpacingMeters = boundaryTieSpacingMeters,
            OpeningMeshHookTypeName = opMeshHookItem?.HookType?.Name,
            IncludeOpeningPerimeterBars = incOpPerimeter,
            OpeningPerimeterBarTypeName = opPerimBar?.Name,
            OpeningPerimeterBarsPerLayer = opPerimCount,
            OpeningPerimeterAnchorageMeters = opPerimLdMeters,
            OpeningPerimeterSpacingMeters = opPerimSpacingMeters,
            IncludeOpeningDiagonalBars = incOpDiagonal,
            OpeningDiagonalBarTypeName = opDiagBar?.Name,
            OpeningDiagonalBarsPerCorner = opDiagCount,
            OpeningDiagonalLengthMeters = opDiagLenMeters,
            IncludeOpeningEdgeStirrups = incOpStirrups,
            OpeningStirrupBarTypeName = opStirBar?.Name,
            OpeningStirrupSpacingMeters = opStirSpacingMeters,
            IncludeInclinedTopBar = incInclinedTop,
            InclinedTopBarTypeName = inclinedTopBar?.Name
        };
    }

    private static bool TryParseDouble(string text, out double value)
    {
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}
