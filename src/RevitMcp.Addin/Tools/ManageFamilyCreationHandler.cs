using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Models;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Opciones de carga de familias en el proyecto activo, sobreescribiendo si ya existe.
/// </summary>
public class FamilyLoadOptionsHandler : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        overwriteParameterValues = true;
        return true;
    }

    public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
    {
        source = FamilySource.Family;
        overwriteParameterValues = true;
        return true;
    }
}

/// <summary>
/// Manejador de la herramienta ManageFamilyCreation:
/// Crea un nuevo documento de familia a partir de una plantilla .rft,
/// inyecta parámetros compartidos a través de FamilyManager, guarda un .rfa temporal
/// y carga la familia resultante dentro del proyecto activo.
/// </summary>
public static class ManageFamilyCreationHandler
{
    public static ManageFamilyCreationResponse Execute(UIApplication uiapp, ManageFamilyCreationRequest request)
    {
        var app = uiapp.Application;
        var activeDoc = uiapp.ActiveUIDocument?.Document;

        if (activeDoc == null)
            throw new InvalidOperationException("No hay ningún proyecto activo en Revit para cargar la familia.");

        if (string.IsNullOrWhiteSpace(request.FamilyName))
            throw new ArgumentException("El nombre de la familia es obligatorio.", nameof(request.FamilyName));

        // 1. Validar la ruta de la plantilla .rft
        string templatePath = ResolveTemplatePath(request.TemplatePath);
        if (!File.Exists(templatePath))
            throw new FileNotFoundException($"No se encontró la plantilla de familia .rft en la ruta: '{templatePath}'");

        // 2. Crear el documento de familia desde la plantilla
        Document famDoc = app.NewFamilyDocument(templatePath);
        if (famDoc == null)
            throw new InvalidOperationException($"No se pudo crear el documento de familia desde '{templatePath}'.");

        try
        {
            var famManager = famDoc.FamilyManager;

            // 3. Inyectar Parámetros Compartidos dentro de una transacción en famDoc
            using (var trans = new Transaction(famDoc, "MCP: Inyectar Parámetros Compartidos"))
            {
                trans.Start();

                var sharedParamFile = app.OpenSharedParameterFile();

                foreach (var sp in request.SharedParameters)
                {
                    bool injected = false;

                    if (sharedParamFile != null)
                    {
                        // Buscar la definición en los grupos del archivo de parámetros compartidos
                        var def = sharedParamFile.Groups
                            .SelectMany(g => g.Definitions.Cast<Definition>())
                            .FirstOrDefault(d => d.Name.Equals(sp.Name, StringComparison.OrdinalIgnoreCase)) as ExternalDefinition;

                        if (def != null)
                        {
                            famManager.AddParameter(def, GroupTypeId.Data, sp.IsInstance);
                            injected = true;
                        }
                    }

                    // Fallback: Si no está en el archivo compartido, agregar como parámetro de familia nativo
                    if (!injected)
                    {
                        famManager.AddParameter(sp.Name, GroupTypeId.Data, SpecTypeId.String.Text, sp.IsInstance);
                    }
                }

                trans.Commit();
            }

            // 4. Guardar archivo temporal de la familia (.rfa)
            string tempDir = Path.Combine(Path.GetTempPath(), "RevitMcp_Families");
            Directory.CreateDirectory(tempDir);
            string tempFamilyPath = Path.Combine(tempDir, $"{request.FamilyName}.rfa");

            var saveOptions = new SaveAsOptions { OverwriteExistingFile = true };
            famDoc.SaveAs(tempFamilyPath, saveOptions);

            // 5. Cargar la familia en el proyecto activo
            Family? loadedFamily = null;
            using (var projectTrans = new Transaction(activeDoc, $"MCP: Cargar Familia {request.FamilyName}"))
            {
                projectTrans.Start();

                // Intentar carga directa documento a documento
                try
                {
                    loadedFamily = famDoc.LoadFamily(activeDoc, new FamilyLoadOptionsHandler());
                }
                catch
                {
                    // Alternativa: cargar desde el archivo guardado en disco
                    activeDoc.LoadFamily(tempFamilyPath, new FamilyLoadOptionsHandler(), out loadedFamily);
                }

                projectTrans.Commit();
            }

            return new ManageFamilyCreationResponse
            {
                Success = true,
                Status = "Loaded",
                FamilyName = request.FamilyName,
                FamilyId = loadedFamily?.Id?.Value,
                Message = $"Familia '{request.FamilyName}' creada con éxito con {request.SharedParameters.Count} parámetros y cargada en el proyecto."
            };
        }
        finally
        {
            // Cerrar el documento de familia sin guardar cambios adicionales
            if (famDoc != null && famDoc.IsValidObject)
            {
                famDoc.Close(false);
            }
        }
    }

    private static string ResolveTemplatePath(string inputPath)
    {
        if (File.Exists(inputPath))
            return inputPath;

        // Búsqueda en rutas estándar de plantillas de Revit
        string[] searchDirs =
        {
            @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\English",
            @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\Spanish",
            @"C:\ProgramData\Autodesk\RVT 2024\Family Templates\English",
            @"C:\ProgramData\Autodesk\RVT 2024\Family Templates\Spanish"
        };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;

            var fullPath = Path.Combine(dir, inputPath);
            if (File.Exists(fullPath)) return fullPath;

            // Búsqueda aproximada si sólo pasaron el nombre del archivo
            var fileName = Path.GetFileName(inputPath);
            var match = Directory.GetFiles(dir, $"*{fileName}*", SearchOption.AllDirectories).FirstOrDefault();
            if (match != null) return match;
        }

        return inputPath;
    }
}
