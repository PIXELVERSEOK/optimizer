````
# EVO FPS OPTIMIZER: auditoría, arquitectura y primer núcleo verificable

## 0. Estado real

En esta conversación no tengo acceso a tu repositorio, a Windows ni a un compilador. Por eso no puedo auditar el repo, compilar, ejecutar pruebas ni medir en el equipo de referencia. Afirmar lo contrario violaría tu requisito de "no inventes resultados".

| Elemento | Estado |
|---|---|
| Auditoría del repositorio | **Pendiente.** Script de solo lectura en la sección 1, para que me pegues la salida. |
| Arquitectura y plan | Diseñados (secciones 2 y 3). |
| Núcleo de seguridad, análisis y reglas (sección 4) | **Escrito, no compilado ni ejecutado por mí.** Hasta que `dotnet test` pase en tu máquina, no lo considero operativo. |
| Telemetría real, UI, ETW/PresentMon, Game Booster, Benchmark Lab, etc. | **No implementados todavía.** |

Empiezo por el núcleo y no por la interfaz porque de él depende que todo lo demás sea seguro y demostrable:

- **Restore Center:** ninguna optimización debe poder aplicarse sin registro previo y reversión verificada.
- **Estadísticas de frametime:** sin ellas no se puede demostrar ninguna mejora.
- **Motor de reglas:** debe separar correlación de causalidad.

---

## 1. Auditoría (solo lectura)

Ejecuta esto en PowerShell, en la raíz del repo, y pega la salida:

```powershell
dotnet --list-sdks; dotnet --list-runtimes
git status -sb; git log --oneline -5
Get-ChildItem -Recurse -Depth 4 -Include *.sln,*.csproj,*.props,global.json,*.md | Select FullName
Get-CimInstance Win32_OperatingSystem | Select Caption,BuildNumber
Get-CimInstance Win32_Processor | Select Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed
Get-CimInstance Win32_PhysicalMemory | Select BankLabel,Capacity,Speed,ConfiguredClockSpeed
Get-CimInstance Win32_VideoController | Select Name,DriverVersion,DriverDate
powercfg /getactivescheme
Get-Command PresentMon* -ErrorAction SilentlyContinue
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

Si el repo está vacío, el siguiente apartado es el punto de partida. Si ya hay código, adaptaré la estructura sin duplicar ni borrar nada.

---

## 2. Arquitectura y decisiones

```
Evo.FpsOptimizer/
├─ src/
│  ├─ Evo.Core/            (net10.0, sin dependencias de plataforma)
│  │   ├─ Common/          lecturas de sensores, origen del dato, modo ejecución
│  │   ├─ Registry/        abstracción + allow-list
│  │   ├─ Restore/         journal + RestoreCenter
│  │   ├─ Optimizations/   IOptimization + implementaciones
│  │   ├─ Analysis/        FrameStats, parser PresentMon
│  │   └─ Diagnostics/     motor de reglas
│  ├─ Evo.Windows/         (net10.0-windows) registro real, WMI, contadores, servicios
│  ├─ Evo.Telemetry/       (fase 2) PerfCounters, LibreHardwareMonitor, ETW
│  ├─ Evo.Data/            (fase 2) SQLite: sesiones, perfiles, historial
│  └─ Evo.App/             (fase 3) WPF + MVVM
└─ tests/Evo.Core.Tests/
```

**Decisiones y riesgos a validar contigo:**

1. **.NET 10 LTS.** Compruébalo con `dotnet --list-sdks`. Si no está instalado, se usa .NET 8 LTS cambiando solo el `TargetFramework`.
2. **WPF en vez de WinUI 3.** WPF tiene menos fricción de empaquetado, no exige MSIX y consume poco en reposo. Es una decisión de ingeniería, no un requisito técnico.
3. **Journal en JSON con escritura atómica.** Se escribe antes de cada cambio (write-ahead) para sobrevivir a un cierre brusco. SQLite se usa para historial y perfiles (fase 2), no para el registro de seguridad.
4. **PresentMon:**
   - Es una herramienta externa de Intel (licencia MIT, a verificar en la versión que se redistribuya).
   - Lee ETW, no inyecta ni dibuja overlay, así que no toca el proceso del juego.
   - Requiere ejecutarse como administrador o con el usuario en el grupo *Performance Log Users*.
   - Los nombres de columnas del CSV cambian entre 1.x y 2.x. El parser busca por nombre y falla de forma explícita si no reconoce ninguno.
5. **LibreHardwareMonitor (MPL-2.0).**
   - Para algunos sensores carga un driver de kernel.
   - **Verifica qué driver usa la versión elegida.** WinRing0 está catalogado como vulnerable y puede ser bloqueado por Defender o anticheats.
   - Mi propuesta es leer esos sensores solo bajo demanda, nunca durante una partida con anticheat, y degradar a "sensor no disponible" si falla.
6. **Intel UHD (equipo de referencia).** La utilización de GPU se obtendrá de los contadores `GPU Engine` y `GPU Adapter Memory` de Windows. Temperatura y consumo de la iGPU probablemente no se expondrán por separado de los del paquete de CPU. La UI mostrará "no disponible" en ese caso, no 0.
7. **Core i3-1220P (híbrido P+E).** No se fijará afinidad por defecto. El planificador de Windows 11 gestiona P/E-cores, y una afinidad manual puede empeorar los 1% Low. Será una opción "Avanzada" y reversible.
8. **Los límites de potencia (PL1/PL2) se leen, no se modifican.** Los fija el firmware.
9. **CS2 y VAC.** No habrá inyección ni overlay, y la monitorización será externa. Cambiar prioridad exige abrir el proceso con permisos de escritura. No puedo garantizar cómo lo trata VAC. Será opt-in con aviso y desactivable.

---

## 3. Plan por fases

| Fase | Contenido | Criterio de "hecho" |
|---|---|---|
| 1 (este mensaje) | Core: Restore Center, FrameStats, parser PresentMon, reglas, 1ª optimización (Game Mode) | `dotnet test` en verde en tu máquina |
| 2 | Telemetría real (contadores, WMI, LHM), SQLite, Hardware Analyzer, Driver Analyzer | Datos verificados contra el Administrador de tareas |
| 3 | Integración PresentMon, FPS Analyzer, Frametime Lab, Benchmark Lab con control de condiciones | Dos capturas repetidas con varianza documentada |
| 4 | Windows Optimizer (inicio, servicios en solo lectura, plan de energía), Game Booster | Aplicar y revertir verificado en CS2 |
| 5 | UI WPF, Dashboard, Thermal/Latency Lab, informes | Consumo propio medido en reposo |

---

## 4. Fase 1: código

### Creación de la solución

```powershell
dotnet new sln -n Evo.FpsOptimizer
dotnet new classlib -n Evo.Core -o src/Evo.Core -f net10.0
dotnet new classlib -n Evo.Windows -o src/Evo.Windows -f net10.0
dotnet new xunit -n Evo.Core.Tests -o tests/Evo.Core.Tests -f net10.0
dotnet sln add src/Evo.Core src/Evo.Windows tests/Evo.Core.Tests
dotnet add src/Evo.Windows reference src/Evo.Core
dotnet add tests/Evo.Core.Tests reference src/Evo.Core
Remove-Item src/Evo.Core/Class1.cs, src/Evo.Windows/Class1.cs
```

**`src/Evo.Core/Evo.Core.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

