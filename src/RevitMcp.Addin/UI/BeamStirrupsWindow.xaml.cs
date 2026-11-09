using System.Globalization;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMcp.Addin.Tools;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin.UI;

/// <summary>
/// Filtro de selección interactiva para aceptar únicamente vigas estructurales.
/// </summary>
public class StructuralFramingSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem)
    {
        return elem is FamilyInstance fi && fi.Category?.Id.Value == (int)BuiltInCategory.OST_StructuralFraming;
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}

/// <summary>
/// Ventana de configuración de estribos zonificados para vigas.
/// Funciona de forma pura como recopilador de datos, delegando las transacciones de Revit
/// al comando externo para garantizar estabilidad total del proceso.
/// </summary>
public partial class BeamStirrupsWindow : Window
{
    private readonly UIDocument _uidoc;
    private readonly Document _doc;
    private readonly List<long> _selectedBeamIds;

    public bool UserWantsToPickBeams { get; private set; }
    public CreateBeamStirrupsRequest? BuiltRequest { get; private set; }

    public BeamStirrupsWindow(UIDocument uidoc, List<long> initialBeamIds, CreateBeamStirrupsRequest? cachedRequest = null)
    {
        InitializeComponent();
        _uidoc = uidoc;
        _doc = uidoc.Document;
        _selectedBeamIds = initialBeamIds ?? new List<long>();

        // Prioridad 1: cachedRequest (si viene de selección en curso en Revit)
        // Prioridad 2: Última configuración guardada por el usuario (persistencia entre ejecuciones)
        var settingsToRestore = cachedRequest ?? BeamStirrupsSettingsStore.Load();

        LoadDocumentRebarData(settingsToRestore);
        UpdateSelectionUi();

        if (settingsToRestore != null)
        {
            RestoreCachedInputs(settingsToRestore);
        }
    }

    private void UpdateSelectionUi()
    {
        if (_selectedBeamIds.Count > 0)
        {
            lblSelectionStatus.Text = $"{_selectedBeamIds.Count} viga(s) seleccionada(s)";
            lblSelectionStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
        }
        else
        {
            lblSelectionStatus.Text = "0 vigas seleccionadas (requerido)";
            lblSelectionStatus.Foreground = System.Windows.Media.Brushes.Crimson;
        }
    }

    private void LoadDocumentRebarData(CreateBeamStirrupsRequest? savedSettings = null)
    {
        var barTypes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .OrderBy(b => b.Name)
            .ToList();

        cbBarType.ItemsSource = barTypes;
        cbBarType.DisplayMemberPath = "Name";

        // Prioridad: Seleccionar el último tipo de barra guardado por el usuario
        RebarBarType? defaultBar = null;
        if (!string.IsNullOrWhiteSpace(savedSettings?.RebarBarTypeName))
        {
            defaultBar = barTypes.FirstOrDefault(b => b.Name.Equals(savedSettings.RebarBarTypeName, StringComparison.OrdinalIgnoreCase))
                      ?? barTypes.FirstOrDefault(b => b.Name.Contains(savedSettings.RebarBarTypeName, StringComparison.OrdinalIgnoreCase));
        }

        if (defaultBar == null)
        {
            defaultBar = barTypes.FirstOrDefault(b => b.Name.Contains("3/8") || b.Name.Contains("10") || b.Name.Contains("#3"))
                      ?? barTypes.FirstOrDefault();
        }

        if (defaultBar != null) cbBarType.SelectedItem = defaultBar;

        var shapes = new FilteredElementCollector(_doc)
            .OfClass(typeof(RebarShape))
            .Cast<RebarShape>()
            .OrderBy(s => s.Name)
            .ToList();

        var stirrupShapes = shapes.Where(s => s.RebarStyle == RebarStyle.StirrupTie).ToList();
        var pool = stirrupShapes.Any() ? stirrupShapes : shapes;

        cbShape.ItemsSource = pool;
        cbShape.DisplayMemberPath = "Name";

        // Prioridad: Seleccionar la última forma de estribo guardada por el usuario
        RebarShape? defaultShape = null;
        if (!string.IsNullOrWhiteSpace(savedSettings?.RebarShapeName))
        {
            defaultShape = pool.FirstOrDefault(s => s.Name.Equals(savedSettings.RebarShapeName, StringComparison.OrdinalIgnoreCase))
                        ?? pool.FirstOrDefault(s => s.Name.Contains(savedSettings.RebarShapeName, StringComparison.OrdinalIgnoreCase));
        }

        if (defaultShape == null)
        {
            defaultShape = pool.FirstOrDefault(s => s.Name.Contains("T1") || s.Name.Contains("51") || s.Name.Contains("Stirrup"))
                        ?? pool.FirstOrDefault();
        }

        if (defaultShape != null) cbShape.SelectedItem = defaultShape;
    }

