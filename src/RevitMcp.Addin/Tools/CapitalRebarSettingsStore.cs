using System;
using System.IO;
using System.Text.Json;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Almacena y recupera la última configuración utilizada para el armado de capiteles,
/// en la memoria de sesión de Revit y en un archivo JSON en AppData.
/// </summary>
public static class CapitalRebarSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RevitMcp");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "CapitalRebarSettings.json");

    private static CreateCapitalRebarRequest? _sessionSettings;

    public static void Save(CreateCapitalRebarRequest request)
    {
        if (request == null) return;

        _sessionSettings = new CreateCapitalRebarRequest
        {
            VerticalBarTypeName = request.VerticalBarTypeName,
            VerticalSpacingMeters = request.VerticalSpacingMeters,
            VerticalHookTypeName = request.VerticalHookTypeName,
            StirrupBarTypeName = request.StirrupBarTypeName,
            StirrupShapeName = request.StirrupShapeName,
            StirrupHookTypeName = request.StirrupHookTypeName,
            StirrupSpacingMeters = request.StirrupSpacingMeters,
            UseElementCover = request.UseElementCover,
            CoverMeters = request.CoverMeters
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

    public static CreateCapitalRebarRequest Load()
    {
        if (_sessionSettings != null) return _sessionSettings;

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<CreateCapitalRebarRequest>(json);
                if (loaded != null)
                {
                    _sessionSettings = loaded;
                    return loaded;
                }
            }
        }
        catch { }

        var defaultSettings = new CreateCapitalRebarRequest
        {
            VerticalBarTypeName = string.Empty,
            VerticalSpacingMeters = 0.20,
            VerticalHookTypeName = string.Empty,
            StirrupBarTypeName = string.Empty,
            StirrupShapeName = string.Empty,
            StirrupHookTypeName = string.Empty,
            StirrupSpacingMeters = 0.15,
            UseElementCover = true,
            CoverMeters = 0.04
        };

        _sessionSettings = defaultSettings;
        return defaultSettings;
    }
}