**`src/Evo.Windows/Evo.Windows.csproj`**: cambia `TargetFramework` a `net10.0-windows` y añade `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` y la referencia a Core.

---

### `src/Evo.Core/Common/Common.cs`

```csharp
namespace Evo.Core;

/// <summary>Simulación: nunca toca Windows. Real: aplica cambios.</summary>
public enum ExecutionMode { Simulation, Real }

/// <summary>Procedencia del dato, para no mezclar medición, diagnóstico y simulación.</summary>
public enum DataOrigin { Measured, Diagnostic, Simulated }

public enum Availability { Available, NotSupported, RequiresAdmin, Error }

/// <summary>Un sensor no disponible NUNCA se representa como 0.</summary>
public sealed record SensorReading(
    string Name, double? Value, string Unit, Availability Status, DataOrigin Origin, string? Detail = null)
{
    public bool HasValue => Status == Availability.Available && Value.HasValue;

    public static SensorReading Ok(string name, double value, string unit, DataOrigin origin = DataOrigin.Measured)
        => new(name, value, unit, Availability.Available, origin);

    public static SensorReading Missing(string name, string unit, Availability why, string? detail = null)
        => why == Availability.Available
            ? throw new ArgumentException("Un sensor ausente no puede estar 'Available'.", nameof(why))
            : new(name, null, unit, why, DataOrigin.Measured, detail);
}
```

### `src/Evo.Core/Registry/RegistryAbstractions.cs`

```csharp
using System.Globalization;

namespace Evo.Core;

public enum RegValueKind { DWord, String }

public sealed record RegistryTarget(string Hive, string SubKey, string ValueName)
{
    public override string ToString() => $@"{Hive}\{SubKey}\{ValueName}";
}

public sealed record RegistryValueState(bool Exists, RegValueKind Kind, string? Data)
{
    public static RegistryValueState Absent { get; } = new(false, RegValueKind.DWord, null);
    public static RegistryValueState DWord(int v) =>
        new(true, RegValueKind.DWord, v.ToString(CultureInfo.InvariantCulture));
    public static RegistryValueState Str(string v) => new(true, RegValueKind.String, v);
}

public interface IRegistryAccessor
{
    RegistryValueState Read(RegistryTarget target);
    void Write(RegistryTarget target, RegistryValueState value);
    void Delete(RegistryTarget target);
}

/// <summary>
/// Lista cerrada de valores que EVO puede tocar. Solo HKCU (mínimo privilegio).
/// Cualquier otro destino se rechaza: no hay modificaciones masivas del registro.
/// </summary>
public sealed class RegistryAllowList
{
    private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);

    public RegistryAllowList(IEnumerable<RegistryTarget> targets)
    {
        foreach (var t in targets) _allowed.Add(t.ToString());
    }

    public bool IsAllowed(RegistryTarget t) =>
        t.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) && _allowed.Contains(t.ToString());
}
```

### `src/Evo.Core/Restore/Journal.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Evo.Core;

public enum ChangeStatus { Pending, Applied, Simulated, ApplyFailed, Reverted, RevertFailed }

public sealed record ChangeRecord(
    Guid Id, string OptimizationId, DateTimeOffset TimestampUtc,
    RegistryTarget Target, RegistryValueState Before, RegistryValueState Intended,
    ChangeStatus Status, string? Detail = null);

public interface IChangeJournal
{
    IReadOnlyList<ChangeRecord> Load();
    void Save(IReadOnlyList<ChangeRecord> records);
}

public sealed class JsonChangeJournal : IChangeJournal
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    public JsonChangeJournal(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    public IReadOnlyList<ChangeRecord> Load()
    {
        if (!File.Exists(_path)) return Array.Empty<ChangeRecord>();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<ChangeRecord>>(json, Options) ?? new();
        }
        catch (JsonException ex)
        {
            // Nunca descartar en silencio un journal corrupto: contiene los estados originales.
            throw new InvalidDataException($"Journal corrupto: {_path}. Conserva el archivo y su .bak.", ex);
        }
    }

    public void Save(IReadOnlyList<ChangeRecord> records)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(records, Options));
        if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak");
        else File.Move(tmp, _path);
    }
}
```

### `src/Evo.Core/Restore/RestoreCenter.cs`

```csharp
using System.Globalization;

namespace Evo.Core;

public enum RevertOutcome { Restored, AlreadyRestored, SkippedExternalChange, Failed, NotApplicable }
public sealed record RestoreResult(Guid ChangeId, RevertOutcome Outcome, string Message);

/// <summary>
/// Único punto por el que EVO modifica el registro. Garantiza:
/// 1) allow-list, 2) estado previo registrado ANTES de escribir (write-ahead),
/// 3) verificación por relectura, 4) reversión verificada y 5) no pisar cambios ajenos.
/// </summary>
public sealed class RestoreCenter
{
    private readonly IChangeJournal _journal;
    private readonly IRegistryAccessor _registry;
    private readonly RegistryAllowList _allow;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    public ExecutionMode Mode { get; }

    public RestoreCenter(IChangeJournal journal, IRegistryAccessor registry, RegistryAllowList allow,
                         ExecutionMode mode, TimeProvider? clock = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _allow = allow ?? throw new ArgumentNullException(nameof(allow));
        Mode = mode;
        _clock = clock ?? TimeProvider.System;
    }

    public IReadOnlyList<ChangeRecord> History() => _journal.Load();

    public ChangeRecord ApplyRegistryChange(string optimizationId, RegistryTarget target, RegistryValueState intended)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optimizationId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(intended);

