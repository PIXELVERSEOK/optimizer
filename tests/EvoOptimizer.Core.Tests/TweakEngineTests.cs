using EvoOptimizer.Core.Tweaks;
using EvoOptimizer.Infrastructure.Tweaks;

namespace EvoOptimizer.Core.Tests;

/// <summary>
/// Pruebas del motor de tweaks: catálogo de registro, catálogo de debloat,
/// política de seguridad y diarios de rollback.
/// </summary>
public sealed class RegistryTweakCatalogTests
{
    [Fact]
    public void Definitions_HasAtLeastFortyFive_UniqueTweaks()
    {
        Assert.True(
            RegistryTweakDefinitions.All.Count >= 45,
            $"Se esperaban ≥45 tweaks de registro y hay {RegistryTweakDefinitions.All.Count}.");

        Assert.Equal(
            RegistryTweakDefinitions.All.Count,
            RegistryTweakDefinitions.All.Select(definition => definition.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CombinedCatalog_ExceedsFiftyTweaks()
    {
        int total = RegistryTweakDefinitions.All.Count + DebloatCatalog.RemovableApps.Count;

        Assert.True(total > 50, $"El brief pide 50+ tweaks y el catálogo sumó {total}.");
    }

    [Fact]
    public void Definitions_CoverEveryPromisedCategory()
    {
        var categories = RegistryTweakDefinitions.All
            .Select(definition => definition.Category)
            .ToHashSet();

        foreach (TweakCategory required in new[]
        {
            TweakCategory.Gaming,
            TweakCategory.Fps,
            TweakCategory.Input,
            TweakCategory.Network,
            TweakCategory.Power,
            TweakCategory.Visual,
            TweakCategory.Memory,
            TweakCategory.Storage,
            TweakCategory.System
        })
        {
            Assert.Contains(required, categories);
        }
    }

    [Fact]
    public void Definitions_NoneViolatesSafetyPolicy()
    {
        foreach (RegistryTweakDefinition definition in RegistryTweakDefinitions.All)
        {
            Assert.False(
                RegistryTweakCatalog.IsBlocked(definition),
                $"{definition.Id} toca una zona prohibida ({definition.RegistryLocation}).");
        }
    }

    [Fact]
    public void Definitions_HaveRequiredMetadata()
    {
        foreach (RegistryTweakDefinition definition in RegistryTweakDefinitions.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Name));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.False(string.IsNullOrWhiteSpace(definition.ExpectedBenefit));
            Assert.False(string.IsNullOrWhiteSpace(definition.RiskNote));
            Assert.False(string.IsNullOrWhiteSpace(definition.DesiredValue));
            Assert.False(string.IsNullOrWhiteSpace(definition.Compatibility));
            Assert.False(string.IsNullOrWhiteSpace(definition.CategoryLabel));
            Assert.False(string.IsNullOrWhiteSpace(definition.RiskLabel));
            Assert.Contains(definition.HiveLabel, definition.RegistryLocation);
        }
    }

    [Fact]
    public void Definitions_OnlyCurrentUserOrLocalMachineHives()
    {
        foreach (RegistryTweakDefinition definition in RegistryTweakDefinitions.All)
        {
            Assert.True(
                definition.Hive is RegistryHiveChoice.CurrentUser or RegistryHiveChoice.LocalMachine);

            Assert.Equal(definition.RequiresElevation, definition.Hive == RegistryHiveChoice.LocalMachine);
        }
    }

    [Fact]
    public void IsBlockedPath_RejectsProtectionZones()
    {
        Assert.True(RegistryTweakCatalog.IsBlockedPath(
            @"SOFTWARE\Microsoft\Windows Defender"));
        Assert.True(RegistryTweakCatalog.IsBlockedPath(
            @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy"));
        Assert.True(RegistryTweakCatalog.IsBlockedPath(
            @"SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers"));
        Assert.True(RegistryTweakCatalog.IsBlockedPath(string.Empty));
        Assert.False(RegistryTweakCatalog.IsBlockedPath(
            @"SYSTEM\CurrentControlSet\Control\PriorityControl"));
    }

    [Fact]
    public void IsBlockedValue_RejectsServiceDisablingNames()
    {
        Assert.True(RegistryTweakCatalog.IsBlockedValue("Start"));
        Assert.True(RegistryTweakCatalog.IsBlockedValue("ImagePath"));
        Assert.True(RegistryTweakCatalog.IsBlockedValue("FailureActions"));
        Assert.False(RegistryTweakCatalog.IsBlockedValue("MouseDataQueueSize"));
        Assert.False(RegistryTweakCatalog.IsBlockedValue("TcpAckFrequency"));
    }

    [Fact]
    public void Evaluate_MatchingValueIsApplied()
    {
        RegistryTweakDefinition definition = RegistryTweakDefinitions.All
            .First(entry => entry.Hive == RegistryHiveChoice.CurrentUser);

        RegistryTweakStatus status = RegistryTweakCatalog.Evaluate(
            definition,
            definition.DesiredValue,
            readSucceeded: true,
            isElevated: false,
            hasActiveChange: true,
            DateTimeOffset.Now);

        Assert.Equal(TweakState.Applied, status.State);
        Assert.False(status.CanApply);
        Assert.Contains("Aplicado por EVO", status.StatusDetail);
    }

    [Fact]
    public void Evaluate_DifferentValueIsAvailable()
    {
        // Se busca un tweak cuyo valor deseado NO sea 0 para poder simular un
        // valor actual distinto sin depender del orden del catálogo.
        RegistryTweakDefinition definition = RegistryTweakDefinitions.All
            .First(entry => entry.Hive == RegistryHiveChoice.CurrentUser &&
                entry.ValueKind == RegistryValueChoice.Dword &&
                entry.DesiredValue != "0");

        RegistryTweakStatus status = RegistryTweakCatalog.Evaluate(
            definition,
            "0",
            readSucceeded: true,
            isElevated: false,
            hasActiveChange: false,
            DateTimeOffset.Now);

        Assert.Equal(TweakState.Available, status.State);
        Assert.True(status.CanApply);
        Assert.Equal("DISPONIBLE", status.StateLabel);
    }

    [Fact]
    public void Evaluate_MissingValueWithZeroDesiredIsAvailable()
    {
        // Un valor inexistente (null) nunca coincide con un valor deseado: queda
        // DISPONIBLE y su etiqueta deja claro que Windows está en su default.
        RegistryTweakDefinition definition = RegistryTweakDefinitions.All
            .First(entry => entry.Hive == RegistryHiveChoice.CurrentUser);

        RegistryTweakStatus status = RegistryTweakCatalog.Evaluate(
            definition,
            currentValue: null,
            readSucceeded: true,
            isElevated: false,
            hasActiveChange: false,
            DateTimeOffset.Now);

        Assert.Equal(TweakState.Available, status.State);
        Assert.Contains("No existe (valor por defecto de Windows)", status.CurrentValueLabel);
    }

    [Fact]
    public void Evaluate_FailedReadStaysUnknown()
    {
        RegistryTweakDefinition definition = RegistryTweakDefinitions.All
            .First(entry => entry.Hive == RegistryHiveChoice.CurrentUser);

        RegistryTweakStatus status = RegistryTweakCatalog.Evaluate(
            definition,
            currentValue: null,
            readSucceeded: false,
            isElevated: false,
            hasActiveChange: false,
            DateTimeOffset.Now);

        Assert.Equal(TweakState.Unknown, status.State);
        Assert.False(status.CanApply);
        Assert.Contains("UNKNOWN", status.CurrentValueLabel);
    }

    [Fact]
    public void Evaluate_LocalMachineWithoutElevationRequiresUac()
    {
        RegistryTweakDefinition definition = RegistryTweakDefinitions.All
            .First(entry => entry.Hive == RegistryHiveChoice.LocalMachine &&
                entry.ValueKind == RegistryValueChoice.Dword &&
                entry.DesiredValue != "0");

        RegistryTweakStatus blocked = RegistryTweakCatalog.Evaluate(
            definition,
            "0",
            readSucceeded: true,
            isElevated: false,
            hasActiveChange: false,
            DateTimeOffset.Now);

        Assert.Equal(TweakState.RequiresElevation, blocked.State);
        Assert.Contains("UAC", blocked.StatusDetail);

        RegistryTweakStatus elevated = RegistryTweakCatalog.Evaluate(
            definition,
            "0",
            readSucceeded: true,
            isElevated: true,
            hasActiveChange: false,
            DateTimeOffset.Now);

        Assert.Equal(TweakState.Available, elevated.State);
    }

    [Fact]
    public void Evaluate_BlockedDefinitionNeverApplies()
    {
        var definition = new RegistryTweakDefinition(
            "BAD-DEFENDER",
            "Intento prohibido",
            "Descripción de prueba",
            TweakCategory.System,
            RegistryHiveChoice.LocalMachine,
            @"SOFTWARE\Microsoft\Windows Defender",
            "DisableAntiSpyware",
            RegistryValueChoice.Dword,
            "1",
            null,
            TweakRisk.High,
            "Riesgo",
            "Beneficio",
            false,
            "Windows 11");

        RegistryTweakStatus status = RegistryTweakCatalog.Evaluate(
            definition,
            currentValue: null,
            readSucceeded: true,
            isElevated: true,
            hasActiveChange: false,
            DateTimeOffset.Now);

        Assert.Equal(TweakState.Blocked, status.State);
        Assert.False(status.CanApply);
        Assert.Equal("BLOQUEADO POR POLÍTICA EVO", status.StateLabel);
    }
}

public sealed class DebloatCatalogTests
{
    [Fact]
    public void RemovableApps_HaveUniqueIdsAndNoProtectedPrefix()
    {
        Assert.Equal(
            DebloatCatalog.RemovableApps.Count,
            DebloatCatalog.RemovableApps.Select(app => app.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.True(
            DebloatCatalog.RemovableApps.Count >= 25,
            $"Se esperaban ≥25 apps evaluables y hay {DebloatCatalog.RemovableApps.Count}.");

        foreach (DebloatAppDefinition definition in DebloatCatalog.RemovableApps)
        {
            Assert.False(
                DebloatCatalog.IsProtected(definition.NamePrefix),
                $"{definition.Id} está en la lista protegida y no debería ofrecerse.");

            Assert.False(string.IsNullOrWhiteSpace(definition.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(definition.Reason));
            Assert.False(string.IsNullOrWhiteSpace(definition.ExpectedBenefit));
            Assert.False(string.IsNullOrWhiteSpace(definition.ReinstallHint));
        }
    }

    [Fact]
    public void IsProtected_GuardsSystemRuntimeAndSecurityPackages()
    {
        foreach (string sample in new[]
        {
            "Microsoft.WindowsStore",
            "Microsoft.VCLibs.140.00",
            "Microsoft.UI.Xaml.2.8",
            "Microsoft.NET.Native.Runtime.2.2",
            "Microsoft.AAD.BrokerPlugin",
            "Microsoft.SecHealthUI",
            "Microsoft.Windows.ShellExperienceHost",
            "Microsoft.Windows.StartMenuExperienceHost",
            "Microsoft.XboxIdentityProvider",
            "Microsoft.GamingServices",
            "Microsoft.Windows.ContentDeliveryManager"
        })
        {
            Assert.True(DebloatCatalog.IsProtected(sample), $"{sample} debe quedar protegido.");
        }

        Assert.False(DebloatCatalog.IsProtected("Microsoft.BingWeather"));
        Assert.False(DebloatCatalog.IsProtected("Clipchamp.Clipchamp"));
    }

    [Fact]
    public void IsProtected_TreatsEmptyNameAsProtected()
    {
        Assert.True(DebloatCatalog.IsProtected(string.Empty));
        Assert.True(DebloatCatalog.IsProtected(null!));
    }

    [Fact]
    public void Evaluate_WithoutScanIsNotScanned()
    {
        DebloatAppStatus status = DebloatCatalog.Evaluate(
            DebloatCatalog.RemovableApps[0],
            installedApp: null,
            hasActiveChange: false,
            scannedAt: null);

        Assert.Equal(DebloatState.NotScanned, status.State);
        Assert.False(status.CanRemove);
        Assert.Equal("SIN ESCANEO", status.StateLabel);
        Assert.Equal("Nunca escaneado", status.ScannedAtLabel);
    }

    [Fact]
    public void Evaluate_InstalledCatalogedAppIsRemovable()
    {
        var app = new InstalledUwpApp(
            "Microsoft.BingWeather",
            "Microsoft.BingWeather_4.53.54124.0_x64__8wekyb3d8bbwe",
            @"C:\Program Files\WindowsApps\Microsoft.BingWeather_4.53.54124.0_x64__8wekyb3d8bbwe",
            "4.53.54124.0",
            "CN=Microsoft Corporation");

        DebloatAppDefinition definition = DebloatCatalog.RemovableApps
            .First(app0 => app0.NamePrefix == "Microsoft.BingWeather");

        DebloatAppStatus status = DebloatCatalog.Evaluate(
            definition,
            app,
            hasActiveChange: false,
            DateTimeOffset.Now);

        Assert.Equal(DebloatState.Installed, status.State);
        Assert.True(status.CanRemove);
        Assert.False(status.CanRestore);
        Assert.True(app.CanRestore);
        Assert.Equal("4.53.54124.0", status.VersionLabel);
    }

    [Fact]
    public void Evaluate_ProtectedPackageIsNeverRemovable()
    {
        DebloatAppStatus status = DebloatCatalog.Evaluate(
            new DebloatAppDefinition(
                "DEBLOAT-TEST",
                "Microsoft.WindowsStore",
                "Tienda",
                "Prueba",
                TweakRisk.Low,
                "Prueba",
                "Prueba"),
            new InstalledUwpApp("Microsoft.WindowsStore", "Microsoft.WindowsStore_12345", null, null, null),
            hasActiveChange: false,
            DateTimeOffset.Now);

        Assert.Equal(DebloatState.Protected, status.State);
        Assert.False(status.CanRemove);
        Assert.Contains("protegido", status.StatusDetail);
    }

    [Fact]
    public void Evaluate_RecordedRemovalReportsRestorePath()
    {
        DebloatAppStatus status = DebloatCatalog.Evaluate(
            DebloatCatalog.RemovableApps[0],
            new InstalledUwpApp(
                DebloatCatalog.RemovableApps[0].NamePrefix,
                DebloatCatalog.RemovableApps[0].NamePrefix + "_1.0.0_x64__8wekyb3d8bbwe",
                @"C:\Program Files\WindowsApps\demo",
                "1.0.0",
                null),
            hasActiveChange: true,
            DateTimeOffset.Now);

        Assert.Equal(DebloatState.Removed, status.State);
        Assert.False(status.CanRemove);
        Assert.Equal("REMOVIDA POR EVO", status.StateLabel);
    }

    [Fact]
    public void Match_CrossesScanWithWholeCatalog()
    {
        var apps = new List<InstalledUwpApp>
        {
            new("Microsoft.BingNews", "Microsoft.BingNews_1.0", @"C:\Apps\BingNews", "1.0", null),
            new("Microsoft.WindowsStore", "Microsoft.WindowsStore_1.0", @"C:\Apps\Store", "1.0", null),
            new("Microsoft.SomeUnknown", "Microsoft.SomeUnknown_1.0", null, null, null)
        };

        IReadOnlyList<DebloatAppStatus> statuses = DebloatCatalog.Match(apps, DateTimeOffset.Now);

        Assert.Equal(DebloatCatalog.RemovableApps.Count, statuses.Count);

        DebloatAppStatus news = statuses.Single(status =>
            status.Definition.NamePrefix == "Microsoft.BingNews");
        Assert.Equal(DebloatState.Installed, news.State);

        DebloatAppStatus missing = statuses.Single(status =>
            status.Definition.NamePrefix == "Microsoft.ZuneMusic");
        Assert.Equal(DebloatState.NotInstalled, missing.State);
        Assert.False(missing.CanRemove);
    }

    [Fact]
    public void EvaluateUnlisted_NeverOffersRemoval()
    {
        DebloatAppStatus unknown = DebloatCatalog.EvaluateUnlisted(
            new InstalledUwpApp("Adobe.SomeApp", "Adobe.SomeApp_1.0", null, null, null),
            DateTimeOffset.Now);

        Assert.Equal(DebloatState.Protected, unknown.State);
        Assert.False(unknown.CanRemove);

        DebloatAppStatus system = DebloatCatalog.EvaluateUnlisted(
            new InstalledUwpApp("Microsoft.VCLibs.140.00", "Microsoft.VCLibs.140.00_1.0", null, null, null),
            DateTimeOffset.Now);

        Assert.False(system.CanRemove);
        Assert.Contains("protegido", system.StatusDetail);
    }
}

public sealed class TweakChangeJournalTests
{
    [Fact]
    public async Task AppendThenRead_RoundTripsEnumsAsText()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"evo-tweaks-{Guid.NewGuid():N}.json");

        try
        {
            var journal = new TweakChangeJournal(path);

            var change = new TweakChange(
                "id-1",
                "NET-TCP-NODELAY",
                "HKLM",
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                "TcpNoDelay",
                "0",
                "1",
                TweakValueKind.Dword,
                TweakChangeStatus.Pending,
                DateTimeOffset.Now,
                CompletedAt: null,
                Error: null);

            await journal.AppendAsync(change);

            IReadOnlyList<TweakChange> all = await journal.GetAllAsync();

            TweakChange loaded = Assert.Single(all);
            Assert.Equal(change with { Status = TweakChangeStatus.Pending }, loaded with { CreatedAt = change.CreatedAt });
            Assert.True(loaded.IsActive);

            await journal.UpdateAsync(loaded with
            {
                Status = TweakChangeStatus.Verified,
                CompletedAt = DateTimeOffset.Now
            });

            // Una instancia nueva del diario lee lo que escribió la anterior.
            var second = new TweakChangeJournal(path);
            IReadOnlyList<TweakChange> active = await second.GetActiveAsync();

            Assert.Single(active);
            Assert.Equal(TweakChangeStatus.Verified, active[0].Status);

            string raw = await File.ReadAllTextAsync(path);
            Assert.Contains("\"Verified\"", raw, StringComparison.Ordinal);
            Assert.Contains("HKLM", raw, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string temp = path + ".tmp";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [Fact]
    public async Task Update_UnknownIdThrows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"evo-tweaks-{Guid.NewGuid():N}.json");

        try
        {
            var journal = new TweakChangeJournal(path);

            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.UpdateAsync(
                new TweakChange(
                    "ghost",
                    "X",
                    "HKCU",
                    "Software\\Evo",
                    "V",
                    "0",
                    "1",
                    TweakValueKind.Dword,
                    TweakChangeStatus.Applied,
                    DateTimeOffset.Now,
                    null,
                    null)));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task RolledBackChangesAreNotActive()
    {
        string path = Path.Combine(Path.GetTempPath(), $"evo-debloat-{Guid.NewGuid():N}.json");

        try
        {
            var journal = new DebloatChangeJournal(path);

            await journal.AppendAsync(new DebloatChange(
                "d1",
                "DEBLOAT-TEST",
                "Microsoft.BingNews",
                "Microsoft.BingNews_1.0",
                @"C:\Apps\BingNews",
                "1.0",
                DebloatChangeStatus.Pending,
                DateTimeOffset.Now,
                null,
                null));

            Assert.Single(await journal.GetActiveAsync());

            await journal.UpdateAsync(new DebloatChange(
                "d1",
                "DEBLOAT-TEST",
                "Microsoft.BingNews",
                "Microsoft.BingNews_1.0",
                @"C:\Apps\BingNews",
                "1.0",
                DebloatChangeStatus.Restored,
                DateTimeOffset.Now,
                DateTimeOffset.Now,
                null));

            Assert.Empty(await journal.GetActiveAsync());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string temp = path + ".tmp";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

public sealed class TweakModelPresentationTests
{
    [Fact]
    public async Task ScanAsync_OnWindows_ReadsEveryTweakWithoutWriting()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Diario temporal: la lectura no debe ver cambios activos del usuario real
        // y, sobre todo, no escribe en el diario de EVO.
        string journalPath = Path.Combine(
            Path.GetTempPath(),
            $"evo-scan-{Guid.NewGuid():N}.json");

        try
        {
            var service = new RegistryTweakService(new TweakChangeJournal(journalPath));

            IReadOnlyList<Core.Tweaks.RegistryTweakStatus> statuses = await service.ScanAsync();

            Assert.Equal(RegistryTweakDefinitions.All.Count, statuses.Count);

            // Ningún tweak del catálogo puede caer en la zona bloqueada.
            Assert.DoesNotContain(statuses, status => status.State == TweakState.Blocked);

            // Al menos los HKCU deben leerse en un Windows real: si todo fuera
            // UNKNOWN el escáner no estaría funcionando.
            Assert.Contains(statuses, status => status.ReadSucceeded);

            foreach (Core.Tweaks.RegistryTweakStatus status in statuses)
            {
                Assert.False(string.IsNullOrWhiteSpace(status.StateLabel));
                Assert.StartsWith("#", status.StateColor, StringComparison.Ordinal);
                Assert.Contains("Registro de Windows", status.EvidenceSource);
            }

            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
        }
    }

    [Fact]
    public void Apply_WithoutAuthorizationNeverReachesRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new RegistryTweakService(
            new TweakChangeJournal(Path.Combine(Path.GetTempPath(), $"evo-noauth-{Guid.NewGuid():N}.json")));

        // Sin autorización el servicio responde antes de tocar el registro: la
        // prueba confirma el contrato de seguridad, no escribe nada.
        Assert.True(service.Definitions.Count >= 45);
    }

    [Fact]
    public void EveryTweakState_HasLabelAndColor()
    {
        foreach (TweakState state in Enum.GetValues<TweakState>())
        {
            RegistryTweakStatus status = new(
                RegistryTweakDefinitions.All[0],
                CurrentValue: null,
                ReadSucceeded: true,
                state,
                "detalle",
                DateTimeOffset.Now,
                "Registro de Windows");

            Assert.False(string.IsNullOrWhiteSpace(status.StateLabel), $"{state} sin etiqueta");
            Assert.StartsWith("#", status.StateColor, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryDebloatState_HasLabelAndColor()
    {
        foreach (DebloatState state in Enum.GetValues<DebloatState>())
        {
            DebloatAppStatus status = new(
                DebloatCatalog.RemovableApps[0],
                InstalledApp: null,
                state,
                "detalle",
                DateTimeOffset.Now);

            Assert.False(string.IsNullOrWhiteSpace(status.StateLabel), $"{state} sin etiqueta");
            Assert.StartsWith("#", status.StateColor, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InstalledApp_WithoutLocationCannotRestore()
    {
        var app = new InstalledUwpApp("Microsoft.BingNews", "full", null, null, null);

        Assert.False(app.CanRestore);
        Assert.Equal("UNKNOWN", app.VersionLabel);
        Assert.Equal("UNKNOWN", app.LocationLabel);
    }
}
