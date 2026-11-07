namespace EvoOptimizer.Core.Tweaks;

/// <summary>
/// Catálogo puro de debloat. Contiene dos listas: aplicaciones que EVO ofrece
/// quitar (por prefijo real de <c>PackageFamilyName</c>) y prefijos protegidos
/// que nunca se ofrecen y nunca se quitan, aunque aparezcan en el escaneo.
/// Nada de aquí ejecuta PowerShell: el servicio de Infrastructure es el que
/// escanea y elimina.
/// </summary>
public static class DebloatCatalog
{
    /// <summary>
    /// Paquetes esenciales del sistema, de identidad, de seguridad o de
    /// runtime. Si un paquete coincide con alguno de estos prefijos su estado
    /// es <see cref="DebloatState.Protected"/> y la UI no muestra botón de quite.
    /// </summary>
    public static IReadOnlyList<string> ProtectedPrefixes { get; } =
    [
        "Microsoft.WindowsStore",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.VCLibs",
        "Microsoft.NET.Native",
        "Microsoft.UI.Xaml",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.Win32WebViewHost",
        "Microsoft.WebView2",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "Microsoft.CredDialogHost",
        "Microsoft.BioEnrollment",
        "Microsoft.LockApp",
        "Microsoft.SysMgr",
        "Microsoft.Windows.AssignedAccessLockApp",
        "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.Windows.ContentDeliveryManager",
        "Microsoft.Windows.CrossDevice",
        "MicrosoftMicrosoft.CrossDevice",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.Search",
        "Microsoft.Windows.CShellHotkeyExperience",
        "Microsoft.Windows.HolographicDevice",
        "Microsoft.Windows.Production",
        "Microsoft.Windows.SecureAssessmentBrowser",
        "Microsoft.Windows.SystemCompatible",
        "Microsoft.Windows.ExperiencePack",
        "Microsoft.Windows.QuickAssist",
        "Microsoft.Windows.RemoteDesktop",
        "Microsoft.WindowsTerminal",
        "Microsoft.MicrosoftEdge",
        "Microsoft.MicrosoftEdge.Stable",
        "Microsoft.SecHealthUI",
        "Microsoft.Windows.Defender",
        "Microsoft.Windows.Defender.log",
        "Microsoft.Windows.Firewall",
        "Microsoft.Windows.NetworkDevice",
        "Microsoft.Xbox.IdentityProvider",
        "Microsoft.XboxIdentityProvider",
        "Microsoft.GamingServices",
        "Microsoft.OOBE.NetworkCheck",
        "Microsoft.OOBE.SoftwareProtection",
        "Microsoft.NET.Native.Framework",
        "Microsoft.Services.Store",
        "Microsoft.Windows.Communications",
        "Microsoft.Windows.CommunicationsCall"
    ];

    /// <summary>Aplicaciones que EVO ofrece quitar del usuario actual.</summary>
    public static IReadOnlyList<DebloatAppDefinition> RemovableApps { get; } = BuildApps();

    public static bool IsProtected(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return true;

        return ProtectedPrefixes.Any(prefix =>
            packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Cruza el escaneo real con el catálogo. Un prefijo protegido gana siempre,
    /// incluso si una definición futura lo lista como eliminable.
    /// </summary>
    public static IReadOnlyList<DebloatAppStatus> Match(
        IReadOnlyList<InstalledUwpApp> apps,
        DateTimeOffset? scannedAt)
    {
        ArgumentNullException.ThrowIfNull(apps);

        var byName = new Dictionary<string, InstalledUwpApp>(StringComparer.OrdinalIgnoreCase);

        foreach (InstalledUwpApp app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.PackageName))
                continue;

            if (!byName.ContainsKey(app.PackageName))
                byName.Add(app.PackageName, app);
        }

        var statuses = new List<DebloatAppStatus>(RemovableApps.Count);

        foreach (DebloatAppDefinition definition in RemovableApps)
        {
            byName.TryGetValue(definition.NamePrefix, out InstalledUwpApp? found);
            statuses.Add(Evaluate(definition, found, hasActiveChange: false, scannedAt));
        }

        return statuses;
    }

    /// <summary>
    /// Estado de una definición. <paramref name="scannedAt"/> nulo significa que
    /// todavía no hubo escaneo: el resultado es UNKNOWN, no "no instalada".
    /// </summary>
    public static DebloatAppStatus Evaluate(
        DebloatAppDefinition definition,
        InstalledUwpApp? installedApp,
        bool hasActiveChange,
        DateTimeOffset? scannedAt)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (scannedAt is null)
        {
            return new DebloatAppStatus(
                definition,
                installedApp,
                DebloatState.NotScanned,
                "Sin escaneo previo. EVO no asume qué apps están instaladas.",
                null);
        }