        if (!_allow.IsAllowed(target))
            throw new InvalidOperationException($"Destino de registro no permitido: {target}");
        if (!intended.Exists || intended.Data is null)
            throw new ArgumentException("El valor deseado debe existir y tener datos.", nameof(intended));
        if (intended.Kind == RegValueKind.DWord &&
            !int.TryParse(intended.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("DWORD inválido.", nameof(intended));

        lock (_gate)
        {
            var records = _journal.Load().ToList();
            var before = _registry.Read(target);

            var rec = new ChangeRecord(Guid.NewGuid(), optimizationId, _clock.GetUtcNow(), target, before, intended,
                Mode == ExecutionMode.Simulation ? ChangeStatus.Simulated : ChangeStatus.Pending);
            records.Add(rec);
            _journal.Save(records);                       // write-ahead
            if (Mode == ExecutionMode.Simulation) return rec;

            try
            {
                _registry.Write(target, intended);
                var after = _registry.Read(target);
                if (after != intended)
                    throw new InvalidOperationException("La relectura no coincide con el valor escrito.");
                rec = rec with { Status = ChangeStatus.Applied };
            }
            catch (Exception ex)
            {
                var rolledBack = SafeRestore(target, before);
                rec = rec with
                {
                    Status = ChangeStatus.ApplyFailed,
                    Detail = $"{ex.Message} | rollback {(rolledBack ? "verificado" : "NO verificado")}"
                };
            }
            records[records.FindIndex(r => r.Id == rec.Id)] = rec;
            _journal.Save(records);
            return rec;
        }
    }

    public RestoreResult Revert(Guid id, bool force = false)
    {
        lock (_gate)
        {
            var records = _journal.Load().ToList();
            var idx = records.FindIndex(r => r.Id == id);
            return idx < 0
                ? new RestoreResult(id, RevertOutcome.NotApplicable, "Cambio no encontrado en el registro.")
                : RevertAt(records, idx, force);
        }
    }

    /// <summary>Restaura en orden cronológico inverso todo lo aplicado.</summary>
    public IReadOnlyList<RestoreResult> RevertAll(bool force = false)
    {
        lock (_gate)
        {
            var records = _journal.Load().ToList();
            var results = new List<RestoreResult>();
            for (var i = records.Count - 1; i >= 0; i--)
                if (records[i].Status is ChangeStatus.Applied or ChangeStatus.RevertFailed)
                    results.Add(RevertAt(records, i, force));
            return results;
        }
    }

    /// <summary>Resuelve registros 'Pending' que quedaron tras un cierre inesperado.</summary>
    public int RecoverPending()
    {
        lock (_gate)
        {
            var records = _journal.Load().ToList();
            var n = 0;
            for (var i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r.Status != ChangeStatus.Pending) continue;
                var cur = _registry.Read(r.Target);
                var untouched = cur == r.Before && cur != r.Intended;
                records[i] = r with
                {
                    Status = untouched ? ChangeStatus.ApplyFailed : ChangeStatus.Applied,
                    Detail = "Recuperado tras interrupción."
                };
                n++;
            }
            if (n > 0) _journal.Save(records);
            return n;
        }
    }

    private RestoreResult RevertAt(List<ChangeRecord> records, int idx, bool force)
    {
        var rec = records[idx];
        if (Mode == ExecutionMode.Simulation)
            return new(rec.Id, RevertOutcome.NotApplicable, "Modo simulación: no se restaura nada.");

        switch (rec.Status)
        {
            case ChangeStatus.Reverted:
                return new(rec.Id, RevertOutcome.AlreadyRestored, "Ya estaba restaurado.");
            case ChangeStatus.Simulated or ChangeStatus.ApplyFailed or ChangeStatus.Pending:
                return new(rec.Id, RevertOutcome.NotApplicable, $"Estado {rec.Status}: nada que revertir.");
        }

        RegistryValueState current;
        try { current = _registry.Read(rec.Target); }
        catch (Exception ex) { return new(rec.Id, RevertOutcome.Failed, $"No se pudo leer: {ex.Message}"); }

        if (current == rec.Before)
        {
            records[idx] = rec with { Status = ChangeStatus.Reverted, Detail = "El valor ya coincidía con el original." };
            _journal.Save(records);
            return new(rec.Id, RevertOutcome.AlreadyRestored, "El valor ya coincidía con el original.");
        }

        if (current != rec.Intended && !force)
            return new(rec.Id, RevertOutcome.SkippedExternalChange,
                "El valor fue modificado después de EVO (por el usuario, Windows u otro cambio posterior). " +
                "No se sobrescribe sin confirmación.");

        string? error = null;
        bool ok;
        try { ok = SafeRestore(rec.Target, rec.Before); }
        catch (Exception ex) { ok = false; error = ex.Message; }

        records[idx] = rec with { Status = ok ? ChangeStatus.Reverted : ChangeStatus.RevertFailed, Detail = error };
        _journal.Save(records);
        return ok
            ? new(rec.Id, RevertOutcome.Restored, "Restaurado y verificado por relectura.")
            : new(rec.Id, RevertOutcome.Failed, error ?? "La relectura no coincide con el estado original.");
    }

    /// <summary>Restaura y verifica por relectura. Devuelve false si no coincide.</summary>
    private bool SafeRestore(RegistryTarget target, RegistryValueState before)
    {
        try
        {
            if (before.Exists) _registry.Write(target, before); else _registry.Delete(target);
            return _registry.Read(target) == before;
        }
        catch { return false; }
    }
}
```

### `src/Evo.Core/Optimizations/Optimizations.cs`

```csharp
namespace Evo.Core;

public enum SafetyClass { Safe, Conditional, Advanced, NotRecommended }

/// <summary>Las cinco explicaciones obligatorias antes de cualquier cambio.</summary>
public sealed record OptimizationInfo(
    string Id, string Name, SafetyClass Class,
    string WhatItChanges, string Why, string ExpectedBenefit, string Risks, string HowToRestore);

public sealed record CompatibilityResult(bool Compatible, string Reason);

public interface IOptimization
{
    OptimizationInfo Info { get; }
    CompatibilityResult CheckCompatibility();
    /// <summary>Devuelve null si no hay nada que cambiar.</summary>
    ChangeRecord? Apply(RestoreCenter restore);
}

public sealed class GameModeOptimization : IOptimization
{
    public static readonly RegistryTarget Target =
        new("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled");

    private const int MinBuild = 15063; // Windows 10 1703, primera versión con Game Mode
    private readonly IRegistryAccessor _registry;
    private readonly int _osBuild;

    public GameModeOptimization(IRegistryAccessor registry, int osBuild)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _osBuild = osBuild;
    }

    public OptimizationInfo Info { get; } = new(
        "windows.gamemode", "Activar Game Mode", SafetyClass.Safe,
        @"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled = 1 (solo si estaba en 0).",
        "Game Mode pide a Windows priorizar el juego en primer plano y limitar actividad en segundo plano (p. ej. instalación de controladores).",
        "Pequeño y variable según el equipo. Debe medirse con Benchmark Lab; no se asume mejora.",
        "Muy bajo. Es una opción estándar de Windows y solo afecta al usuario actual.",
        "Restore Center escribe de vuelta el valor original y verifica por relectura.");

    public CompatibilityResult CheckCompatibility() =>
        _osBuild >= MinBuild
            ? new(true, "Compatible.")
            : new(false, $"Build {_osBuild} < {MinBuild}: Game Mode no existe.");

    public ChangeRecord? Apply(RestoreCenter restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        var compat = CheckCompatibility();
        if (!compat.Compatible) throw new InvalidOperationException(compat.Reason);

        var current = _registry.Read(Target);
        // Valor ausente = comportamiento por defecto de Windows (supuesto a verificar en el equipo
        // de referencia). Solo se interviene si el usuario o una política lo dejaron explícitamente en 0.
        if (!current.Exists || current != RegistryValueState.DWord(0)) return null;

        return restore.ApplyRegistryChange(Info.Id, Target, RegistryValueState.DWord(1));
    }
}

public static class DefaultAllowList
{
    public static RegistryAllowList Create() => new(new[] { GameModeOptimization.Target });
}
```

### `src/Evo.Core/Analysis/FrameStats.cs`

```csharp
namespace Evo.Core;

