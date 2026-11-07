namespace EvoOptimizer.Core.Tweaks;

/// <summary>
/// Definiciones concretas de tweaks de registro para el equipo objetivo
/// (i3-1220P + Intel UHD + 16 GB DDR5 + Windows 11). Cada entrada declara su
/// valor por defecto conocido; si Windows no informa el valor, el estado real
/// queda UNKNOWN y nunca se inventa.
///
/// Política: ninguna definición toca Defender, firewall, servicios ni valores
/// capaces de desactivarlos. <see cref="RegistryTweakCatalog.IsBlocked"/> se
/// aplica igualmente en tiempo de evaluación.
/// </summary>
public static class RegistryTweakDefinitions
{
    public static IReadOnlyList<RegistryTweakDefinition> All { get; } = Build();

    private static IReadOnlyList<RegistryTweakDefinition> Build()
    {
        var list = new List<RegistryTweakDefinition>();

        // ── GAMING / GameConfigStore ─────────────────────────────────────────
        list.Add(User(
            "GAME-DVR-ENABLED",
            "Desactivar Game DVR (GameConfigStore)",
            "Apaga la grabación en segundo plano que Windows mantiene para la Barra de juegos.",
            TweakCategory.Gaming,
            @"System\GameConfigStore",
            "GameDVR_Enabled",
            "0", "1",
            TweakRisk.Low,
            "Perdés la captura rápida hasta revertir el cambio.",
            "Elimina el costo de codificación en segundo plano mientras jugás."));

        list.Add(User(
            "GAME-FSE-BEHAVIOR",
            "Comportamiento de pantalla completa exclusiva",
            "Indica a DXGI cómo tratar el modo pantalla completa exclusiva en juegos.",
            TweakCategory.Gaming,
            @"System\GameConfigStore",
            "GameDVR_FSEBehaviorMode",
            "2", "0",
            TweakRisk.Medium,
            "Puede cambiar la forma en que algunos juegos entran en pantalla completa.",
            "Reduce la intervención del compositor en pantalla completa."));

        list.Add(User(
            "GAME-FSE-HONOR",
            "Respetar pantalla completa exclusiva del usuario",
            "Habilita que se respete la petición de FSE del juego.",
            TweakCategory.Gaming,
            @"System\GameConfigStore",
            "GameDVR_HonorUserFSEBehaviorMode",
            "1", "0",
            TweakRisk.Medium,
            "En iGPU puede aumentar el tearing si VRR no está disponible.",
            "Evita que Windows fuerce modo ventana optimizado."));

        list.Add(User(
            "GAME-DXGI-HONOR-FSE",
            "DXGI compatible con FSE",
            "Alinea DXGI con el modo pantalla completa exclusiva.",
            TweakCategory.Gaming,
            @"System\GameConfigStore",
            "GameDVR_DXGIHonorFSEWindowsCompatible",
            "1", "0",
            TweakRisk.Medium,
            "Afecta sólo a juegos que usan DXGI en FSE.",
            "Menos cambios de modo al entrar y salir del juego."));

        list.Add(User(
            "GAME-EFSE-FLAGS",
            "Flags de FSE (EFSE)",
            "Desactiva flags experimentales de pantalla completa.",
            TweakCategory.Gaming,
            @"System\GameConfigStore",
            "GameDVR_EFSEFeatureFlags",
            "0", "1",
            TweakRisk.Low,
            "Ninguno conocido; reversible en un paso.",
            "Comportamiento de presentación más predecible."));

        list.Add(User(
            "GAME-AUTO-GAME-MODE",
            "Modo Juego automático",
            "Habilita que Windows active el Modo Juego sin pedirlo.",
            TweakCategory.Gaming,
            @"Software\Microsoft\GameBar",
            "AutoGameModeEnabled",
            "1", "1",
            TweakRisk.Low,
            "Ninguno; sólo prioriza el juego en primer plano.",
            "El planificador prioriza el proceso del juego."));

        list.Add(User(
            "GAME-BAR-STARTUP-PANEL",
            "Ocultar panel de inicio de Game Bar",
            "Evita que la Barra de juegos muestre su panel al iniciar.",
            TweakCategory.Gaming,
            @"Software\Microsoft\GameBar",
            "ShowStartupPanel",
            "0", "1",
            TweakRisk.Low,
            "Ninguno.",
            "Menos procesos y ventanas al iniciar sesión."));

        list.Add(User(
            "GAME-BAR-NEXUS",
            "Desactivar Nexus de Game Bar",
            "Apaga el componente Nexus (widgets) de la Barra de juegos.",
            TweakCategory.Gaming,
            @"Software\Microsoft\GameBar",
            "UseNexusForGameBarEnabled",
            "0", "1",
            TweakRisk.Low,
            "Perdés los widgets de la Barra de juegos.",
            "Menos actividad en segundo plano durante la partida."));

        list.Add(Machine(
            "GPU-HAGS",
            "Hardware-accelerated GPU scheduling (HAGS)",
            "Activa la planificación de GPU acelerada por hardware.",
            TweakCategory.Gaming,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode",
            "2", "1",
            TweakRisk.Medium,
            "En Intel UHD puede no estar soportado; si no medís mejora, revertilo.",
            "Reduce latencia de presentación cuando el driver lo soporta.",
            requiresReboot: true,
            compatibility: "Windows 11 · driver WDDM 2.7+ (verificar soporte en Intel UHD)"));

        // ── FPS / LATENCIA (multimedia y planificador) ───────────────────────
        list.Add(Machine(
            "MM-SYSTEM-RESPONSIVENESS",
            "SystemResponsiveness (multimedia)",
            "Reserva menos CPU para tareas multimedia del sistema.",
            TweakCategory.Fps,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "SystemResponsiveness",
            "10", "20",
            TweakRisk.Medium,
            "Puede afectar reproducción de audio/video en segundo plano.",
            "Más CPU disponible para el juego en primer plano."));

        list.Add(Machine(
            "MM-NETWORK-THROTTLING",
            "NetworkThrottlingIndex",
            "Elimina el límite de throttling de red para multimedia.",
            TweakCategory.Network,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "NetworkThrottlingIndex",
            "4294967295", "10",
            TweakRisk.Medium,
            "Aumenta el uso de red de apps multimedia; reversible.",
            "Menos throttling de paquetes en juegos online."));

        list.Add(Machine(
            "MM-GAMES-GPU-PRIORITY",
            "Prioridad de GPU para juegos",
            "Sube la prioridad de GPU de la tarea Games.",
            TweakCategory.Fps,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
            "GPU Priority",
            "8", "1",
            TweakRisk.Medium,
            "Sólo afecta procesos clasificados como Games.",
            "La GPU atiende antes los frames del juego."));

        list.Add(Machine(
            "MM-GAMES-PRIORITY",
            "Prioridad de CPU para juegos",
            "Sube la prioridad de CPU de la tarea Games.",
            TweakCategory.Fps,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
            "Priority",
            "6", "1",
            TweakRisk.Medium,
            "Nunca equivale a High/Realtime del proceso; es la clase multimedia.",
            "Mejor respuesta del hilo de juego bajo carga."));

        list.Add(Machine(
            "MM-GAMES-SCHEDULING",
            "Categoría de planificación de juegos",
            "Mueve la tarea Games a la categoría High del planificador multimedia.",
            TweakCategory.Fps,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
            "Scheduling Category",
            "High", "Medium",
            TweakRisk.Medium,
            "Afecta sólo la clase multimedia Games.",
            "Menos cambios de contexto del proceso de juego.",
            kind: RegistryValueChoice.String));

        list.Add(Machine(
            "MM-GAMES-SFIO",
            "Prioridad SFIO de juegos",
            "Sube la prioridad de E/S de la tarea Games.",
            TweakCategory.Fps,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
            "SFIO Priority",
            "High", "Normal",
            TweakRisk.Medium,
            "Puede aumentar la actividad de disco de otros procesos en espera.",
            "Menos latencia de E/S al cargar mapas y assets.",
            kind: RegistryValueChoice.String));

        list.Add(Machine(
            "SCHED-PRIORITY-SEPARATION",
            "Win32PrioritySeparation (primer plano)",
            "Da más quantum al proceso en primer plano.",
            TweakCategory.Fps,
            @"SYSTEM\CurrentControlSet\Control\PriorityControl",
            "Win32PrioritySeparation",
            "38", "2",
            TweakRisk.Medium,
            "Los procesos en segundo plano se vuelven menos responsivos.",
            "El juego recibe quanta más largos y estables."));

        // ── INPUT ────────────────────────────────────────────────────────────
        list.Add(User(
            "INPUT-MOUSE-SPEED",
            "Desactivar aceleración de mouse",
            "MouseSpeed en 0 elimina la aceleración del puntero.",
            TweakCategory.Input,
            @"Control Panel\Mouse",
            "MouseSpeed",
            "0", "1",
            TweakRisk.Low,
            "Cambia la sensación del puntero hasta que te adaptes.",
            "Entrada 1:1 y consistente para apuntar.",
            kind: RegistryValueChoice.String));

        list.Add(User(
            "INPUT-MOUSE-THRESHOLD1",
            "Umbral de aceleración 1",
            "Primer umbral de aceleración del puntero.",
            TweakCategory.Input,
            @"Control Panel\Mouse",
            "MouseThreshold1",
            "0", "6",
            TweakRisk.Low,
            "Ninguno con MouseSpeed=0.",
            "Sin saltos de velocidad al mover el mouse.",
            kind: RegistryValueChoice.String));

        list.Add(User(
            "INPUT-MOUSE-THRESHOLD2",
            "Umbral de aceleración 2",
            "Segundo umbral de aceleración del puntero.",
            TweakCategory.Input,
            @"Control Panel\Mouse",
            "MouseThreshold2",
            "0", "10",
            TweakRisk.Low,
            "Ninguno con MouseSpeed=0.",
            "Consistencia de sensibility entre sesiones.",
            kind: RegistryValueChoice.String));

        list.Add(User(
            "INPUT-KEYBOARD-DELAY",
            "Retardo de repetición de teclado",
            "Minimiza el retardo antes de repetir una tecla.",
            TweakCategory.Input,
            @"Control Panel\Keyboard",
            "KeyboardDelay",
            "0", "1",
            TweakRisk.Low,
            "Ninguno.",
            "Respuesta más rápida al mantener teclas.",
            kind: RegistryValueChoice.String));

        list.Add(User(
            "INPUT-KEYBOARD-SPEED",
            "Velocidad de repetición de teclado",
            "Máxima velocidad de repetición soportada.",
            TweakCategory.Input,
            @"Control Panel\Keyboard",
            "KeyboardSpeed",
            "31", "31",
            TweakRisk.Low,
            "Ninguno.",
            "Menor latencia de entrada en movimiento.",
            kind: RegistryValueChoice.String));

        list.Add(Machine(
            "INPUT-MOUSE-QUEUE",
            "Cola de datos del mouse (mouclass)",
            "Reduce la cola del driver de mouse para bajar latencia.",
            TweakCategory.Input,
            @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters",
            "MouseDataQueueSize",
            "16", "100",
            TweakRisk.Medium,
            "Cola más chica puede perder eventos bajo carga extrema.",
            "Menos buffering entre el mouse y el juego.",
            requiresReboot: true));

        list.Add(Machine(
            "INPUT-KEYBOARD-QUEUE",
            "Cola de datos del teclado (kbdclass)",
            "Reduce la cola del driver de teclado.",
            TweakCategory.Input,
            @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters",
            "KeyboardDataQueueSize",
            "16", "100",
            TweakRisk.Medium,
            "Cola más chica puede perder pulsaciones bajo carga extrema.",
            "Menos buffering de pulsaciones.",
            requiresReboot: true));

        // ── VISUAL ───────────────────────────────────────────────────────────
        list.Add(User(
            "VISUAL-FX-PERFORMANCE",
            "Efectos visuales orientados al rendimiento",
            "VisualFXSetting=2 prioriza rendimiento sobre apariencia.",
            TweakCategory.Visual,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
            "VisualFXSetting",
            "2", "0",
            TweakRisk.Low,
            "La interfaz pierde animaciones y sombras.",
            "Menos trabajo de composición del DWM."));

        list.Add(User(
            "VISUAL-TRANSPARENCY",
            "Desactivar transparencia",
            "Apaga el acrílico/transparencias del shell.",
            TweakCategory.Visual,
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "EnableTransparency",
            "0", "1",
            TweakRisk.Low,
            "Estética menos pulida.",
            "Menos uso de GPU integrada en el escritorio."));

        list.Add(User(
            "VISUAL-TASKBAR-ANIMATIONS",
            "Desactivar animaciones de la barra de tareas",
            "Apaga animaciones de la barra de tareas.",
            TweakCategory.Visual,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            "TaskbarAnimations",
            "0", "1",
            TweakRisk.Low,
            "Ninguno.",
            "Shell más ágil al abrir y cerrar ventanas."));

        list.Add(User(
            "VISUAL-MIN-ANIMATE",
            "Desactivar animación al minimizar",
            "MinAnimate=0 elimina la animación de minimizado.",
            TweakCategory.Visual,
            @"Control Panel\Desktop\WindowMetrics",
            "MinAnimate",
            "0", "1",
            TweakRisk.Low,
            "Ninguno.",
            "Cambio de foco más rápido al minimizar el juego.",
            kind: RegistryValueChoice.String));

        list.Add(User(
            "VISUAL-DISALLOW-SHAKING",
            "Desactivar Aero Shake",
            "Evita que agitar una ventana minimice el resto.",
            TweakCategory.Visual,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            "DisallowShaking",
            "1", "0",
            TweakRisk.Low,
            "Perdés el gesto de Aero Shake.",
            "Menos recomposición accidental del escritorio."));

        list.Add(User(
            "VISUAL-MENU-SHOW-DELAY",
            "Retardo de menús",
            "MenuShowDelay en 0 muestra los menús al instante.",
            TweakCategory.Visual,
            @"Control Panel\Desktop",
            "MenuShowDelay",
            "0", "400",
            TweakRisk.Low,
            "Ninguno.",
            "Interfaz más responsiva.",
            kind: RegistryValueChoice.String));

        // ── ENERGÍA ──────────────────────────────────────────────────────────
        list.Add(Machine(
            "POWER-THROTTLING-OFF",
            "Desactivar power throttling",
            "Evita que Windows limite la frecuencia de hilos en segundo plano.",
            TweakCategory.Power,
            @"SYSTEM\CurrentControlSet\Control\Power",
            "PowerThrottlingOff",
            "1", "no existe",
            TweakRisk.Medium,
            "Mayor consumo y temperatura en portátiles.",
            "Hilos del juego y de red sin límites de frecuencia.",
            requiresReboot: true));

        list.Add(Machine(
            "POWER-FAST-STARTUP-OFF",
            "Desactivar inicio rápido",
            "Apaga HiberbootEnabled para un arranque limpio.",
            TweakCategory.Power,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Power",
            "HiberbootEnabled",
            "0", "1",
            TweakRisk.Low,
            "El arranque tarda unos segundos más.",
            "Evita estados de driver inconsistentes tras reinicios."));

        // ── MEMORIA ──────────────────────────────────────────────────────────
        list.Add(Machine(
            "MEM-DISABLE-PAGING-EXECUTIVE",
            "No paginar el executive del kernel",
            "Mantiene el executive en RAM física.",
            TweakCategory.Memory,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "DisablePagingExecutive",
            "1", "0",
            TweakRisk.Medium,
            "Con 16 GB es razonable; si tu RAM libre baja, revertilo. " +
            "EVO no desactiva la paginación del sistema.",
            "Menos fallos de página del kernel bajo carga.",
            requiresReboot: true));

        list.Add(Machine(
            "MEM-LARGE-SYSTEM-CACHE",
            "LargeSystemCache en 0",
            "Configuración recomendada para juegos en clientes Windows.",
            TweakCategory.Memory,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "LargeSystemCache",
            "0", "0",
            TweakRisk.Low,
            "Ninguno en equipos de escritorio.",
            "Evita que la caché del sistema compita con el juego.",
            requiresReboot: true));

        list.Add(Machine(
            "MEM-CLEAR-PAGEFILE-SHUTDOWN",
            "No borrar el archivo de paginación al apagar",
            "Acelera el apagado sin sacrificar seguridad en un equipo personal.",
            TweakCategory.Memory,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "ClearPageFileAtShutdown",
            "0", "0",
            TweakRisk.Low,
            "Ninguno para uso doméstico.",
            "Apagado más rápido.",
            requiresReboot: true));

        // ── RED ──────────────────────────────────────────────────────────────
        list.Add(Machine(
            "NET-TCP-ACK-FREQUENCY",
            "TCP AckFrequency",
            "Confirma paquetes TCP de inmediato en lugar de acumularlos.",
            TweakCategory.Network,
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
            "TcpAckFrequency",
            "1", "no existe (2)",
            TweakRisk.Medium,
            "Más ACKs en la red; reversible.",
            "Menor latencia de entrada en juegos online.",
            requiresReboot: true));

        list.Add(Machine(
            "NET-TCP-NODELAY",
            "TCP NoDelay",
            "Desactiva el algoritmo de Nagle para la pila TCP/IP.",
            TweakCategory.Network,
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
            "TCPNoDelay",
            "1", "no existe",
            TweakRisk.Medium,
            "Más paquetes pequeños en la red.",
            "Menos latencia de red en partidas competitivas.",
            requiresReboot: true));

        // ── ALMACENAMIENTO ───────────────────────────────────────────────────
        list.Add(Machine(
            "STORAGE-NTFS-8DOT3",
            "Desactivar nombres 8.3 en NTFS",
            "Evita crear nombres cortos legacy en volúmenes nuevos.",
            TweakCategory.Storage,
            @"SYSTEM\CurrentControlSet\Control\FileSystem",
            "NtfsDisable8dot3NameCreation",
            "1", "2",
            TweakRisk.Low,
            "Aplicaciones muy antiguas pueden requerir nombres 8.3.",
            "Menos metadatos por archivo en el SSD."));

        list.Add(Machine(
            "STORAGE-NTFS-LAST-ACCESS",
            "Desactivar actualización de último acceso",
            "No escribe timestamps de lectura en NTFS.",
            TweakCategory.Storage,
            @"SYSTEM\CurrentControlSet\Control\FileSystem",
            "NtfsDisableLastAccessUpdate",
            "1", "1",
            TweakRisk.Low,
            "Herramientas de backup que usan LastAccess pueden verse afectadas.",
            "Menos escrituras innecesarias en el NVMe."));

        // ── SISTEMA / EXPLORADOR / PRIVACIDAD ────────────────────────────────
        list.Add(User(
            "SYS-STARTUP-DELAY",
            "Eliminar retardo de aplicaciones de inicio",
            "StartupDelayInMSec=0 inicia las apps de inicio sin espera artificial.",
            TweakCategory.System,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize",
            "StartupDelayInMSec",
            "0", "no existe (10000)",
            TweakRisk.Low,
            "Más apps iniciando a la vez puede saturar el disco al arrancar.",
            "Inicio de sesión más rápido."));

        list.Add(User(
            "SYS-SEARCH-BING",
            "Desactivar búsqueda web en el menú Inicio",
            "El buscador local deja de consultar Bing.",
            TweakCategory.System,
            @"Software\Microsoft\Windows\CurrentVersion\Search",
            "BingSearchEnabled",
            "0", "1",
            TweakRisk.Low,
            "Perdés resultados web desde Inicio.",
            "Menos procesos de búsqueda y tráfico en segundo plano."));

        list.Add(User(
            "SYS-CORTANA-CONSENT",
            "Desactivar consentimiento de Cortana",
            "Apaga la asistencia de Cortana en la búsqueda.",
            TweakCategory.System,
            @"Software\Microsoft\Windows\CurrentVersion\Search",
            "CortanaConsent",
            "0", "1",
            TweakRisk.Low,
            "Cortana deja de responder.",
            "Menos componentes residentes."));

        list.Add(User(
            "SYS-SEARCH-HIGHLIGHTS",
            "Desactivar destacados de búsqueda",
            "Evita contenido sugerido en la barra de búsqueda.",
            TweakCategory.System,
            @"Software\Microsoft\Windows\CurrentVersion\Search",
            "SearchHighlights",
            "0", "1",
            TweakRisk.Low,
            "Ninguno.",
            "Menos red y render en el shell."));

        list.Add(User(
            "SYS-ADVERTISING-ID",
            "Desactivar ID de publicidad",
            "Evita que las apps usen un identificador publicitario.",
            TweakCategory.System,
            @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
            "Enabled",
            "0", "1",
            TweakRisk.Low,
            "Ninguno funcional.",
            "Menos telemetría de apps UWP."));

        list.Add(User(
            "SYS-CONTENT-DELIVERY-WELCOME",
            "Desactivar experiencia de bienvenida",
            "Apaga el contenido sugerido tras actualizaciones.",
            TweakCategory.System,
            @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
            "SubscribedContent-310093Enabled",
            "0", "1",
            TweakRisk.Low,
            "Ninguno.",
            "Menos procesos de contenido al iniciar sesión."));

        list.Add(User(
            "SYS-START-TRACKPROGS",
            "Desactivar seguimiento de apps iniciadas",
            "Windows deja de registrar qué apps abrís para mejorar Inicio.",
            TweakCategory.System,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            "Start_TrackProgs",
            "0", "1",
            TweakRisk.Low,
            "Inicio deja de mostrar apps frecuentes.",
            "Menos escritura de telemetría local."));

        list.Add(User(
            "SYS-WAITTOKILL-APP",
            "WaitToKillAppTimeout reducido",
            "Acorta la espera al cerrar aplicaciones colgadas.",
            TweakCategory.System,
            @"Control Panel\Desktop",
            "WaitToKillAppTimeout",
            "2000", "20000",
            TweakRisk.Low,
            "Una app con datos sin guardar podría cerrarse antes.",
            "Apagado y cierre de sesión más rápidos.",
            kind: RegistryValueChoice.String));

        list.Add(User(
            "SYS-HUNGAPP-TIMEOUT",
            "HungAppTimeout reducido",
            "Detecta antes las aplicaciones sin respuesta.",
            TweakCategory.System,
            @"Control Panel\Desktop",
            "HungAppTimeout",
            "1000", "5000",
            TweakRisk.Low,
            "Puede marcar como colgada una app legítimamente ocupada.",
            "Menos tiempo bloqueado esperando procesos.",
            kind: RegistryValueChoice.String));

        list.Add(User(
            "SYS-AUTOEND-TASKS",
            "AutoEndTasks al apagar",
            "Cierra automáticamente aplicaciones colgadas al apagar.",
            TweakCategory.System,
            @"Control Panel\Desktop",
            "AutoEndTasks",
            "1", "0",
            TweakRisk.Medium,
            "Puede cerrar una app con cambios sin guardar.",
            "Apagados sin diálogos interminables.",
            kind: RegistryValueChoice.String));

        list.Add(Machine(
            "SYS-SVCHOST-SPLIT",
            "SvcHostSplitThresholdInKB elevado",
            "Agrupar más servicios en un solo svchost reduce procesos.",
            TweakCategory.System,
            @"SYSTEM\CurrentControlSet\Control",
            "SvcHostSplitThresholdInKB",
            "16777216", "3670016",
            TweakRisk.Medium,
            "Cambia el agrupamiento de servicios; requiere reinicio y es reversible. " +
            "EVO no desactiva ningún servicio.",
            "Menos procesos svchost y menos memoria comprometida.",
            requiresReboot: true));

        return list;
    }