    private void RestoreCachedInputs(CreateBeamStirrupsRequest cached)
    {
        txtS0.Text = (cached.FirstStirrupOffsetMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);
        txtN1.Text = cached.Zone1Count.ToString();
        txtS1.Text = (cached.Zone1SpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);
        txtS2.Text = (cached.Zone2MaxSpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        chkSymmetric.IsChecked = cached.IsSymmetric;
        txtN3.Text = cached.Zone3Count.ToString();
        txtS3.Text = (cached.Zone3SpacingMeters * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        chkLongitudinal.IsChecked = cached.IncludeLongitudinalBars;

        if (!string.IsNullOrWhiteSpace(cached.RebarBarTypeName))
        {
            var match = (cbBarType.ItemsSource as IEnumerable<RebarBarType>)?.FirstOrDefault(b => b.Name == cached.RebarBarTypeName);
            if (match != null) cbBarType.SelectedItem = match;
        }

        if (!string.IsNullOrWhiteSpace(cached.RebarShapeName))
        {
            var match = (cbShape.ItemsSource as IEnumerable<RebarShape>)?.FirstOrDefault(s => s.Name == cached.RebarShapeName);
            if (match != null) cbShape.SelectedItem = match;
        }
    }

    private void BtnPickBeams_Click(object sender, RoutedEventArgs e)
    {
        UserWantsToPickBeams = true;
        BuildCurrentRequest(silent: true);
        if (BuiltRequest != null)
        {
            BeamStirrupsSettingsStore.Save(BuiltRequest);
        }
        this.DialogResult = false;
        this.Close();
    }

    private void ChkSymmetric_Changed(object sender, RoutedEventArgs e)
    {
        if (gridZone3Custom != null)
        {
            gridZone3Custom.IsEnabled = chkSymmetric.IsChecked != true;
        }
    }

    private void ChkNormative_Checked(object sender, RoutedEventArgs e)
    {
        if (_selectedBeamIds.Count == 0) return;

        var firstBeam = _doc.GetElement(new ElementId(_selectedBeamIds[0])) as FamilyInstance;
        if (firstBeam != null)
        {
            var geom = BeamGeometryHelper.ExtractGeometry(_doc, firstBeam);
            double hCm = geom.HeightMeters * 100.0;
            double lcCm = 2.0 * hCm;
            double s1Cm = ParseDouble(txtS1.Text, 10.0);
            int nSuggest = Math.Max(6, (int)Math.Ceiling(lcCm / s1Cm));
            txtN1.Text = nSuggest.ToString();
            txtN3.Text = nSuggest.ToString();
        }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBeamIds.Count == 0)
        {
            TaskDialog.Show("BIM Suite - Aviso", "Por favor seleccione al menos una viga de concreto antes de aplicar.");
            return;
        }

        if (!BuildCurrentRequest(silent: false))
        {
            return;
        }

        if (BuiltRequest != null)
        {
            BeamStirrupsSettingsStore.Save(BuiltRequest);
        }

        this.DialogResult = true;
        this.Close();
    }

    private bool BuildCurrentRequest(bool silent)
    {
        var barType = cbBarType.SelectedItem as RebarBarType;
        if (barType == null)
        {
            if (!silent) TaskDialog.Show("BIM Suite - Aviso", "Por favor elija un diámetro de barra de armadura.");
            return false;
        }

        var shape = cbShape.SelectedItem as RebarShape;
        if (shape == null)
        {
            if (!silent) TaskDialog.Show("BIM Suite - Aviso", "Por favor elija una forma de estribo (RebarShape).");
            return false;
        }

        double s0Meters = ParseDouble(txtS0.Text, 5.0) / 100.0;
        int n1 = ParseInt(txtN1.Text, 6);
        double s1Meters = ParseDouble(txtS1.Text, 10.0) / 100.0;
        double s2Meters = ParseDouble(txtS2.Text, 20.0) / 100.0;

        bool isSymmetric = chkSymmetric.IsChecked == true;
        int n3 = isSymmetric ? n1 : ParseInt(txtN3.Text, 6);
        double s3Meters = isSymmetric ? s1Meters : ParseDouble(txtS3.Text, 10.0) / 100.0;

        BuiltRequest = new CreateBeamStirrupsRequest
        {
            BeamElementIds = new List<long>(_selectedBeamIds),
            RebarBarTypeName = barType.Name,
            RebarShapeName = shape.Name,
            FirstStirrupOffsetMeters = s0Meters,
            Zone1Count = n1,
            Zone1SpacingMeters = s1Meters,
            Zone2MaxSpacingMeters = s2Meters,
            IsSymmetric = isSymmetric,
            Zone3Count = n3,
            Zone3SpacingMeters = s3Meters,
            IncludeLongitudinalBars = chkLongitudinal.IsChecked == true
        };

        return true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
        this.Close();
    }

    private static double ParseDouble(string input, double fallback)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;
        input = input.Replace(',', '.');
        return double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : fallback;
    }

    private static int ParseInt(string input, int fallback)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;
        return int.TryParse(input, out int val) ? val : fallback;
    }
}
