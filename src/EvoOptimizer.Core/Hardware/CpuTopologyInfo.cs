namespace EvoOptimizer.Core.Hardware;

public enum CoreType
{
    Standard,
    Performance,
    Efficient
}

public enum GamingAffinityMode
{
    AllCores,
    PerformanceCoresOnly,
    PerformanceAndLightEfficiency,
    ExcludeCore0
}

public sealed record CoreDescription(
    int CoreIndex,
    CoreType Type,
    ulong AffinityMask,
    int LogicalProcessorCount,
    bool HasHyperThreading);

public sealed record CpuTopologyInfo(
    string ProcessorName,
    int TotalPhysicalCores,
    int LogicalProcessors,
    int PerformanceCores,
    int EfficientCores,
    ulong PerformanceCoresMask,
    ulong EfficientCoresMask,
    ulong AllCoresMask,
    bool IsHybrid,
    IReadOnlyList<CoreDescription> Cores)
{
    /// <summary>
    /// Límite de procesadores lógicos direccionables con una máscara de 64 bits.
    /// Windows reparte más hilos en grupos de procesadores y la afinidad de un
    /// proceso sólo cubre el grupo activo, por lo que esos casos se rechazan.
    /// </summary>
    private const int MaxLogicalProcessors = 64;

    /// <summary>
    /// Cantidad de E-Cores auxiliares que se suman a los P-Cores en el modo
    /// balanceado. Es una política de reparto, no una máscara fija.
    /// </summary>
    private const int LightEfficiencyCoreCount = 2;

    /// <summary>
    /// Cantidad de procesadores lógicos (hilos) pertenecientes a núcleos de rendimiento (P-Cores).
    /// En procesadores sin clases heterogéneas se consideran todos los núcleos.
    /// </summary>
    public int PerformanceLogicalProcessors
    {
        get
        {
            IReadOnlyList<CoreDescription> cores = CoreList;
            if (cores.Count == 0)
            {
                return PerformanceCores * (LogicalProcessors > TotalPhysicalCores ? 2 : 1);
            }

            int performanceThreads = cores
                .Where(c => c.Type == CoreType.Performance)
                .Sum(c => c.LogicalProcessorCount);

            if (performanceThreads > 0) return performanceThreads;

            // CPU estándar (sin P-Cores declarados): todos los hilos son aptos.
            return cores.Sum(c => c.LogicalProcessorCount);
        }
    }

    /// <summary>
    /// Cantidad de procesadores lógicos (hilos) pertenecientes a núcleos eficientes (E-Cores).
    /// </summary>
    public int EfficientLogicalProcessors
    {
        get
        {
            IReadOnlyList<CoreDescription> cores = CoreList;
            if (cores.Count == 0) return EfficientCores;

            int efficientThreads = cores
                .Where(c => c.Type == CoreType.Efficient)
                .Sum(c => c.LogicalProcessorCount);

            return efficientThreads > 0 ? efficientThreads : EfficientCores;
        }
    }

    /// <summary>
    /// Valida que la topología descubierta sea confiable y contenga datos consistentes.
    /// Si los datos no son consistentes, no deben aplicarse cambios de afinidad al sistema.
    /// </summary>
    public bool IsTopologyReliable
    {
        get
        {
            if (TotalPhysicalCores <= 0) return false;
            if (LogicalProcessors <= 0 || LogicalProcessors > MaxLogicalProcessors) return false;
            if (AllCoresMask == 0) return false;
            if (PerformanceCores < 0 || EfficientCores < 0) return false;
            if (PerformanceCores + EfficientCores > TotalPhysicalCores) return false;

            IReadOnlyList<CoreDescription> cores = CoreList;
            if (cores.Count == 0 || cores.Count != TotalPhysicalCores) return false;

            // Máscaras declaradas: deben ser subconjuntos válidos y disjuntos.
            if ((PerformanceCoresMask & AllCoresMask) != PerformanceCoresMask) return false;
            if ((EfficientCoresMask & AllCoresMask) != EfficientCoresMask) return false;
            if ((PerformanceCoresMask & EfficientCoresMask) != 0) return false;
            if ((PerformanceCoresMask | EfficientCoresMask) != AllCoresMask) return false;

            var indices = new HashSet<int>();
            ulong union = 0;
            int performanceCount = 0;
            int efficientCount = 0;
            int logicalSum = 0;

            foreach (CoreDescription core in cores)
            {
                if (core is null) return false;
                if (core.CoreIndex < 0) return false;
                if (!indices.Add(core.CoreIndex)) return false;
                if (!IsCoreMaskValid(core, AllCoresMask)) return false;
                if ((union & core.AffinityMask) != 0) return false;

                union |= core.AffinityMask;
                logicalSum += core.LogicalProcessorCount;

                switch (core.Type)
                {
                    case CoreType.Performance:
                        performanceCount++;
                        break;
                    case CoreType.Efficient:
                        efficientCount++;
                        break;
                    case CoreType.Standard:
                        break;
                    default:
                        return false;
                }
            }

            // La unión de los núcleos debe reconstruir exactamente la máscara total.
            if (union != AllCoresMask) return false;
            if (logicalSum != PopCount(AllCoresMask)) return false;

            if (IsHybrid)
            {
                // Sin datos heterogéneos completos no se asume una topología híbrida.
                if (performanceCount == 0 || efficientCount == 0) return false;
                if (PerformanceCores != performanceCount) return false;
                if (EfficientCores != efficientCount) return false;
                if (PerformanceCoresMask == 0 || EfficientCoresMask == 0) return false;
                if (PerformanceCoresMask != MaskOf(cores, CoreType.Performance)) return false;
                if (EfficientCoresMask != MaskOf(cores, CoreType.Efficient)) return false;
            }
            else
            {
                // En CPU estándar el proveedor puede rotular los núcleos con la clase
                // de eficiencia nativa; se exige coherencia de agregados, no de tipos.
                if (PerformanceCores == 0 && EfficientCores == 0) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Topología neutra para CPU sin clases heterogéneas detectables.
    /// No inventa P-Cores ni E-Cores: todos los núcleos quedan como <see cref="CoreType.Standard"/>.
    /// </summary>
    public static CpuTopologyInfo Default(int logicalProcessors, string? name = null)
    {
        int count = Math.Clamp(logicalProcessors, 0, MaxLogicalProcessors);
        if (count == 0)
        {
            return new CpuTopologyInfo(
                ProcessorName: name ?? "Procesador x86/x64",
                TotalPhysicalCores: 0,
                LogicalProcessors: 0,
                PerformanceCores: 0,
                EfficientCores: 0,
                PerformanceCoresMask: 0,
                EfficientCoresMask: 0,
                AllCoresMask: 0,
                IsHybrid: false,
                Cores: Array.Empty<CoreDescription>());
        }

        ulong mask = count >= MaxLogicalProcessors ? ulong.MaxValue : (1UL << count) - 1;
        var defaultCores = new List<CoreDescription>(count);
        for (int i = 0; i < count; i++)
        {
            defaultCores.Add(new CoreDescription(i, CoreType.Standard, 1UL << i, 1, false));
        }

        return new CpuTopologyInfo(
            ProcessorName: name ?? "Procesador x86/x64",
            TotalPhysicalCores: count,
            LogicalProcessors: count,
            PerformanceCores: count,
            EfficientCores: 0,
            PerformanceCoresMask: mask,
            EfficientCoresMask: 0,
            AllCoresMask: mask,
            IsHybrid: false,
            Cores: defaultCores);
    }

    /// <summary>
    /// Calcula dinámicamente la máscara de afinidad según la política seleccionada y los núcleos detectados.
    /// No utiliza valores mágicos ni posiciones prefijadas: itera sobre la lista de núcleos descubiertos.
    /// </summary>
    public ulong GetAffinityMask(GamingAffinityMode mode)
    {
        ulong allMask = AllCoresMask;
        if (allMask == 0) return 0;

        switch (mode)
        {
            case GamingAffinityMode.PerformanceCoresOnly:
                return ResolvePerformanceMask(allMask);

            case GamingAffinityMode.PerformanceAndLightEfficiency:
            {
                ulong mask = ResolvePerformanceMask(allMask);
                int taken = 0;
                foreach (CoreDescription core in OrderedCores())
                {
                    if (taken >= LightEfficiencyCoreCount) break;
                    if (core.Type != CoreType.Efficient) continue;
                    if (!IsCoreMaskValid(core, allMask)) continue;
                    mask |= core.AffinityMask;
                    taken++;
                }
                return mask == 0 ? allMask : mask & allMask;
            }

            case GamingAffinityMode.ExcludeCore0:
            {
                // Se excluye el primer núcleo físico descubierto (con todos sus hilos).
                ulong firstCoreMask = 0;
                foreach (CoreDescription core in OrderedCores())
                {
                    if (!IsCoreMaskValid(core, allMask)) continue;
                    firstCoreMask = core.AffinityMask;
                    break;
                }

                if (firstCoreMask == 0) firstCoreMask = allMask & (~allMask + 1);
                ulong mask = allMask & ~firstCoreMask;
                return mask != 0 ? mask : allMask;
            }

            default:
                return allMask;
        }
    }

    private IReadOnlyList<CoreDescription> CoreList =>
        Cores ?? (IReadOnlyList<CoreDescription>)Array.Empty<CoreDescription>();

    private IEnumerable<CoreDescription> OrderedCores() => CoreList.OrderBy(c => c.CoreIndex);

    private ulong ResolvePerformanceMask(ulong allMask)
    {
        ulong mask = 0;
        foreach (CoreDescription core in OrderedCores())
        {
            if (core.Type != CoreType.Performance) continue;
            if (!IsCoreMaskValid(core, allMask)) continue;
            mask |= core.AffinityMask;
        }

        if (mask == 0 && (PerformanceCoresMask & allMask) == PerformanceCoresMask)
        {
            mask = PerformanceCoresMask;
        }

        // Sólo una CPU sin clases heterogéneas puede usar todos los núcleos como
        // "rendimiento". En híbridas sin P-Cores válidos se devuelve 0 para que el
        // consumidor rechace la operación en lugar de inventar una máscara.
        if (mask == 0 && !IsHybrid) mask = allMask;

        return mask & allMask;
    }

    private static bool IsCoreMaskValid(CoreDescription core, ulong allMask) =>
        core is not null &&
        core.CoreIndex >= 0 &&
        core.AffinityMask != 0 &&
        (core.AffinityMask & allMask) == core.AffinityMask &&
        core.LogicalProcessorCount > 0 &&
        core.LogicalProcessorCount == PopCount(core.AffinityMask);

    private static ulong MaskOf(IReadOnlyList<CoreDescription> cores, CoreType type)
    {
        ulong mask = 0;
        foreach (CoreDescription core in cores)
        {
            if (core.Type == type) mask |= core.AffinityMask;
        }
        return mask;
    }

    private static int PopCount(ulong value) => System.Numerics.BitOperations.PopCount(value);
}