    private static RegistryTweakDefinition User(
        string id,
        string name,
        string description,
        TweakCategory category,
        string keyPath,
        string valueName,
        string desiredValue,
        string? defaultHint,
        TweakRisk risk,
        string riskNote,
        string expectedBenefit,
        bool requiresReboot = false,
        RegistryValueChoice kind = RegistryValueChoice.Dword,
        string compatibility = "Windows 10/11 · HKCU · sin elevación") =>
        new(
            id,
            name,
            description,
            category,
            RegistryHiveChoice.CurrentUser,
            keyPath,
            valueName,
            kind,
            desiredValue,
            defaultHint,
            risk,
            riskNote,
            expectedBenefit,
            requiresReboot,
            compatibility);

    private static RegistryTweakDefinition Machine(
        string id,
        string name,
        string description,
        TweakCategory category,
        string keyPath,
        string valueName,
        string desiredValue,
        string? defaultHint,
        TweakRisk risk,
        string riskNote,
        string expectedBenefit,
        bool requiresReboot = false,
        RegistryValueChoice kind = RegistryValueChoice.Dword,
        string compatibility = "Windows 10/11 · HKLM · requiere UAC") =>
        new(
            id,
            name,
            description,
            category,
            RegistryHiveChoice.LocalMachine,
            keyPath,
            valueName,
            kind,
            desiredValue,
            defaultHint,
            risk,
            riskNote,
            expectedBenefit,
            requiresReboot,
            compatibility);
}