public sealed record StutterOptions(int HalfWindow = 30, double Factor = 2.0, double MinExcessMs = 4.0);
public sealed record StutterEvent(int FrameIndex, double TimeSeconds, double FrameTimeMs, double BaselineMs);

public sealed record FrameStatistics(
    int FrameCount, double DurationSeconds,
    double AvgFps, double MinFps, double MaxFps,
    double OnePercentLowFps,        // media de las peores 1 % de frames (método CapFrameX "average")
    double PointOnePercentLowFps,   // ídem 0,1 %
    double OnePercentLowP99Fps,     // 1000 / percentil 99 de frametime (método percentil)
    double PointOnePercentLowP999Fps,
    double MeanFrameTimeMs, double MedianFrameTimeMs, double StdDevMs,
    double MeanAbsDeltaMs,          // variación frame a frame: sensación de fluidez
    IReadOnlyList<StutterEvent> Stutters);

/// <summary>
/// Trabaja sobre frametimes en ms. FPS = frames/s renderizados; NO es la tasa del monitor.
/// Los dos métodos de "low" están rotulados: no son comparables entre sí.
/// </summary>
public static class FrameStats
{
    public static FrameStatistics Compute(IReadOnlyList<double> frameTimesMs, StutterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);
        options ??= new StutterOptions();
        if (options.HalfWindow < 1) throw new ArgumentOutOfRangeException(nameof(options));

        var ft = frameTimesMs.Where(x => double.IsFinite(x) && x > 0).ToArray();
        if (ft.Length < 2) throw new ArgumentException("Se necesitan al menos 2 frametimes válidos.", nameof(frameTimesMs));

        var n = ft.Length;
        var total = ft.Sum();
        var mean = total / n;
        var asc = (double[])ft.Clone();
        Array.Sort(asc);

        var variance = ft.Sum(x => (x - mean) * (x - mean)) / n;
        var absDelta = 0.0;
        for (var i = 1; i < n; i++) absDelta += Math.Abs(ft[i] - ft[i - 1]);

        return new FrameStatistics(
            n, total / 1000.0,
            1000.0 * n / total, 1000.0 / asc[^1], 1000.0 / asc[0],
            1000.0 / WorstAverage(asc, 0.01), 1000.0 / WorstAverage(asc, 0.001),
            1000.0 / Percentile(asc, 0.99), 1000.0 / Percentile(asc, 0.999),
            mean, MedianOfSorted(asc, 0, n), Math.Sqrt(variance), absDelta / (n - 1),
            DetectStutters(ft, options));
    }

    private static int CeilCount(int n, double fraction) =>
        Math.Max(1, (int)Math.Ceiling(n * fraction - 1e-9));

    private static double WorstAverage(double[] asc, double fraction)
    {
        var k = Math.Min(asc.Length, CeilCount(asc.Length, fraction));
        var sum = 0.0;
        for (var i = asc.Length - k; i < asc.Length; i++) sum += asc[i];
        return sum / k;
    }

    // Nearest-rank.
    private static double Percentile(double[] asc, double p) =>
        asc[Math.Min(asc.Length, CeilCount(asc.Length, p)) - 1];

    private static double MedianOfSorted(double[] a, int start, int len) =>
        len % 2 == 1 ? a[start + len / 2] : (a[start + len / 2 - 1] + a[start + len / 2]) / 2.0;

    /// <summary>Pico = frame &gt; Factor × mediana local Y exceso absoluto ≥ MinExcessMs.</summary>
    private static List<StutterEvent> DetectStutters(double[] ft, StutterOptions o)
    {
        var events = new List<StutterEvent>();
        var buf = new double[2 * o.HalfWindow + 1];
        var t = 0.0;
        for (var i = 0; i < ft.Length; i++)
        {
            var lo = Math.Max(0, i - o.HalfWindow);
            var hi = Math.Min(ft.Length - 1, i + o.HalfWindow);
            var len = hi - lo + 1;
            Array.Copy(ft, lo, buf, 0, len);
            Array.Sort(buf, 0, len);
            var baseline = MedianOfSorted(buf, 0, len);
            if (ft[i] > o.Factor * baseline && ft[i] - baseline >= o.MinExcessMs)
                events.Add(new StutterEvent(i, t / 1000.0, ft[i], baseline));
            t += ft[i];
        }
        return events;
    }
}
```

### `src/Evo.Core/Analysis/PresentMonCsvParser.cs`

```csharp
using System.Globalization;
using System.Text;

namespace Evo.Core;

public enum FrameMetric { MsBetweenPresents, DisplayedTime }

public sealed record PresentMonCapture(
    double[] FrameTimesMs, string ColumnUsed, int SkippedRows, int DroppedRows,
    IReadOnlyList<string> Applications, IReadOnlyList<string> Warnings);

/// <summary>
/// Lee el CSV de PresentMon por NOMBRE de columna (varía entre 1.x y 2.x). Si no reconoce
/// ninguna columna esperada falla con un error explícito y no adivina.
/// Nombres candidatos: verificar contra la versión de PresentMon instalada.
/// </summary>
public static class PresentMonCsvParser
{
    private static readonly string[] PresentCols = { "MsBetweenPresents", "FrameTime" };
    private static readonly string[] DisplayCols = { "MsBetweenDisplayChange", "DisplayedTime" };

    public static PresentMonCapture Parse(TextReader reader, FrameMetric metric, string? processFilter = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var headerLine = reader.ReadLine() ?? throw new FormatException("CSV vacío.");
        var headers = SplitCsv(headerLine);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++) map.TryAdd(headers[i].Trim(), i);

        var candidates = metric == FrameMetric.MsBetweenPresents ? PresentCols : DisplayCols;
        var column = candidates.FirstOrDefault(map.ContainsKey)
            ?? throw new FormatException(
                $"Ninguna columna reconocida ({string.Join("/", candidates)}). Encontradas: {string.Join(", ", headers)}");

        var valueIdx = map[column];
        var appIdx = map.TryGetValue("Application", out var a) ? a : -1;
        var dropIdx = map.TryGetValue("Dropped", out var d) ? d : -1;

        var values = new List<double>();
        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0, dropped = 0;

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitCsv(line);
            if (f.Count <= valueIdx) { skipped++; continue; }

            if (appIdx >= 0 && appIdx < f.Count)
            {
                var app = f[appIdx].Trim();
                if (processFilter is not null && !app.Equals(processFilter, StringComparison.OrdinalIgnoreCase)) continue;
                apps.Add(app);
            }
            if (dropIdx >= 0 && dropIdx < f.Count && f[dropIdx].Trim() == "1") dropped++;

            if (double.TryParse(f[valueIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && double.IsFinite(v) && v > 0)
                values.Add(v);
            else
                skipped++;
        }

        var warnings = new List<string>();
        if (processFilter is null && apps.Count > 1)
            warnings.Add($"El CSV contiene {apps.Count} aplicaciones; filtra por proceso para no mezclar frametimes.");
        if (skipped > 0)
            warnings.Add($"{skipped} filas omitidas (valor inválido, 0 o fila incompleta).");

        return new PresentMonCapture(values.ToArray(), column, skipped, dropped, apps.ToList(), warnings);
    }

