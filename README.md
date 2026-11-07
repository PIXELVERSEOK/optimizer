# EVO FPS OPTIMIZER

Solución WPF/C# organizada en tres proyectos, reconstruida a partir de los archivos fuente ya compartidos.

## Estructura activa

- `src/EvoOptimizer.App`: WPF, MVVM, dashboard, controles y tema.
- `src/EvoOptimizer.Core`: modelos, contratos de telemetría, hardware, decisiones y score.
- `src/EvoOptimizer.Infrastructure`: captura Windows/Win32 y perfil de hardware.
- `reference/`: fuentes originales de Claude/Argon y ZIP/script recibidos, preservados sin mezclarlos en la compilación activa.

## Estado funcional real

Esta reconstrucción corresponde a la base modular de Fase 1. Incluye telemetría global de CPU y memoria/commit, perfil de hardware y dashboard. El motor `IDecisionEngine` todavía es un contrato sin implementación conectada; GPU, temperatura, disco, procesos y frametime/PresentMon siguen pendientes. No se aplican cambios al sistema.

Las implementaciones monolíticas anteriores de Argon (.NET 8) y el núcleo previo de Claude se conservan en `reference/` para migrar sus piezas de forma controlada; no se compilan dentro de la solución activa porque tienen otra estructura y contratos.

## Compilación en Windows

Desde esta carpeta, con .NET 10 SDK y workload/targeting packs de Windows Desktop disponibles:

```powershell
dotnet build .\EVO-FPS-OPTIMIZER.slnx
```

No se declara compilación validada desde este entorno.
