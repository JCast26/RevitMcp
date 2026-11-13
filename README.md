# Revit MCP Integration (.NET / C#)

Integración completa y desacoplada entre **Autodesk Revit** y modelos de lenguaje (LLM) a través del estándar abierto **Model Context Protocol (MCP)**.

---

## 🏛️ Arquitectura del Sistema

La arquitectura está dividida en dos componentes principales para superar la restricción de que Revit no posee un flujo `stdio` nativo:

```
┌─────────────────┐           stdio            ┌─────────────────────────────────────────┐
│                 │ (JSON-RPC 2.0 / MCP stdio) │        MCP Server Console App           │
│   LLM Client    │ ◄────────────────────────► │             (RevitMcp.Server)           │
│ (Claude/Cursor) │                            │  - Maneja tools/list, initialize        │
└─────────────────┘                            └───────────────────┬─────────────────────┘
                                                                   │
                                                      IPC Windows  │
                                                      Named Pipes  │ "RevitMcp_Ipc_Pipe"
                                                                   │
                                                                   ▼
┌──────────────────────────────────────────────────────────────────┴─────────────────────┐
│                              Autodesk Revit Add-in Process                             │
│                                                                                        │
│   ┌────────────────────────────────┐         ┌─────────────────────────────────────┐   │
│   │      NamedPipeIpcServer        │         │      RevitExternalEventHandler      │   │
│   │  (Background listener thread)  │ ──────► │      (IExternalEventHandler)        │   │
│   │   - Deserializa IpcRequest     │ Enqueue │   - ExternalEvent.Raise()           │   │
│   │   - Espera TaskCompletionSource│         │   - Ejecución segura en Main Thread │   │
│   └────────────────────────────────┘         └──────────────────┬──────────────────┘   │
│                                                                 │                      │
│                                          ┌──────────────────────┼──────────────────┐   │
│                                          ▼                      ▼                  ▼   │
│                                  AuditParameters        CreateModelElement   ManageFamily│
│                                  (Lectura/Filtro)       (Muros/Familias)     (Inyección) │
│                                          │                      │                  │   │
│                                          └──────────────────────┼──────────────────┘   │
│                                                                 ▼                      │
│                                                      Autodesk Revit DB Model           │
│                                                      (Unidades internas: Decimal Feet) │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 📏 Regla Estricta de Conversión de Unidades (Metros)

Revit almacena todas las dimensiones lineales internamente en **Pies Decimales (Decimal Feet)** ($1\text{ ft} = 0.3048\text{ m}$).

Esta solución implementa una capa bidireccional estricta en `RevitMcp.Core.Units.UnitConverter`:
- **Entradas desde el LLM:** Todas las coordenadas (`x, y, z`), longitudes y alturas se reciben obligatoriamente en **Metros** y se convierten a pies antes de ejecutar transacciones (`meters * 3.2808398950131233` o `meters / 0.3048`).
- **Salidas hacia el LLM:** Todos los valores extraídos de tipo longitud, área y volumen se devuelven en **Metros**, $\text{m}^2$ y $\text{m}^3$ respectivamente.

---

## 🛠️ Herramientas Expuestas (MCP Tools)

### 1. `AuditParameters`
Filtra elementos del modelo por categoría y extrae los parámetros solicitados. Busca tanto parámetros de ejemplar (instancia) como de tipo.
- **Input:**
  ```json
  {
    "categoryName": "Doors",
    "parameterNames": ["Height", "Width", "Comments", "Mark", "Fire Rating"]
  }
  ```
- **Output:**
  ```json
  {
    "category": "Doors",
    "count": 1,
    "elements": [
      {
        "elementId": 451234,
        "name": "Single-Flush: 0915 x 2134mm",
        "category": "Doors",
        "parameters": {
          "Height": 2.134,
          "Width": 0.915,
          "Comments": "Puerta cortafuego 60 min",
          "Mark": "D-101"
        }
      }
    ]
  }
  ```

### 2. `CreateModelElement`
Crea elementos en el modelo activo dentro de una `Transaction` segura en el hilo principal de Revit.
- **Para Muros Lineales:**
  ```json
  {
    "elementType": "Wall",
    "levelName": "Level 1",
    "startPoint": { "x": 0.0, "y": 0.0, "z": 0.0 },
    "endPoint": { "x": 6.5, "y": 0.0, "z": 0.0 },
    "height": 3.20
  }
  ```
- **Para Instancias de Familia (ej. Columnas, Mobiliario):**
  ```json
  {
    "elementType": "FamilyInstance",
    "levelName": "Level 1",
    "location": { "x": 3.0, "y": 2.5, "z": 0.0 },
    "familyName": "M_Concrete-Round-Column",
    "typeName": "450mm"
  }
  ```

### 3. `ManageFamilyCreation`
Crea una nueva familia desde una plantilla `.rft`, le inyecta parámetros compartidos (Shared Parameters) mediante `FamilyManager`, guarda el `.rfa` temporalmente y lo carga en el proyecto activo con `IFamilyLoadOptions`.
- **Input:**
  ```json
  {
    "familyName": "M_MesaConSensores",
    "templatePath": "Metric Generic Model.rft",
    "sharedParameters": [
      {
        "name": "COBie_Asset_Identifier",
        "group": "Data",
        "isInstance": true
      },
      {
        "name": "BIM_Mantenimiento_Anual",
        "group": "Identity Data",
        "isInstance": false
      }
    ]
  }
  ```

### 3.1. `InspectFamily`
Inspecciona el documento de familia (`.rfa`) abierto actualmente en el Editor de Familias (`doc.IsFamilyDocument == true`). Devuelve los parámetros del `FamilyManager`, tipos de familia, formas 3D (`Extrusion`, `Blend`, `Revolution`, `Sweep`), sus perfiles y *bounding boxes* en **metros**, planos de referencia, cotas, familias anidadas y los `elementId` seleccionados en pantalla.

### 3.2. `EditFamilyGeometry`
Modifica geométricamente o paramétricamente elementos dentro de la familia (`.rfa`) activa en el Editor de Familias. Todas las longitudes/coordenadas se envían en **metros** y los ángulos en **grados**.
- **Operaciones soportadas:**
  - `Move`: traslada elementos (`translationMeters`).
  - `Rotate`: rota elementos alrededor de un eje (`axisOriginMeters`, `axisDirection`, `angleDegrees`).
  - `Mirror`: refleja o copia elementos respecto a un plano (`planeOriginMeters`, `planeNormal`, `copy`).
  - `SetExtrusionBounds`: ajusta `startOffsetMeters`, `endOffsetMeters`, ángulos de revolución o sólido/vacío (`isSolid`).
  - `ScaleProfile`: escala el croquis 2D en el plano de trabajo (`scaleX`, `scaleY` o `scaleFactor`, `pivotMeters`).
  - `OffsetProfile`: desfasa el perfil 2D hacia afuera/adentro (`offsetMeters`).
  - `ReplaceExtrusionProfile`: reemplaza el perfil 2D mediante `profileLoops`, `rectangle` o `circle` preservando el `ElementId` vía `SketchEditScope`.
  - `CreateExtrusion`: crea una nueva extrusión sólida o vacía (`isSolid`) en la familia.
  - `SetFamilyParameter`: actualiza parámetros del `FamilyManager` (incluyendo fórmulas) o parámetros directos del elemento.
  - `DeleteElements`: elimina elementos de la familia.

### 4. `CreateBeamStirrups` (Módulo Acero de Refuerzo - BIM Suite)
Genera conjuntos de armadura (estribos) para vigas de concreto armado siguiendo el criterio sismorresistente estricto en 3 conjuntos:
- **Conjunto 1 (Inicio):** Regla `NumberWithSpacing` ($N_1 \text{ @ } S_1$), separado $S_0$ (típicamente 5 cm) de la cara interior del apoyo.
- **Conjunto 2 (Centro / Resto):** Regla `MaximumSpacing` ($S_2$, típicamente 20 cm) cubriendo la luz libre remanente sin duplicar barras.
- **Conjunto 3 (Fin):** Regla `NumberWithSpacing` ($N_3 \text{ @ } S_3$), separado $S_0$ de la cara interior del apoyo opuesto.
- **Visualización 3D:** Aplica automáticamente `SetUnobscuredInView` en vistas 3D.
- **Input:**
  ```json
  {
    "beamElementIds": [458120, 458125],
    "rebarBarTypeName": "3/8\"",
    "rebarShapeName": "T1",
    "firstStirrupOffsetMeters": 0.05,
    "zone1Count": 6,
    "zone1SpacingMeters": 0.10,
    "zone2MaxSpacingMeters": 0.20,
    "isSymmetric": true
  }
  ```

### 5. `ToggleRebarFilter` / Filtro Acero (Módulo Acero de Refuerzo - BIM Suite)
Botón de alternancia rápida (On/Off) que genera y aplica filtros automáticos por tipo de armadura exclusivamente para la categoría **Acero de Refuerzo** (`BuiltInCategory.OST_Rebar`) en la vista activa de Revit:
- **Sobrescritura Gráfica Completa:** Aplica modificaciones tanto para **Corte** como para **Proyección / Superficie** (color de líneas y trama sólida frontal).
- **Paleta Cromática Estándar:** Asigna colores según el diámetro nominal físico de la varilla (6mm Cyan, 8mm Teal, 3/8" Verde, 1/2" Amarillo, 5/8" Naranja, 3/4" Azul Rey, 7/8" Fucsia, 1" Rojo Carmesí, etc.).
- **Modo Toggle:** Si los filtros ya están aplicados en la vista activa, los desactiva inmediatamente restaurando la visualización nativa.

### 6. `CreateWallRebar` / Acero en Muros (Módulo Acero de Refuerzo - BIM Suite)
Herramienta de modelado y configuración automatizada de mallas de armadura para muros estructurales de concreto armado:
- **Mallas Simples o Dobles:** Permite generar 1 malla central (muros delgados) o 2 mallas (doble cortina exterior e interior respetando recubrimiento de concreto).
- **Tipologías Estructurales Soportadas:**
  - **Malla Continua Estándar:** Disposición uniforme ortogonal de refuerzo vertical y horizontal a lo largo y alto del muro.
  - **Malla con Remate de Ganchos (U-Hooks):** Barras horizontales con ganchos sísmicos de confinamiento en extremos cerrando el perímetro.
  - **Confinamiento de Borde (Boundary Elements / Cabezales Sísmicos):** Estribos cerrados de borde en extremos y malla distribuida en el alma central.
  - **Grapas Transversales (Crossties):** Opción de trabadores transversales vinculando ambas mallas al tresbolillo.
- **Interfaz Gráfica Integrada:** Ventana WPF interactiva desacoplada del hilo de Revit, persistencia de parámetros en `AppData\RevitMcp\WallRebarSettings.json` e ícono nativo con la estética gráfica de BIM Suite.
- **Input MCP (en metros):**
  ```json
  {
    "wallElementIds": [542100],
    "numberOfMeshes": 2,
    "typology": "StandardMesh",
    "verticalBarTypeName": "1/2\"",
    "verticalSpacingMeters": 0.20,
    "horizontalBarTypeName": "3/8\"",
    "horizontalSpacingMeters": 0.20,
    "useWallCover": true,
    "coverMeters": 0.025,
    "horizontalBarsOutside": true
  }
  ```

---

## 🚀 Compilación y Despliegue

### Requisitos
- .NET 8.0 SDK (o superior)
- Autodesk Revit 2025 (o 2024 adaptando el `<TargetFramework>`)

### 1. Compilar la Solución
```powershell
dotnet build RevitMcp.slnx -c Release
```

### 2. Instalar el Add-in en Revit
Copia el archivo `RevitMcp.addin` a la carpeta de Add-ins de Revit:
- **Ruta:** `%APPDATA%\Autodesk\Revit\Addins\2025\`
- Asegúrate de que el tag `<Assembly>` apunte a la ruta absoluta de `RevitMcp.Addin.dll` o copia las DLLs compiladas (`RevitMcp.Addin.dll` y `RevitMcp.Core.dll`) en ese mismo directorio.

Al iniciar Revit:
1. El Add-in arrancará el listener de Named Pipes en segundo plano (`RevitMcp_Ipc_Pipe`).
2. Se integrarán automáticamente los botones en la pestaña personalizada **"BIM Suite"**:
   - Panel **"Integración MCP"**: Botón **"Conexión MCP"** (diagnóstico de conexión).
   - Panel **"Acero de Refuerzo"**:
     - Botón **"Estribos en Vigas"** (herramienta interactiva con ventana WPF para armado sismorresistente).
     - Botón **"Filtro Acero"** (alternancia On/Off para activar filtros automáticos por tipo de armadura en corte y proyección según estándar cromático).
     - Botón **"Acero en Muros"** (herramienta interactiva con ventana WPF para modelar armaduras en muros con 1 o 2 mallas y diversas tipologías estructurales).

### 3. Configuración del Cliente MCP (ej. Claude Desktop / Cursor)
Agrega el servidor MCP a tu archivo `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "revit": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\Users\\jjcastillob\\Documents\\Antigravity_Revit\\src\\RevitMcp.Server\\RevitMcp.Server.csproj",
        "--no-build"
      ]
    }
  }
}
```
*(O apunta directamente al ejecutable publicado `RevitMcp.Server.exe`).*

---

## 🔒 Seguridad de Hilos de la API de Revit

Cualquier llamada fuera del hilo principal de Revit arroja la excepción `InvalidOperationException`. 
Este diseño garantiza que:
1. El servidor Named Pipe recibe la solicitud de forma 100% asíncrona.
2. La solicitud se encapsula en un `TaskCompletionSource<IpcResponse>` y se encola en `RevitExternalEventHandler`.
3. Se invoca `ExternalEvent.Raise()`, notificando al ciclo de eventos de Revit.
4. Revit ejecuta el método `Execute(UIApplication)` en su hilo principal nativo, abriendo las transacciones y realizando las lecturas sin bloqueos ni excepciones de concurrencia.
5. El resultado se devuelve de inmediato al cliente MCP y al LLM.