    internal static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        return res;
    }
}
```

### `src/Evo.Core/Diagnostics/Rules.cs`

```csharp
using System.Globalization;

namespace Evo.Core;

public enum Confidence { Low, Medium, High }
public sealed record Evidence(string Metric, string Value);

/// <summary>Un hallazgo es una correlación observada con su advertencia; no una causa demostrada.</summary>
public sealed record Finding(
    string RuleId, string Title, string Summary, Confidence Confidence,
    IReadOnlyList<Evidence> Evidence, string Caveat, IReadOnlyList<string> SuggestedOptimizationIds);

/// <summary>Campos null = dato no disponible. Las reglas NO adivinan: sin datos devuelven null.</summary>
public sealed record SystemSnapshot
{
    public double? CpuTotalPct { get; init; }
    public double? GpuEngine3DPct { get; init; }
    public double? RamUsedPct { get; init; }
    public double? CpuTempC { get; init; }
    public double? CpuTjMaxC { get; init; }
    public double? CpuClockMHz { get; init; }
    public double? CpuMaxClockMHz { get; init; }
    public int? MemoryModuleCount { get; init; }
    public bool? GpuIsIntegrated { get; init; }
    public FrameStatistics? Frames { get; init; }
}

public interface IRule
{
    string Id { get; }
    Finding? Evaluate(SystemSnapshot s);
}

internal static class Fmt
{
    public static string Pct(double v) => v.ToString("F0", CultureInfo.InvariantCulture) + " %";
    public static string Num(double v, string unit) => v.ToString("F0", CultureInfo.InvariantCulture) + " " + unit;
}

public sealed class GpuBoundRule : IRule
{
    public string Id => "diag.gpu-bound";
    public Finding? Evaluate(SystemSnapshot s)
    {
        if (s.GpuEngine3DPct is not { } gpu || s.CpuTotalPct is not { } cpu) return null;
        if (gpu < 95 || cpu >= 85) return null;
        return new Finding(Id, "Patrón compatible con limitación de GPU",
            "El motor 3D de la GPU está casi saturado mientras la CPU tiene margen.",
            Confidence.Medium,
            new[] { new Evidence("GPU motor 3D", Fmt.Pct(gpu)), new Evidence("CPU total", Fmt.Pct(cpu)) },
            "Correlación, no prueba. Para confirmarlo, baja resolución o calidad y comprueba si los FPS suben.",
            Array.Empty<string>());
    }
}

public sealed class CpuBoundRule : IRule
{
    public string Id => "diag.cpu-bound";
    public Finding? Evaluate(SystemSnapshot s)
    {
        if (s.GpuEngine3DPct is not { } gpu || s.CpuTotalPct is not { } cpu) return null;
        if (cpu < 90 || gpu >= 80) return null;
        return new Finding(Id, "Patrón compatible con limitación de CPU",
            "La CPU está muy cargada y la GPU no está saturada.",
            Confidence.Medium,
            new[] { new Evidence("CPU total", Fmt.Pct(cpu)), new Evidence("GPU motor 3D", Fmt.Pct(gpu)) },
            "Un juego limitado por un solo hilo puede no verse en el uso total. Revisa el uso por núcleo/hilo antes de concluir.",
            Array.Empty<string>());
    }
}

public sealed class ThermalThrottlingRule : IRule
{
    public string Id => "diag.thermal-throttling";
    public Finding? Evaluate(SystemSnapshot s)
    {
        if (s.CpuTempC is not { } t || s.CpuTjMaxC is not { } tj ||
            s.CpuClockMHz is not { } clk || s.CpuMaxClockMHz is not { } max ||
            s.CpuTotalPct is not { } load) return null;
        if (load < 70 || t < tj - 5 || clk >= 0.8 * max) return null;
        return new Finding(Id, "Patrón compatible con throttling térmico",
            "Con carga alta, la temperatura está cerca del límite y la frecuencia queda por debajo del máximo observado.",
            Confidence.Medium,
            new[]
            {
                new Evidence("Temp CPU", Fmt.Num(t, "°C")), new Evidence("Tjmax", Fmt.Num(tj, "°C")),
                new Evidence("Frecuencia", Fmt.Num(clk, "MHz")), new Evidence("Máx. observada", Fmt.Num(max, "MHz"))
            },
            "La frecuencia también baja por límites de potencia o por ahorro de energía. Confirma con el indicador de límite del sensor si existe.",
            Array.Empty<string>());
    }
}

public sealed class SingleChannelMemoryRule : IRule
{
    public string Id => "diag.single-channel-igpu";
    public Finding? Evaluate(SystemSnapshot s)
    {
        if (s.GpuIsIntegrated != true || s.MemoryModuleCount != 1) return null;
        return new Finding(Id, "Un solo módulo de RAM con GPU integrada",
            "La iGPU comparte la memoria del sistema; con un único módulo el ancho de banda disponible es menor.",
            Confidence.Low,
            new[] { new Evidence("Módulos detectados", "1") },
            "El número de módulos es una aproximación del número de canales. Confírmalo con una herramienta de terceros.",
            Array.Empty<string>());
    }
}

public sealed class RuleEngine
{
    private readonly IReadOnlyList<IRule> _rules;
    private readonly Action<string, Exception>? _onError;

    public RuleEngine(IEnumerable<IRule> rules, Action<string, Exception>? onError = null)
    {
        _rules = rules.ToList();
        _onError = onError;
    }

    public static RuleEngine CreateDefault(Action<string, Exception>? onError = null) =>
        new(new IRule[] { new GpuBoundRule(), new CpuBoundRule(), new ThermalThrottlingRule(), new SingleChannelMemoryRule() }, onError);

    public IReadOnlyList<Finding> Evaluate(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var list = new List<Finding>();
        foreach (var r in _rules)
        {
            try { if (r.Evaluate(snapshot) is { } f) list.Add(f); }
            catch (Exception ex) { _onError?.Invoke(r.Id, ex); }   // una regla rota no tumba el diagnóstico
        }
        return list;
    }
}
```

### `src/Evo.Windows/WindowsRegistryAccessor.cs`

```csharp
using System.Globalization;
using System.Runtime.Versioning;
using Evo.Core;
using Microsoft.Win32;

namespace Evo.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryAccessor : IRegistryAccessor
{
    private static RegistryKey Root(string hive) =>
        hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            ? Registry.CurrentUser
            : throw new NotSupportedException($"Hive no soportado: {hive}");

