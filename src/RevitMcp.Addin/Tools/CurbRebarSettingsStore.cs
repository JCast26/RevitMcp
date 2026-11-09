using System;
using System.IO;
using System.Text.Json;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Almacena y recupera la configuración empleada para el modelado de sardineles y muretes bajos,
/// en la memoria de la sesión de Revit y en un archivo JSON en AppData.
/// </summary>
public static class CurbRebarSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RevitMcp");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "CurbRebarSettings.json");

    private static CreateCurbRebarRequest? _sessionSettings;

    public static void Save(CreateCurbRebarRequest request)
    {
        if (request == null) return;

        _sessionSettings = new CreateCurbRebarRequest
        {
            StirrupType = request.StirrupType,
            StirrupBarTypeName = request.StirrupBarTypeName,
            StirrupSpacingMeters = request.StirrupSpacingMeters,
            StirrupHookTypeName = request.StirrupHookTypeName,
            LongitudinalBarCount = request.LongitudinalBarCount,
            LongitudinalBarTypeName = request.LongitudinalBarTypeName,
            ExtendAtCorners = request.ExtendAtCorners,
            CornerHookTypeName = request.CornerHookTypeName,
            UseWallCover = request.UseWallCover,
            CoverMeters = request.CoverMeters,
            StartOffsetMeters = request.StartOffsetMeters,
            EndOffsetMeters = request.EndOffsetMeters
        };

        try
        {
            if (!Directory.Exists(SettingsDirectory))
            {
                Directory.CreateDirectory(SettingsDirectory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_sessionSettings, options);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch { }
    }

    public static CreateCurbRebarRequest Load()
    {
        if (_sessionSettings != null) return _sessionSettings;

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<CreateCurbRebarRequest>(json);
                if (loaded != null)
                {
                    _sessionSettings = loaded;
                    return loaded;
                }
            }
        }
        catch { }

        var defaultSettings = new CreateCurbRebarRequest
        {
            StirrupType = CurbStirrupType.OpenC,
            StirrupBarTypeName = string.Empty,
            StirrupSpacingMeters = 0.20,
            LongitudinalBarCount = 2,
            LongitudinalBarTypeName = string.Empty,
            ExtendAtCorners = true,
            CornerHookTypeName = string.Empty,
            UseWallCover = true,
            CoverMeters = 0.025,
            StartOffsetMeters = 0.05,
            EndOffsetMeters = 0.05
        };

        _sessionSettings = defaultSettings;
        return defaultSettings;
    }
}
