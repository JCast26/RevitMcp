using System;
using System.IO;
using System.Text.Json;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Almacena y recupera la configuración empleada para el modelado de armaduras en muros,
/// tanto en la memoria de la sesión de Revit como en un archivo JSON en AppData.
/// </summary>
public static class WallRebarSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RevitMcp");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "WallRebarSettings.json");

    private static CreateWallRebarRequest? _sessionSettings;

    public static void Save(CreateWallRebarRequest request)
    {
        if (request == null) return;

        _sessionSettings = new CreateWallRebarRequest
        {
            NumberOfMeshes = request.NumberOfMeshes,
            Typology = request.Typology,
            VerticalBarTypeName = request.VerticalBarTypeName,
            VerticalSpacingMeters = request.VerticalSpacingMeters,
            VerticalTopOffsetMeters = request.VerticalTopOffsetMeters,
            VerticalBottomOffsetMeters = request.VerticalBottomOffsetMeters,
            VerticalHookTypeName = request.VerticalHookTypeName,
            VerticalHookPosition = request.VerticalHookPosition,
            HorizontalBarTypeName = request.HorizontalBarTypeName,
            HorizontalSpacingMeters = request.HorizontalSpacingMeters,
            HorizontalStartOffsetMeters = request.HorizontalStartOffsetMeters,
            HorizontalEndOffsetMeters = request.HorizontalEndOffsetMeters,
            HorizontalHookTypeName = request.HorizontalHookTypeName,
            HorizontalBarsOutside = request.HorizontalBarsOutside,
            UseWallCover = request.UseWallCover,
            CoverMeters = request.CoverMeters,
            IncludeCrossties = request.IncludeCrossties,
            CrosstieBarTypeName = request.CrosstieBarTypeName,
            CrosstieSpacingMeters = request.CrosstieSpacingMeters,
            CrosstiePlanPattern = request.CrosstiePlanPattern,
            CrosstieHookMode = request.CrosstieHookMode,
            IncludeBoundaryElements = request.IncludeBoundaryElements,
            BoundaryLengthMeters = request.BoundaryLengthMeters,
            BoundaryTieSpacingMeters = request.BoundaryTieSpacingMeters,
            BoundaryTieBarTypeName = request.BoundaryTieBarTypeName,
            BoundaryTieShapeName = request.BoundaryTieShapeName,
            OpeningMeshHookTypeName = request.OpeningMeshHookTypeName,
            IncludeOpeningPerimeterBars = request.IncludeOpeningPerimeterBars,
            OpeningPerimeterBarTypeName = request.OpeningPerimeterBarTypeName,
            OpeningPerimeterBarsPerLayer = request.OpeningPerimeterBarsPerLayer,
            OpeningPerimeterAnchorageMeters = request.OpeningPerimeterAnchorageMeters,
            OpeningPerimeterSpacingMeters = request.OpeningPerimeterSpacingMeters,
            IncludeOpeningDiagonalBars = request.IncludeOpeningDiagonalBars,
            OpeningDiagonalBarTypeName = request.OpeningDiagonalBarTypeName,
            OpeningDiagonalBarsPerCorner = request.OpeningDiagonalBarsPerCorner,
            OpeningDiagonalLengthMeters = request.OpeningDiagonalLengthMeters,
            IncludeOpeningEdgeStirrups = request.IncludeOpeningEdgeStirrups,
            OpeningStirrupBarTypeName = request.OpeningStirrupBarTypeName,
            OpeningStirrupSpacingMeters = request.OpeningStirrupSpacingMeters,
            IncludeInclinedTopBar = request.IncludeInclinedTopBar,
            InclinedTopBarTypeName = request.InclinedTopBarTypeName
        };

        try
        {
            if (!Directory.Exists(SettingsDirectory))
            {
                Directory.CreateDirectory(SettingsDirectory);
            }

            var json = JsonSerializer.Serialize(_sessionSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Fallback silencioso
        }
    }

    public static CreateWallRebarRequest? Load()
    {
        if (_sessionSettings != null)
        {
            return _sessionSettings;
        }

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                _sessionSettings = JsonSerializer.Deserialize<CreateWallRebarRequest>(json);
                return _sessionSettings;
            }
        }
        catch
        {
            // Ignorar
        }

        return null;
    }
}