    public RegistryValueState Read(RegistryTarget t)
    {
        using var key = Root(t.Hive).OpenSubKey(t.SubKey, writable: false);
        var value = key?.GetValue(t.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (key is null || value is null) return RegistryValueState.Absent;

        return key.GetValueKind(t.ValueName) switch
        {
            RegistryValueKind.DWord => RegistryValueState.DWord((int)value),
            RegistryValueKind.String => RegistryValueState.Str((string)value),
            var k => throw new NotSupportedException($"Tipo de valor no soportado: {k}")
        };
    }

    public void Write(RegistryTarget t, RegistryValueState v)
    {
        if (!v.Exists || v.Data is null) throw new ArgumentException("Valor inexistente.", nameof(v));
        using var key = Root(t.Hive).CreateSubKey(t.SubKey, writable: true);
        if (v.Kind == RegValueKind.DWord)
            key.SetValue(t.ValueName, int.Parse(v.Data, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        else
            key.SetValue(t.ValueName, v.Data, RegistryValueKind.String);
    }

    public void Delete(RegistryTarget t)
    {
        using var key = Root(t.Hive).OpenSubKey(t.SubKey, writable: true);
        key?.DeleteValue(t.ValueName, throwOnMissingValue: false);
    }
}
```

---

### Pruebas: `tests/Evo.Core.Tests/`

Borra `UnitTest1.cs`.

**`TestSupport.cs`**
```csharp
using Evo.Core;

namespace Evo.Core.Tests;

internal sealed class FakeRegistry : IRegistryAccessor
{
    public Dictionary<string, RegistryValueState> Values = new(StringComparer.OrdinalIgnoreCase);
    public int WriteCount;
    public bool FailWrites;     // lanza IOException
    public bool IgnoreWrites;   // escritura "silenciosamente" ineficaz

    public RegistryValueState Read(RegistryTarget t) =>
        Values.TryGetValue(t.ToString(), out var v) ? v : RegistryValueState.Absent;

    public void Write(RegistryTarget t, RegistryValueState v)
    {
        if (FailWrites) throw new IOException("fallo simulado");
        WriteCount++;
        if (!IgnoreWrites) Values[t.ToString()] = v;
    }

    public void Delete(RegistryTarget t) => Values.Remove(t.ToString());
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evo-" + Guid.NewGuid());
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best effort */ } }
}
```

**`RestoreCenterTests.cs`**
```csharp
using Evo.Core;
using Xunit;

namespace Evo.Core.Tests;

public class RestoreCenterTests
{
    private static readonly RegistryTarget T = new("HKCU", @"Software\EvoTest", "ValueA");
    private static RegistryAllowList Allow => new(new[] { T });

    private static RestoreCenter Make(FakeRegistry reg, string journalPath, ExecutionMode mode = ExecutionMode.Real) =>
        new(new JsonChangeJournal(journalPath), reg, Allow, mode);

    [Fact]
    public void Apply_then_revert_restores_existing_value()
    {
        using var tmp = new TempDir();
        var reg = new FakeRegistry();
        reg.Values[T.ToString()] = RegistryValueState.DWord(0);
        var rc = Make(reg, tmp.File("j.json"));

        var rec = rc.ApplyRegistryChange("opt", T, RegistryValueState.DWord(1));
        Assert.Equal(ChangeStatus.Applied, rec.Status);
        Assert.Equal(RegistryValueState.DWord(1), reg.Read(T));

        var res = rc.Revert(rec.Id);
        Assert.Equal(RevertOutcome.Restored, res.Outcome);
        Assert.Equal(RegistryValueState.DWord(0), reg.Read(T));
        Assert.Equal(ChangeStatus.Reverted, rc.History().Single().Status);
    }

    [Fact]
    public void Revert_deletes_value_that_did_not_exist()
    {
        using var tmp = new TempDir();
        var reg = new FakeRegistry();
        var rc = Make(reg, tmp.File("j.json"));

        var rec = rc.ApplyRegistryChange("opt", T, RegistryValueState.DWord(1));
        rc.Revert(rec.Id);

        Assert.False(reg.Read(T).Exists);
    }

    [Fact]
    public void Target_outside_allowlist_is_rejected_and_nothing_written()
    {
        using var tmp = new TempDir();
        var reg = new FakeRegistry();
        var rc = Make(reg, tmp.File("j.json"));
        var other = new RegistryTarget("HKCU", @"Software\Other", "X");

        Assert.Throws<InvalidOperationException>(() =>
            rc.ApplyRegistryChange("opt", other, RegistryValueState.DWord(1)));
        Assert.Equal(0, reg.WriteCount);
        Assert.Empty(rc.History());
    }

    [Fact]
    public void Hklm_is_rejected_even_if_listed()
    {
        var hklm = new RegistryTarget("HKLM", @"Software\EvoTest", "ValueA");
        Assert.False(new RegistryAllowList(new[] { hklm }).IsAllowed(hklm));
    }

    [Fact]
    public void Simulation_mode_never_writes()
    {
        using var tmp = new TempDir();
        var reg = new FakeRegistry();
        var rc = Make(reg, tmp.File("sim.json"), ExecutionMode.Simulation);

        var rec = rc.ApplyRegistryChange("opt", T, RegistryValueState.DWord(1));

        Assert.Equal(ChangeStatus.Simulated, rec.Status);
        Assert.Equal(0, reg.WriteCount);
        Assert.False(reg.Read(T).Exists);
    }

    [Fact]
    public void Ineffective_write_is_detected_as_ApplyFailed()
    {
        using var tmp = new TempDir();
        var reg = new FakeRegistry { IgnoreWrites = true };
        var rc = Make(reg, tmp.File("j.json"));

        var rec = rc.ApplyRegistryChange("opt", T, RegistryValueState.DWord(1));

        Assert.Equal(ChangeStatus.ApplyFailed, rec.Status);
    }

    [Fact]
    public void External_change_blocks_revert_unless_forced()
    {
        using var tmp = new TempDir();
        var reg = new FakeRegistry();
        reg.Values[T.ToString()] = RegistryValueState.DWord(0);
        var rc = Make(reg, tmp.File("j.json"));
        var rec = rc.ApplyRegistryChange("opt", T, RegistryValueState.DWord(1));

        reg.Values[T.ToString()] = RegistryValueState.DWord(7); // alguien lo cambió después

        Assert.Equal(RevertOutcome.SkippedExternalChange, rc.Revert(rec.Id).Outcome);
        Assert.Equal(RegistryValueState.DWord(7), reg.Read(T));
        Assert.Equal(RevertOutcome.Restored, rc.Revert(rec.Id, force: true).Outcome);
        Assert.Equal(RegistryValueState.DWord(0), reg.Read(T));
    }

    [Fact]
    public void RevertAll_undoes_stacked_changes_in_reverse_order()
    {
        using var tmp = new TempDir();
        var reg = new FakeRegistry();
        reg.Values[T.ToString()] = RegistryValueState.DWord(0);
        var rc = Make(reg, tmp.File("j.json"));
        rc.ApplyRegistryChange("a", T, RegistryValueState.DWord(1));
        rc.ApplyRegistryChange("b", T, RegistryValueState.DWord(2));

        var results = rc.RevertAll();

        Assert.All(results, r => Assert.Equal(RevertOutcome.Restored, r.Outcome));
        Assert.Equal(RegistryValueState.DWord(0), reg.Read(T));
    }