        if (installedApp is null)
        {
            return new DebloatAppStatus(
                definition,
                null,
                DebloatState.NotInstalled,
                "No aparece en Get-AppxPackage para este usuario. No hay nada que quitar.",
                scannedAt);
        }

        if (IsProtected(installedApp.PackageName))
        {
            return new DebloatAppStatus(
                definition,
                installedApp,
                DebloatState.Protected,
                "Paquete protegido por política EVO: es componente de sistema, seguridad o runtime.",
                scannedAt);
        }

        return hasActiveChange
            ? new DebloatAppStatus(
                definition,
                installedApp,
                DebloatState.Removed,
                $"Quitada por EVO ({installedApp.PackageFullName}). Se puede volver a registrar.",
                scannedAt)
            : new DebloatAppStatus(
                definition,
                installedApp,
                DebloatState.Installed,
                $"Instalada: {installedApp.PackageFullName}. EVO puede quitarla para este usuario.",
                scannedAt);
    }

    /// <summary>
    /// Devuelve el estado de un paquete detectado que no está en el catálogo de
    /// eliminables: se muestra como protegido o como no listado, nunca como
    /// candidato a quite automático.
    /// </summary>
    public static DebloatAppStatus EvaluateUnlisted(
        InstalledUwpApp app,
        DateTimeOffset? scannedAt)
    {
        ArgumentNullException.ThrowIfNull(app);

        DebloatAppDefinition definition = new(
            (IsProtected(app.PackageName) ? "PROTECTED-" : "UNLISTED-") + app.PackageName,
            app.PackageName,
            app.PackageName,
            IsProtected(app.PackageName)
                ? "Componente esencial del sistema o del runtime."
                : "Paquete no catalogado por EVO.",
            TweakRisk.High,
            IsProtected(app.PackageName)
                ? "Ninguna: no se ofrece quitar."
                : "No se ofrece quite automático.",
            "No aplica.");

        return new DebloatAppStatus(
            definition,
            app,
            DebloatState.Protected,
            IsProtected(app.PackageName)
                ? "Paquete protegido por política EVO."
                : "EVO no quita apps que no estén en su catálogo verificado.",
            scannedAt);
    }

    private static List<DebloatAppDefinition> BuildApps()
    {
        var apps = new List<DebloatAppDefinition>();

        // ── Servicios de Xbox que no intervienen en el juego local ───────────
        Add(apps,
            "Xbox Console Companion",
            "Microsoft.XboxApp",
            "App complementaria para vincular la consola. No participa en el juego local.",
            TweakRisk.Low,
            "Menos paquetes en segundo plano y menos inicio diferido de su proceso.",
            "Microsoft Store → «Xbox Console Companion», o Add-AppxPackage -Register con su AppxManifest.");

        Add(apps,
            "Xbox Speech To Text Overlay",
            "Microsoft.XboxSpeechToTextOverlay",
            "Transcripción del chat de voz de Xbox. Inútil sin cuenta Xbox Live.",
            TweakRisk.Low,
            "Quita un overlay de voz y su carga de memoria.",
            "Microsoft Store → «Xbox Speech to Text Overlay».");

        Add(apps,
            "Xbox Game Overlay",
            "Microsoft.XboxGameOverlay",
            "Overlay antiguo de Xbox, reemplazado por XboxGamingOverlay.",
            TweakRisk.Medium,
            "Menos procesos de overlay superpuestos al juego.",
            "Microsoft Store → «Xbox Game Overlay».");

        Add(apps,
            "Xbox TCUI",
            "Microsoft.Xbox.TCUI",
            "Diálogos de cuenta Xbox. Game Bar puede volver a pedirlo.",
            TweakRisk.Medium,
            "Reduce servicios de cuenta Xbox residentes.",
            "Microsoft Store → «Xbox TCUI».");

        Add(apps,
            "Xbox Game Bar (XboxGamingOverlay)",
            "Microsoft.XboxGamingOverlay",
            "Barra de juegos Win+G. EVO mide con PresentMon, no con Game Bar, pero Windows la usa para captura y Game DVR.",
            TweakRisk.Medium,
            "Elimina el agente de overlay que puede presentarse sobre CS2.",
            "Microsoft Store → «Xbox Game Bar» o Add-AppxPackage -Register con el AppxManifest.");

        Add(apps,
            "App Xbox moderna",
            "Microsoft.GamingApp",
            "Aplicación Xbox de Windows. No es necesaria para jugar en PC local.",
            TweakRisk.Medium,
            "Quita un servicio de inicio, su WebView2 y su agente de notificaciones.",
            "Microsoft Store → «Xbox».");

        // ── Apps de Microsoft precargadas ────────────────────────────────────
        Add(apps,
            "Microsoft Teams (chat personal)",
            "MicrosoftTeams",
            "Chat de Teams precargado en Windows 11. No es el Teams de trabajo instalado por separado.",
            TweakRisk.Medium,
            "Menos proceso de inicio y menos consumo de red en segundo plano.",
            "Microsoft Store → «Microsoft Teams». Puede volver con ciertas actualizaciones.");

        Add(apps,
            "Clipchamp",
            "Clipchamp.Clipchamp",
            "Editor de video precargado, no usado para jugar.",
            TweakRisk.Low,
            "Libera espacio y elimina su servicio de inicio.",
            "Microsoft Store → «Clipchamp».");

        Add(apps,
            "Outlook (mail precargado)",
            "Microsoft.OutlookForWindows",
            "Cliente de correo nuevo de Windows 11. Innecesario si usás webmail.",
            TweakRisk.Low,
            "Quita un agente de notificaciones y su proceso residente.",
            "Microsoft Store → «Outlook for Windows».");

        Add(apps,
            "Skype",
            "Microsoft.SkypeApp",
            "Cliente de VoIP precargado y con servicio de inicio.",
            TweakRisk.Low,
            "Menos procesos de red en segundo plano.",
            "Microsoft Store → «Skype».");

        Add(apps,
            "Copilot",
            "Microsoft.Copilot",
            "App de IA precargada. No interviene en el juego y se levanta al iniciar sesión.",
            TweakRisk.Medium,
            "Elimina un WebView2 residente y su servicio de inicio.",
            "Microsoft Store → «Copilot».");

        Add(apps,
            "Cortana",
            "Microsoft.549981C3F5F1",
            "Asistente de voz discontinuado por Microsoft; sigue registrando tareas de fondo.",
            TweakRisk.Medium,
            "Quita micrófono residente y una tarea de índice de voz.",
            "Microsoft Store → «Cortana».");

        Add(apps,
            "Mixed Reality Portal",
            "Microsoft.MixedReality.Portal",
            "Innecesario sin visor de realidad mixta.",
            TweakRisk.Low,
            "Libera espacio y desactiva su detección de sensores.",
            "Microsoft Store → «Windows Mixed Reality Portal».");

        Add(apps,
            "People",
            "Microsoft.People",
            "Contactos del sistema; no aporta al juego.",
            TweakRisk.Low,
            "Menos sincronización de contactos en segundo plano.",
            "Microsoft Store → «Microsoft People».");

        Add(apps,
            "Your Phone / Phone Link",
            "Microsoft.YourPhone",
            "Vinculación con el teléfono. Consume servicio de inicio y notificaciones.",
            TweakRisk.Medium,
            "Quita el servicio PhoneLink y su agente de notificaciones.",
            "Microsoft Store → «Tu teléfono» / «Phone Link».");

        Add(apps,
            "Get Help",
            "Microsoft.GetHelp",
            "Soporte guiado con chat remoto; no se usa para jugar.",
            TweakRisk.Low,
            "Reduce paquetes de inicio.",
            "Microsoft Store → «Obtener ayuda».");

        Add(apps,
            "Get Started / Consejos",
            "Microsoft.Getstarted",
            "Consejos y sugerencias de Windows.",
            TweakRisk.Low,
            "Quita sugerencias que Windows reactiva en segundo plano.",
            "Microsoft Store → «Consejos de Windows».");

        Add(apps,
            "Bing News",
            "Microsoft.BingNews",
            "Noticias con widgets que se actualizan solas.",
            TweakRisk.Low,
            "Menos red y menos WebView2 en segundo plano.",
            "Microsoft Store → «Bing News / Widgets».");

        Add(apps,
            "Bing Weather",
            "Microsoft.BingWeather",
            "Clima precargado, con tile y notificaciones.",
            TweakRisk.Low,
            "Elimina una app de red residente.",
            "Microsoft Store → «Bing Weather».");

        Add(apps,
            "Solitaire Collection",
            "Microsoft.MicrosoftSolitaireCollection",
            "Juego de cartas con publicidad y servicios de cuenta.",
            TweakRisk.Low,
            "Libera espacio y quita un paquete con telemetría publicitaria.",
            "Microsoft Store → «Microsoft Solitaire Collection».");

        Add(apps,
            "Windows Maps",
            "Microsoft.WindowsMaps",
            "Mapas con servicios de ubicación en segundo plano.",
            TweakRisk.Low,
            "Quita uso de ubicación y su caché de mapas.",
            "Microsoft Store → «Maps».");

        Add(apps,
            "Movies & TV (ZuneVideo)",
            "Microsoft.ZuneVideo",
            "Alquiler y reproducción de video; innecesario si ya tenés otro reproductor.",
            TweakRisk.Low,
            "Reduce paquetes multimedia de inicio.",
            "Microsoft Store → «Movies & TV».");

        Add(apps,
            "Groove Music (ZuneMusic)",
            "Microsoft.ZuneMusic",
            "Reproductor de música precargado.",
            TweakRisk.Low,
            "Reduce paquetes multimedia de inicio.",
            "Microsoft Store → «Groove Music».");

        Add(apps,
            "Feedback Hub",
            "Microsoft.WindowsFeedbackHub",
            "Envío de comentarios a Microsoft; sube telemetría del sistema.",
            TweakRisk.Low,
            "Quita el envío de feedback desde este usuario.",
            "Microsoft Store → «Feedback Hub».");

        Add(apps,
            "Advertising SDK (Xaml)",
            "Microsoft.Advertising.Xaml",
            "Marco publicitario que usan apps de terceros para mostrar anuncios.",
            TweakRisk.Medium,
            "Elimina la fuente de anuncios dentro de apps que lo usan.",
            "Vuelve solo si reinstalás una app que lo declare como dependencia.");

        Add(apps,
            "Amazon",
            "Amazon.com.Amazon",
            "Atajo de tienda precargado por OEM.",
            TweakRisk.Low,
            "Reduce paquetes inútiles.",
            "Microsoft Store → «Amazon Shopping».");

        Add(apps,
            "Instagram",
            "Facebook.InstagramBeta",
            "Atajo de Instagram precargado.",
            TweakRisk.Low,
            "Reduce paquetes inútiles.",
            "Microsoft Store → «Instagram».");

        Add(apps,
            "Facebook",
            "Facebook.Facebook",
            "Atajo de Facebook precargado.",
            TweakRisk.Low,
            "Reduce paquetes inútiles.",
            "Microsoft Store → «Facebook».");

        Add(apps,
            "Spotify (Store)",
            "SPotifyAB.SpotifyMusic",
            "Client Store de Spotify; si usás el cliente de escritorio o la web, sobra.",
            TweakRisk.Low,
            "Reduce paquetes y notificaciones duplicadas.",
            "Microsoft Store → «Spotify Music».");

        Add(apps,
            "Adobe Photoshop Express",
            "AdobeSystemsIncorporated.AdobePhotoshopExpress",
            "Edición de fotos con cuenta Adobe, precargada por OEM.",
            TweakRisk.Low,
            "Reduce paquetes inútiles.",
            "Microsoft Store → «Adobe Photoshop Express».");

        Add(apps,
            "Flipboard",
            "Flipboard.Flipboard",
            "Revista de noticias precargada por OEM.",
            TweakRisk.Low,
            "Reduce paquetes inútiles.",
            "Microsoft Store → «Flipboard».");

        Add(apps,
            "LinkedIn",
            "LinkedIn Corporation.LinkedInForWin8",
            "Red social precargada; no aporta al juego.",
            TweakRisk.Low,
            "Reduce paquetes inútiles.",
            "Microsoft Store → «LinkedIn».");

        Add(apps,
            "TikTok",
            "BytedancePte.Ltd.TikTok",
            "Atajo de TikTok precargado.",
            TweakRisk.Low,
            "Reduce paquetes inútiles.",
            "Microsoft Store → «TikTok».");

        return apps;
    }

    private static void Add(
        List<DebloatAppDefinition> apps,
        string displayName,
        string namePrefix,
        string reason,
        TweakRisk risk,
        string expectedBenefit,
        string reinstallHint)
    {
        apps.Add(new DebloatAppDefinition(
            $"DEBLOAT-{namePrefix.ToUpperInvariant().Replace(" ", "_").Replace(".", "_")}",
            namePrefix,
            displayName,
            reason,
            risk,
            expectedBenefit,
            reinstallHint));
    }
}
