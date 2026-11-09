using Autodesk.Revit.DB;

namespace RevitMcp.Addin.Tools;

/// <summary>
/// Resuelve nombres de categorías provistos por el LLM a BuiltInCategory de Revit.
/// </summary>
public static class CategoryResolver
{
    private static readonly Dictionary<string, BuiltInCategory> CommonCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        { "doors", BuiltInCategory.OST_Doors },
        { "door", BuiltInCategory.OST_Doors },
        { "puertas", BuiltInCategory.OST_Doors },
        { "walls", BuiltInCategory.OST_Walls },
        { "wall", BuiltInCategory.OST_Walls },
        { "muros", BuiltInCategory.OST_Walls },
        { "paredes", BuiltInCategory.OST_Walls },
        { "windows", BuiltInCategory.OST_Windows },
        { "window", BuiltInCategory.OST_Windows },
        { "ventanas", BuiltInCategory.OST_Windows },
        { "floors", BuiltInCategory.OST_Floors },
        { "floor", BuiltInCategory.OST_Floors },
        { "suelos", BuiltInCategory.OST_Floors },
        { "pisos", BuiltInCategory.OST_Floors },
        { "columns", BuiltInCategory.OST_Columns },
        { "column", BuiltInCategory.OST_Columns },
        { "columnas", BuiltInCategory.OST_Columns },
        { "structural columns", BuiltInCategory.OST_StructuralColumns },
        { "structural framing", BuiltInCategory.OST_StructuralFraming },
        { "beams", BuiltInCategory.OST_StructuralFraming },
        { "vigas", BuiltInCategory.OST_StructuralFraming },
        { "rooms", BuiltInCategory.OST_Rooms },
        { "room", BuiltInCategory.OST_Rooms },
        { "habitaciones", BuiltInCategory.OST_Rooms },
        { "furniture", BuiltInCategory.OST_Furniture },
        { "mobiliario", BuiltInCategory.OST_Furniture },
        { "muebles", BuiltInCategory.OST_Furniture },
        { "generic models", BuiltInCategory.OST_GenericModel },
        { "generic model", BuiltInCategory.OST_GenericModel },
        { "modelos genericos", BuiltInCategory.OST_GenericModel },
        { "ceilings", BuiltInCategory.OST_Ceilings },
        { "roofs", BuiltInCategory.OST_Roofs },
        { "stairs", BuiltInCategory.OST_Stairs },
        { "railings", BuiltInCategory.OST_StairsRailing }
    };

    /// <summary>
    /// Resuelve el string de categoría a un BuiltInCategory.
    /// Soporta nombres comunes (ej. "Doors"), nombres de enum (ej. "OST_Doors" o "Doors"),
    /// y categorías del documento.
    /// </summary>
    public static bool TryResolve(Document doc, string categoryName, out BuiltInCategory builtInCategory)
    {
        var cleanName = categoryName.Trim();

        // 1. Diccionario de nombres comunes
        if (CommonCategories.TryGetValue(cleanName, out builtInCategory))
            return true;

        // 2. Coincidencia exacta con enum BuiltInCategory
        if (Enum.TryParse(cleanName, true, out builtInCategory))
            return true;

        // 3. Coincidencia con prefijo OST_
        if (!cleanName.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse($"OST_{cleanName}", true, out builtInCategory))
                return true;
        }

        // 4. Búsqueda en Categories del documento activo
        foreach (Category cat in doc.Settings.Categories)
        {
            if (cat.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
            {
                builtInCategory = (BuiltInCategory)cat.Id.Value;
                return true;
            }
        }

        builtInCategory = BuiltInCategory.INVALID;
        return false;
    }
}