    [Fact]
    public void Journal_survives_restart_and_can_revert()
    {
        using var tmp = new TempDir();
        var path = tmp.File("j.json");
        var reg = new FakeRegistry();
        reg.Values[T.ToString()] = RegistryValueState.DWord(0);
        var id = Make(reg, path).ApplyRegistryChange("opt", T, RegistryValueState.DWord(1)).Id;

        var restarted = Make(reg, path); // nueva instancia, mismo archivo
        Assert.Equal(RevertOutcome.Restored, restarted.Revert(id).Outcome);
        Assert.Equal(RegistryValueState.DWord(0), reg.Read(T));
    }

    [Fact]
    public void Pending_record_after_crash_is_recovered_and_revertable()
    {
        using var tmp = new TempDir();
        var path = tmp.File("j.json");
        var reg = new FakeRegistry();
        reg.Values[T.ToString()] = RegistryValueState.DWord(1); // la escritura sí llegó a ocurrir
        var pending = new ChangeRecord(Guid.NewGuid(), "opt", DateTimeOffset.UtcNow, T,
            RegistryValueState.DWord(0), RegistryValueState.DWord(1), ChangeStatus.Pending);
        new JsonChangeJournal(path).Save(new[] { pending });

        var rc = Make(reg, path);
        Assert.Equal(1, rc.RecoverPending());
        Assert.Equal(ChangeStatus.Applied, rc.History().Single().Status);
        Assert.Equal(RevertOutcome.Restored, rc.Revert(pending.Id).Outcome);
        Assert.Equal(RegistryValueState.DWord(0), reg.Read(T));
    }

    [Fact]
    public void Corrupt_journal_is_not_silently_discarded()
    {
        using var tmp = new TempDir();
        var path = tmp.File("j.json");
        File.WriteAllText(path, "{ esto no es json");
        Assert.Throws<InvalidDataException>(() => new JsonChangeJournal(path).Load());
        Assert.True(File.Exists(path));
    }
}
```

**`GameModeOptimizationTests.cs`**
```csharp
using Evo.Core;
using Xunit;

namespace Evo.Core.Tests;

public class GameModeOptimizationTests
{
    private static RestoreCenter Rc(FakeRegistry reg, string path) =>
        new(new JsonChangeJournal(path), reg, DefaultAllowList.Create(), ExecutionMode.Real);

    [Fact]
    public void Enables_when_explicitly_disabled_and_reverts()
    {
        using var tmp = new TempDir();
        var reg = new FakeRegistry();
        reg.Values[GameModeOptimization.Target.ToString()] = RegistryValueState.DWord(0);
        var rc = Rc(reg, tmp.File("j.json"));
        var opt = new GameModeOptimization(reg, osBuild: 26100);

        var rec = opt.Apply(rc);

        Assert.NotNull(rec);
        Assert.Equal(RegistryValueState.DWord(1), reg.Read(GameModeOptimization.Target));
        rc.Revert(rec!.Id);
        Assert.Equal(RegistryValueState.DWord(0), reg.Read(GameModeOptimization.Target));
    }

    [Fact]
    public void Does_nothing_when_value_absent_or_already_enabled()
    {
        using var tmp = new TempDir();
        var reg = new FakeRegistry();
        var rc = Rc(reg, tmp.File("j.json"));
        var opt = new GameModeOptimization(reg, 26100);

        Assert.Null(opt.Apply(rc));
        reg.Values[GameModeOptimization.Target.ToString()] = RegistryValueState.DWord(1);
        Assert.Null(opt.Apply(rc));
        Assert.Empty(rc.History());
    }

    [Fact]
    public void Old_windows_build_is_incompatible()
    {
        var opt = new GameModeOptimization(new FakeRegistry(), 10240);
        Assert.False(opt.CheckCompatibility().Compatible);
        using var tmp = new TempDir();
        Assert.Throws<InvalidOperationException>(() => opt.Apply(Rc(new FakeRegistry(), tmp.File("j.json"))));
    }

    [Fact]
    public void Every_optimization_documents_the_five_required_fields()
    {
        var i = new GameModeOptimization(new FakeRegistry(), 26100).Info;
        Assert.All(new[] { i.WhatItChanges, i.Why, i.ExpectedBenefit, i.Risks, i.HowToRestore },
            s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }
}
```

**`FrameStatsTests.cs`**
```csharp
using Evo.Core;
using Xunit;

namespace Evo.Core.Tests;

public class FrameStatsTests
{
    [Fact]
    public void Constant_10ms_gives_100fps_everywhere()
    {
        var s = FrameStats.Compute(Enumerable.Repeat(10.0, 1000).ToArray());
        Assert.Equal(100.0, s.AvgFps, 6);
        Assert.Equal(100.0, s.OnePercentLowFps, 6);
        Assert.Equal(100.0, s.PointOnePercentLowFps, 6);
        Assert.Equal(0.0, s.StdDevMs, 9);
        Assert.Equal(0.0, s.MeanAbsDeltaMs, 9);
        Assert.Empty(s.Stutters);
        Assert.Equal(10.0, s.DurationSeconds, 9);
    }

    [Fact]
    public void Ten_slow_frames_in_a_thousand_known_values()
    {
        var ft = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(50.0, 10)).ToArray();
        var s = FrameStats.Compute(ft);

        Assert.Equal(1000.0 * 1000 / 10400.0, s.AvgFps, 6);   // 96,1538...
        Assert.Equal(20.0, s.OnePercentLowFps, 6);             // media de las 10 peores = 50 ms
        Assert.Equal(20.0, s.PointOnePercentLowFps, 6);        // 1 peor frame = 50 ms
        Assert.Equal(100.0, s.OnePercentLowP99Fps, 6);         // percentil 99 = 10 ms: método distinto
        Assert.Equal(20.0, s.MinFps, 6);
        Assert.Equal(100.0, s.MaxFps, 6);
    }

    [Fact]
    public void Single_spike_is_detected_once_with_correct_time()
    {
        var ft = Enumerable.Repeat(10.0, 200).ToArray();
        ft[100] = 40.0;
        var s = FrameStats.Compute(ft);

        var e = Assert.Single(s.Stutters);
        Assert.Equal(100, e.FrameIndex);
        Assert.Equal(1.0, e.TimeSeconds, 6);
        Assert.Equal(10.0, e.BaselineMs, 6);
    }

    [Fact]
    public void Invalid_values_are_ignored_and_too_few_frames_throw()
    {
        var s = FrameStats.Compute(new[] { 10.0, double.NaN, -1, 0, 10.0, double.PositiveInfinity });
        Assert.Equal(2, s.FrameCount);
        Assert.Throws<ArgumentException>(() => FrameStats.Compute(new[] { 10.0 }));
    }
}
```

**`PresentMonParserTests.cs`**
```csharp
using Evo.Core;
using Xunit;

namespace Evo.Core.Tests;

public class PresentMonParserTests
{
    [Fact]
    public void Parses_v1_style_columns_and_counts_dropped()
    {
        var csv = "Application,ProcessID,Dropped,MsBetweenPresents\n" +
                  "cs2.exe,1,0,10.0\ncs2.exe,1,1,12.5\ncs2.exe,1,0,abc\n";
        var cap = PresentMonCsvParser.Parse(new StringReader(csv), FrameMetric.MsBetweenPresents);

        Assert.Equal(new[] { 10.0, 12.5 }, cap.FrameTimesMs);
        Assert.Equal("MsBetweenPresents", cap.ColumnUsed);
        Assert.Equal(1, cap.DroppedRows);
        Assert.Equal(1, cap.SkippedRows);
    }

