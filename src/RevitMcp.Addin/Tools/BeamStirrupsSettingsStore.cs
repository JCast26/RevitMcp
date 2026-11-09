using System;
using System.IO;
using System.Text.Json;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Almacena y recupera la última configuración empleada para el modelado de estribos,
/// tanto en la memoria de la sesión de Revit como en un archivo JSON en AppData para persistencia entre sesiones.
/// </summary>
public static class BeamStirrupsSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RevitMcp");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "BeamStirrupsSettings.json");

    private static CreateBeamStirrupsRequest? _sessionSettings;

    /// <summary>
    /// Guarda la configuración actual tanto en memoria estática como en el archivo JSON.
    /// </summary>
    public static void Save(CreateBeamStirrupsRequest request)
    {
        if (request == null) return;

        // Clonar parámetros de configuración excluyendo la lista de IDs de vigas particulares
        _sessionSettings = new CreateBeamStirrupsRequest
        {
            RebarBarTypeName = request.RebarBarTypeName,
            RebarShapeName = request.RebarShapeName,
            FirstStirrupOffsetMeters = request.FirstStirrupOffsetMeters,
            Zone1Count = request.Zone1Count,
            Zone1SpacingMeters = request.Zone1SpacingMeters,
            Zone2MaxSpacingMeters = request.Zone2MaxSpacingMeters,
            IsSymmetric = request.IsSymmetric,
            Zone3Count = request.Zone3Count,
            Zone3SpacingMeters = request.Zone3SpacingMeters,
            IncludeLongitudinalBars = request.IncludeLongitudinalBars,
            LongitudinalBarTypeName = request.LongitudinalBarTypeName
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
            // Silencioso si no hay permisos de disco
        }
    }

    /// <summary>
    /// Carga la última configuración utilizada. Prioriza la sesión en memoria y luego el archivo en disco.
    /// </summary>
    public static CreateBeamStirrupsRequest? Load()
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
                _sessionSettings = JsonSerializer.Deserialize<CreateBeamStirrupsRequest>(json);
                return _sessionSettings;
            }
        }
        catch
        {
            // Ignorar errores de lectura/deserialización
        }

        return null;
    }
}
