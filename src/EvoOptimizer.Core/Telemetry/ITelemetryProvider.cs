namespace EvoOptimizer.Core.Telemetry;

public interface ITelemetryProvider
{
    /// <summary>Toma una muestra. Debe ser barata; proveedores costosos deben cachear internamente.</summary>
    TelemetrySnapshot Sample();
}