    [Fact]
    public void Parses_v2_style_column_and_filters_process()
    {
        var csv = "Application,FrameTime\ncs2.exe,8.0\nchrome.exe,16.0\ncs2.exe,9.0\n";
        var cap = PresentMonCsvParser.Parse(new StringReader(csv), FrameMetric.MsBetweenPresents, "CS2.EXE");

        Assert.Equal(new[] { 8.0, 9.0 }, cap.FrameTimesMs);
        Assert.Empty(cap.Warnings.Where(w => w.Contains("aplicaciones")));
    }

    [Fact]
    public void Warns_when_mixing_applications_without_filter()
    {
        var csv = "Application,FrameTime\na.exe,8\nb.exe,9\n";
        var cap = PresentMonCsvParser.Parse(new StringReader(csv), FrameMetric.MsBetweenPresents);
        Assert.Contains(cap.Warnings, w => w.Contains("2 aplicaciones"));
    }

    [Fact]
    public void Unknown_columns_fail_loudly()
    {
        Assert.Throws<FormatException>(() =>
            PresentMonCsvParser.Parse(new StringReader("A,B\n1,2\n"), FrameMetric.MsBetweenPresents));
    }

    [Fact]
    public void Decimal_point_is_culture_invariant()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            var cap = PresentMonCsvParser.Parse(new StringReader("FrameTime\n6.944\n"), FrameMetric.MsBetweenPresents);
            Assert.Equal(6.944, cap.FrameTimesMs.Single(), 6);
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }
}
```

**`RulesTests.cs`**
```csharp
using Evo.Core;
using Xunit;

namespace Evo.Core.Tests;

public class RulesTests
{
    [Fact]
    public void Gpu_bound_pattern_detected_with_caveat()
    {
        var f = new GpuBoundRule().Evaluate(new SystemSnapshot { GpuEngine3DPct = 98, CpuTotalPct = 60 });
        Assert.NotNull(f);
        Assert.False(string.IsNullOrWhiteSpace(f!.Caveat));
        Assert.Contains("compatible", f.Title);   // lenguaje de correlación
    }

    [Fact]
    public void Missing_data_yields_no_finding_instead_of_a_guess()
    {
        Assert.Null(new GpuBoundRule().Evaluate(new SystemSnapshot { GpuEngine3DPct = 98 }));
        Assert.Null(new CpuBoundRule().Evaluate(new SystemSnapshot()));
        Assert.Null(new ThermalThrottlingRule().Evaluate(new SystemSnapshot { CpuTempC = 99 }));
    }

    [Fact]
    public void Thermal_needs_load_temperature_and_low_clock()
    {
        var s = new SystemSnapshot { CpuTempC = 98, CpuTjMaxC = 100, CpuClockMHz = 2000, CpuMaxClockMHz = 4400, CpuTotalPct = 95 };
        Assert.NotNull(new ThermalThrottlingRule().Evaluate(s));
        Assert.Null(new ThermalThrottlingRule().Evaluate(s with { CpuTotalPct = 10 }));
        Assert.Null(new ThermalThrottlingRule().Evaluate(s with { CpuClockMHz = 4300 }));
    }

    [Fact]
    public void Dual_module_reference_machine_does_not_trigger_single_channel_rule()
    {
        Assert.Null(new SingleChannelMemoryRule().Evaluate(new SystemSnapshot { GpuIsIntegrated = true, MemoryModuleCount = 2 }));
        Assert.NotNull(new SingleChannelMemoryRule().Evaluate(new SystemSnapshot { GpuIsIntegrated = true, MemoryModuleCount = 1 }));
    }

    private sealed class Boom : IRule
    {
        public string Id => "boom";
        public Finding? Evaluate(SystemSnapshot s) => throw new InvalidOperationException();
    }

    [Fact]
    public void Failing_rule_does_not_break_the_engine()
    {
        string? failed = null;
        var engine = new RuleEngine(new IRule[] { new Boom(), new GpuBoundRule() }, (id, _) => failed = id);
        var findings = engine.Evaluate(new SystemSnapshot { GpuEngine3DPct = 99, CpuTotalPct = 50 });
        Assert.Single(findings);
        Assert.Equal("boom", failed);
    }
}
```

### Verificación

```powershell
dotnet build -c Release
dotnet test -c Release
```

Pégame la salida completa, incluidos los errores si los hay. No he podido compilarlo y es probable que haya alguna errata, por ejemplo un conflicto de nombres o una advertencia tratada como error. Las corregiré antes de continuar.

---

## 5. Qué está y qué no está operativo

| Componente | Estado | Origen del dato / limitación |
|---|---|---|
| Restore Center (registro HKCU) | Código y pruebas escritos, **sin ejecutar** | Probado con registro simulado. Falta una prueba de integración con `WindowsRegistryAccessor` sobre una clave temporal. |
| Game Mode | Escrito, **sin ejecutar** | El supuesto "valor ausente = predeterminado de Windows" debe verificarse en el PN64. El beneficio **no está demostrado**. |
| FrameStats | Escrito, **sin ejecutar** | Puro cálculo. Las definiciones de "1% Low" están explícitas y rotuladas. |
| Parser PresentMon | Escrito, **sin ejecutar** | Falta validar con un CSV real de tu versión de PresentMon. |
| Motor de reglas | Escrito, **sin ejecutar** | Los umbrales (95 %, 85 %, 0,8×) son heurísticas iniciales. Las calibraré con capturas reales de CS2 y no las presento como verdad. |
| Telemetría, UI, Game Booster, Benchmark Lab, Thermal/Latency Lab, etc. | **No iniciados** | Fases 2 a 5. |

**Deuda técnica registrada:**
- `Evo.Windows` solo tiene el accessor de registro.
- Falta registro de logs, es decir, `ILogger` con archivo rotativo.
- Las optimizaciones de prioridad, afinidad, plan de energía y servicios aún no usan el Restore Center, que por ahora solo cubre el registro. Habrá que generalizarlo a "acciones reversibles" antes de la fase 4.
- El Benchmark Lab necesitará un modelo de "condiciones de prueba" (resolución, mapa, temperatura inicial, procesos de fondo) con una regla dura: **no se calcula ningún porcentaje de mejora entre sesiones con condiciones distintas**.

## 6. Qué necesito de ti para continuar

1. La salida del script de auditoría (sección 1).
2. La salida de `dotnet build` y `dotnet test`.
3. Si el repo ya tiene código, su estructura, para adaptarme en lugar de duplicar.
4. Una captura real de PresentMon de CS2 en el PN64 (el CSV, aunque sean 60 s) para validar el parser y calibrar las reglas.

Con eso paso a la fase 2: telemetría real con contadores de Windows y WMI, y el Hardware Analyzer con la distinción disponible, no disponible y requiere administrador.
````