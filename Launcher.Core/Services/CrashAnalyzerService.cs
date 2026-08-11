using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.App.Services;

public static class CrashAnalyzerService
{
    // Общая формулировка для launch_classpath: ставится в результат, если причину не удалось сузить.
    private const string ClasspathGenericSummary =
        "Не загрузился класс запуска Forge — установка неполная: часть файлов клиента или Forge не докачалась. " +
        "Нажми «Проверить файлы» в настройках лаунчера, он доустановит недостающее.";

    private const string ClasspathNonAsciiSummary =
        "Не загрузился класс запуска Forge. В пути установки есть не-ASCII символы (кириллица), а Java с таким путём " +
        "часто не находит свои библиотеки. Переустанови сборку в путь без кириллицы, например C:\\BL-modern.";

    private const string ClasspathBlockedSummary =
        "Не загрузился класс запуска Forge — файлы клиента не докачались, потому что с этого компьютера не открываются " +
        "серверы Mojang (блокировка провайдера, DNS или фильтр антивируса). Смени DNS на 1.1.1.1 или 8.8.8.8 либо " +
        "включи VPN и нажми «Проверить файлы» в настройках лаунчера.";

    private static readonly (string Category, string[] Needles, string Summary)[] KnownPatterns =
    [
        ("launch_classpath",
        [
            "Could not find or load main class",
            "cpw.mods.bootstraplauncher"
        ],
        // Текст зависит от того, есть ли в пути установки не-ASCII символы: см. RefineClasspathSummary.
        // Раньше здесь безусловно говорилось про кириллицу, и игроки с путём вида C:\Games бесконечно
        // переставляли сборку, хотя причина была в недокачанных файлах клиента.
        ClasspathGenericSummary),

        ("graphics_glfw",
        [
            "Failed to initialize GLFW"
        ],
        "Не удалось инициализировать графику (GLFW). Обнови драйвер видеокарты и отключи оверлеи (Afterburner/RTSS, Discord, NVIDIA)."),

        ("forge_incomplete",
        [
            "Invalid paths argument, contained no existing paths"
        ],
        "Неполная установка Forge — отсутствуют пропатченные файлы клиента (client-srg/extra, forge-client). Нажми «Проверить файлы» в настройках лаунчера: он доустановит Forge заново."),

        ("native_oom",
        [
            "OutOfMemoryError",
            "org.lwjgl.system.MemoryUtil"
        ],
        "Не хватило нативной (off-heap) памяти на текстуры/LWJGL. Это бывает, когда выделено слишком много памяти под игру. УМЕНЬШИ память в настройках лаунчера до 6 ГБ — большой -Xmx оставляет мало места драйверу и текстурам."),

        ("graphics_driver",
        [
            "nvoglv64.dll"
        ],
        "Краш драйвера видеокарты NVIDIA (nvoglv64.dll). Что делать: 1) обнови драйвер с nvidia.com (не через «Диспетчер устройств»); 2) в «Панель управления NVIDIA» → «Управление параметрами 3D» назначь javaw.exe на видеокарту NVIDIA; 3) попробуй запуск без шейдеров; 4) если выделено много памяти (8+ ГБ) — снизь до 6 ГБ."),

        ("graphics_driver",
        [
            "atio6axx.dll"
        ],
        "Краш драйвера видеокарты AMD (atio6axx.dll). Что делать: 1) обнови драйвер с amd.com; 2) попробуй запуск без шейдеров; 3) если выделено много памяти (8+ ГБ) — снизь до 6 ГБ."),

        ("graphics_driver",
        [
            "ig75icd64.dll"
        ],
        "Краш ВСТРОЕННОЙ видеокарты Intel (ig75icd64.dll). На ноутбуке игра идёт на встроенной Intel вместо дискретной. Что делать: 1) ОБЯЗАТЕЛЬНО назначь javaw.exe на дискретную видеокарту: Параметры Windows → Система → Дисплей → Графика → Обзор → javaw.exe → «Высокая производительность»; 2) обнови драйвер Intel; 3) попробуй без шейдеров."),

        ("graphics_driver",
        [
            "igxelpicd64.dll"
        ],
        "Краш встроенной видеокарты Intel (igxelpicd64.dll). На ноутбуке назначь javaw.exe на дискретную видеокарту (Параметры Windows → Система → Дисплей → Графика → javaw.exe → «Высокая производительность»), обнови драйвер Intel, попробуй без шейдеров."),

        ("graphics_driver",
        [
            "igdml64.dll"
        ],
        "Краш встроенной видеокарты Intel (igdml64.dll). На ноутбуке назначь javaw.exe на дискретную видеокарту (Параметры Windows → Система → Дисплей → Графика), обнови драйвер Intel, попробуй без шейдеров."),

        // Реальные имена из hs_err в саппорт-логах: помимо ig75icd64 встречается ig12icd64.
        ("graphics_driver",
        [
            "ig12icd64.dll"
        ],
        "Краш встроенной видеокарты Intel (ig12icd64.dll). На ноутбуке назначь javaw.exe на дискретную видеокарту (Параметры Windows → Система → Дисплей → Графика → Обзор → javaw.exe → «Высокая производительность»), обнови драйвер Intel, попробуй без шейдеров."),

        ("graphics_driver",
        [
            "igdumdim64.dll"
        ],
        "Краш встроенной видеокарты Intel (igdumdim64.dll). На ноутбуке назначь javaw.exe на дискретную видеокарту (Параметры Windows → Система → Дисплей → Графика), обнови драйвер Intel, попробуй без шейдеров."),

        // ───────────────────────────────────────────────────────────────────────────────
        //  LINUX и macOS. Windows-паттерны выше ловят имена .dll, которых на этих системах
        //  нет вовсе, поэтому списки можно держать вместе — пересечься они не могут.
        //  Тексты советов тоже свои: «назначь javaw.exe на дискретную видеокарту» на Linux
        //  бессмысленно, там переключение идёт через prime-run/DRI_PRIME.
        // ───────────────────────────────────────────────────────────────────────────────

        ("graphics_driver",
        [
            "libnvidia-glcore.so"
        ],
        "Краш драйвера NVIDIA на Linux (libnvidia-glcore.so). Что делать: 1) обнови проприетарный драйвер NVIDIA средствами своего дистрибутива; 2) если ноутбук с двумя видеокартами — запусти лаунчер через prime-run или с DRI_PRIME=1; 3) попробуй без шейдеров; 4) если выделено 8+ ГБ памяти — снизь до 6 ГБ."),

        ("graphics_driver",
        [
            "libGLX_nvidia.so"
        ],
        "Ошибка драйвера NVIDIA на Linux (libGLX_nvidia.so). Обнови драйвер NVIDIA, проверь, что установлены пакеты libnvidia-gl нужной разрядности, и попробуй запуск без шейдеров."),

        ("graphics_driver",
        [
            "radeonsi_dri.so"
        ],
        "Краш видеодрайвера AMD на Linux (Mesa, radeonsi). Что делать: обнови пакет mesa средствами дистрибутива, попробуй без шейдеров, а при выделении 8+ ГБ памяти снизь до 6 ГБ."),

        ("graphics_driver",
        [
            "iris_dri.so"
        ],
        "Краш встроенной видеокарты Intel на Linux (Mesa, iris). Обнови пакет mesa, попробуй без шейдеров. Если в ноутбуке есть дискретная видеокарта — запускай через prime-run или DRI_PRIME=1."),

        // swrast = программный рендеринг: игра идёт на процессоре, потому что драйвер не подхватился.
        // Формально это не краш, но 5 FPS игрок воспринимает как поломку.
        ("graphics_software_render",
        [
            "swrast"
        ],
        "Графика идёт через программный рендеринг (swrast), то есть на процессоре вместо видеокарты — отсюда очень низкий FPS. Установи драйверы видеокарты: для AMD/Intel это пакет mesa, для NVIDIA — проприетарный драйвер дистрибутива."),

        ("graphics_display",
        [
            "X11: Failed to open display"
        ],
        "Не удалось открыть графический дисплей (X11). Обычно это запуск без графической сессии — например, по SSH или из-под другого пользователя. Запусти лаунчер из своего рабочего стола."),

        ("graphics_display",
        [
            "Wayland",
            "GLFW"
        ],
        "Не удалось создать окно в Wayland. Что делать: 1) поставь пакеты xwayland и libdecor; 2) как временное решение запусти сессию Xorg вместо Wayland; 3) обнови драйверы видеокарты (mesa или драйвер NVIDIA)."),

        // macOS: без -XstartOnFirstThread GLFW не создаёт окно. Лаунчер этот ключ добавляет сам,
        // поэтому сообщение означает, что игру запустили мимо лаунчера или ключ кто-то затёр
        // в пользовательских аргументах JVM.
        ("macos_first_thread",
        [
            "XstartOnFirstThread"
        ],
        "На macOS игре нужен ключ запуска -XstartOnFirstThread, иначе окно не создаётся. Лаунчер добавляет его сам — проверь, не переопределены ли аргументы JVM в настройках лаунчера, и убери оттуда свои значения."),

        ("java_wrong_arch",
        [
            "no suitable image found",
            "mach-o"
        ],
        "Java не подходит по архитектуре процессора (например, x64 вместо arm64 на Apple Silicon). Очисти путь к Java в настройках лаунчера — он скачает подходящую сам."),

        // Самая частая причина крашей в саппорт-логах (136 бандлов за июль 2026, сборки 0.13.3+):
        // баг мода TooManyRecipeViewers — NPE при обращении к JEI-плагинам, обычно по клику мышью.
        ("mod_recipe_viewer",
        [
            "JEIPluginManager.onRuntimeUnavailable"
        ],
        "Известный баг мода просмотра рецептов (TooManyRecipeViewers/JEI) — краш при клике в интерфейсе рецептов. Это баг мода в сборке, не лаунчера. Что делать: не открывай просмотр рецептов до обновления сборки и сообщи в поддержку — исправление приедет с обновлением сборки."),

        ("mod_minimap",
        [
            "Xaero's Minimap",
            "has crashed"
        ],
        "Краш мода карты (Xaero's Minimap). Что делать: удали папку xaero из папки сборки (карты потеряются) или отключи мод карты, затем запусти игру заново."),

        // Найдено в саппорт-логах 01.08.2026: краш звукового движка через миксин Sound Physics Remastered.
        ("mod_sound_physics",
        [
            "Tried to release unknown channel"
        ],
        "Краш звукового движка игры (мод Sound Physics Remastered). Это баг мода в сборке, не лаунчера. Что делать: попробуй запустить игру заново, а если повторяется — снизь качество звука в настройках игры и сообщи в поддержку."),

        // Найдено в саппорт-логах 01.08.2026: NPE в вагонетках Create (контроллер без привязанной вагонетки).
        ("mod_create_minecart",
        [
            "MinecartController.cart()"
        ],
        "Краш мода Create на вагонетке — известный баг мода, не лаунчера. Что делать: запусти игру заново; если краш повторяется при заходе в мир, разбери вагонетку в этом месте или сообщи в поддержку."),

        ("java_heap_oom",
        [
            "OutOfMemoryError",
            "Java heap space"
        ],
        "Игре НЕ ХВАТИЛО выделенной памяти (Java heap). Зайди в Настройки лаунчера → «Память» и увеличь до 6–8 ГБ (если на ПК столько ОЗУ есть). На сборках с шейдерами дефолтных 4 ГБ часто мало — это частая причина вылетов."),

        ("out_of_memory",
        [
            "OutOfMemoryError"
        ],
        "Не хватило памяти. Если это обычный вылет — увеличь память в Настройках лаунчера; если краш драйвера/текстур (видеодрайвер) — наоборот, снизь до 6 ГБ."),

        ("java_version",
        [
            "UnsupportedClassVersionError",
            "has been compiled by a more recent version"
        ],
        "Неверная версия Java. Для этой сборки нужна Java 17."),

        ("mod_dependency",
        [
            "Missing or unsupported mandatory dependencies",
            "Missing mandatory dependencies",
            "ModLoadingException"
        ],
        "Конфликт модов или зависимостей. Обычно помогает проверка файлов сборки."),

        ("language_provider",
        [
            "needs language provider"
        ],
        "Не хватает загрузчика или зависимости для одного из модов."),

        ("missing_file",
        [
            "FileNotFoundException",
            "NoSuchFileException",
            "Failed to download file"
        ],
        "Не найден или не скачался нужный файл. Нужна проверка файлов сборки."),

        ("forge_version_mismatch",
        [
            "Actual version:",
            "Expected range:",
            "forge"
        ],
        "Версия Forge не подходит одному из модов."),

        ("graphics_driver",
        [
            "Pixel format not accelerated",
            "OpenGL"
        ],
        "Проблема с видеодрайвером или OpenGL. Обнови драйвер видеокарты."),

        ("init_crash",
        [
            "The game crashed whilst initializing game",
            "Rendering overlay"
        ],
        "Краш на инициализации клиента. Чаще всего это конфликт модов, ресурсов или клиентских настроек."),

        ("module_resolution",
        [
            "java.lang.module.ResolutionException",
            "export package"
        ],
        "Конфликт модулей Java между модами. Обычно это дубли или несовместимые jar-файлы.")
    ];

    public static CrashAnalysisResult Analyze(string installRoot, int exitCode)
    {
        var latestLogPath = Path.Combine(installRoot, "logs", "latest.log");
        var stderrLogPath = Path.Combine(installRoot, "logs", "minecraft-stderr.log");
        var crashReportPath = FindNewestCrashReport(installRoot);
        var hsErrPath = FindNewestHsErr(installRoot);
        var logText = ReadTail(latestLogPath);
        var stderrText = ReadTail(stderrLogPath);
        var crashText = ReadTail(crashReportPath);
        var hsErrText = ReadTail(hsErrPath);
        var combined = $"{logText}{Environment.NewLine}{stderrText}{Environment.NewLine}{crashText}{Environment.NewLine}{hsErrText}";

        // Крэш-репорт — показание о том, что игру УБИЛО, а latest.log копит ошибки за всю сессию (тот же
        // TooManyRecipeViewers сыплет NPE, не роняя игру). Поэтому сначала разбираем сам крэш-репорт и
        // только если он молчит — весь остальной текст. Иначе причина краша подменяется чужим шумом.
        var finding = DetectFinding(crashText, exitCode, allowFallback: false)
                      ?? DetectFinding(combined, exitCode, allowFallback: true)!;
        var summary = RefineClasspathSummary(finding, installRoot);
        var details = BuildDetails(exitCode, summary, latestLogPath, crashReportPath);

        return new CrashAnalysisResult(
            summary,
            details,
            latestLogPath,
            crashReportPath,
            finding.Category,
            finding.Signature,
            finding.Evidence,
            !string.IsNullOrWhiteSpace(crashReportPath),
            DescribeExitCode(exitCode),
            "0x" + unchecked((uint)exitCode).ToString("X8"),
            TelemetrySanitizer.SanitizeMultiline(combined, 2000),
            !string.IsNullOrWhiteSpace(hsErrPath));
    }

    /// <summary>
    /// «Could not find or load main class» — симптом, а не причина. Причин три: не-ASCII в пути,
    /// недокачанные файлы клиента/Forge и блокировка серверов Mojang (частный случай второго).
    /// Раньше игроку безусловно показывали версию про кириллицу, и человек с путём C:\Games по кругу
    /// переставлял сборку. Здесь диагноз сужается по фактам: сам путь установки и лог лаунчера.
    /// </summary>
    private static string RefineClasspathSummary(CrashFinding finding, string installRoot)
    {
        if (!string.Equals(finding.Category, "launch_classpath", StringComparison.Ordinal))
        {
            return finding.Summary;
        }

        if (HasNonAscii(installRoot))
        {
            return ClasspathNonAsciiSummary;
        }

        return LauncherLogShowsMojangBlock(installRoot)
            ? ClasspathBlockedSummary
            : ClasspathGenericSummary;
    }

    private static bool HasNonAscii(string? value) =>
        !string.IsNullOrEmpty(value) && value.Any(character => character > '\u007F');

    // Если установка ванильных файлов падала по DNS/сети на серверах Mojang, это записано в логе
    // лаунчера рядом с полным текстом исключения. Смотрим самый свежий лог.
    private static bool LauncherLogShowsMojangBlock(string installRoot)
    {
        try
        {
            var logRoot = Path.Combine(installRoot, ".launcher", "logs");
            if (!Directory.Exists(logRoot))
            {
                return false;
            }

            var newest = new DirectoryInfo(logRoot)
                .GetFiles("launcher-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                return false;
            }

            var text = ReadTail(newest.FullName);
            return MojangDownloadHosts.Any(host => text.Contains(host, StringComparison.OrdinalIgnoreCase))
                   && NetworkFailureMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static readonly string[] MojangDownloadHosts =
    [
        "piston-meta.mojang.com",
        "piston-data.mojang.com",
        "launchermeta.mojang.com",
        "libraries.minecraft.net",
        "resources.download.minecraft.net"
    ];

    private static readonly string[] NetworkFailureMarkers =
    [
        "Этот хост неизвестен",
        "No such host is known",
        "UnknownHostException",
        "SocketException",
        "HttpRequestException"
    ];

    // Расшифровка кодов выхода: отрицательные = аварийное нативное завершение (видеодрайвер, нативка
    // мода, оверлей), а не «чистый» выход Java. Помогает не гадать по голому числу.
    public static string DescribeExitCode(int exitCode)
    {
        var hex = "0x" + unchecked((uint)exitCode).ToString("X8");

        // На Linux и macOS процесс, убитый сигналом, отдаёт 128+номер сигнала. Коды NTSTATUS
        // (0xC0000005 и прочие) там не встречаются вовсе, а 137/139 без расшифровки выглядят
        // как случайные числа.
        if (!Platform.HostPlatform.IsWindows)
        {
            var unix = exitCode switch
            {
                0 => "код 0 (без ошибки)",
                1 => "код 1 (ранний выход — обычно повреждённая установка или падение до старта логов)",
                130 => "код 130 (SIGINT — прервано с клавиатуры)",
                134 => "код 134 (SIGABRT — аварийное завершение JVM, смотри hs_err)",
                137 => "код 137 (SIGKILL — процесс убит системой; чаще всего не хватило оперативной памяти)",
                139 => "код 139 (SIGSEGV — нативный краш, чаще видеодрайвер или нативка мода)",
                143 => "код 143 (SIGTERM — процесс попросили завершиться)",
                _ when exitCode > 128 && exitCode < 165 => $"код {exitCode} (сигнал {exitCode - 128})",
                _ => $"код {exitCode}"
            };

            return unix;
        }

        return exitCode switch
        {
            0 => "код 0 (без ошибки)",
            1 => "код 1 (ранний выход — обычно повреждённый путь/установка или падение до старта логов)",
            unchecked((int)0xC0000005) => $"{hex} ACCESS_VIOLATION — нативный краш, чаще видеодрайвер или нативка мода",
            unchecked((int)0xC0000409) => $"{hex} STACK_BUFFER_OVERRUN — нативный краш, чаще видеодрайвер/оверлей",
            unchecked((int)0xC00000FD) => $"{hex} STACK_OVERFLOW",
            unchecked((int)0xC000041D) => $"{hex} нативное исключение в коллбэке",
            unchecked((int)0xC0000374) => $"{hex} повреждение кучи (heap corruption)",
            _ when exitCode < 0 => $"{hex} аварийное нативное завершение",
            _ => $"код {exitCode}"
        };
    }

    private static string? FindNewestHsErr(string installRoot)
    {
        try
        {
            if (!Directory.Exists(installRoot))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(installRoot, "hs_err_pid*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    // allowFallback=false: вернуть null, если ни один паттерн не подошёл (используется для прохода
    // только по крэш-репорту, после которого разбирается полный текст).
    private static CrashFinding? DetectFinding(string text, int exitCode, bool allowFallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return allowFallback ? UnknownFinding(text ?? string.Empty, exitCode) : null;
        }

        foreach (var pattern in KnownPatterns)
        {
            if (pattern.Needles.All(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                var evidence = ExtractEvidence(text, pattern.Needles);
                return new CrashFinding(
                    pattern.Category,
                    pattern.Summary,
                    BuildSignature(pattern.Category, evidence, pattern.Summary),
                    evidence);
            }
        }

        return allowFallback ? UnknownFinding(text, exitCode) : null;
    }

    private static CrashFinding UnknownFinding(string text, int exitCode)
    {
        var fallbackEvidence = ExtractEvidence(text, ["Exception", "Error", "Caused by:", "ResolutionException", "Problematic frame"]);
        return new CrashFinding(
            "unknown",
            $"Minecraft завершился: {DescribeExitCode(exitCode)}. Точная причина не распознана автоматически.",
            BuildSignature("unknown", fallbackEvidence, $"exit_code_{exitCode}"),
            fallbackEvidence);
    }

    private static string BuildDetails(int exitCode, string summary, string latestLogPath, string? crashReportPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Minecraft быстро завершился после запуска.");
        builder.AppendLine();
        builder.AppendLine("Что найдено:");
        builder.AppendLine($"- {summary}");
        builder.AppendLine();
        builder.AppendLine($"Код завершения: {exitCode}");
        builder.AppendLine();
        builder.AppendLine("Файлы для диагностики:");
        builder.AppendLine(File.Exists(latestLogPath) ? latestLogPath : $"{latestLogPath} (не найден)");
        if (!string.IsNullOrWhiteSpace(crashReportPath))
        {
            builder.AppendLine(crashReportPath);
        }

        return builder.ToString();
    }

    private static string ExtractEvidence(string text, IReadOnlyList<string> needles)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (needles.Any(needle => trimmed.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                return SanitizeEvidence(trimmed);
            }
        }

        return string.Empty;
    }

    private static string SanitizeEvidence(string value)
    {
        var sanitized = Regex.Replace(value, @"[A-Za-z]:\\[^ \r\n\t]+", "<path>");

        // Домашние каталоги Unix вычищаем ОТДЕЛЬНО: правило выше знает только «C:\», поэтому
        // строка вида /home/ivan/... или /Users/ivan/... уезжала в телеметрию вместе с именем
        // пользователя. Это те же личные данные, что и в windows-пути.
        sanitized = Regex.Replace(sanitized, @"/(?:home|Users)/[^ \r\n\t:]+", "<path>");

        sanitized = Regex.Replace(sanitized, @"https?://\S+", "<url>");
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        const int maxLength = 220;
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "...";
    }

    private static string BuildSignature(string category, string evidence, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(evidence) ? fallback : evidence;
        source = Regex.Replace(source.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (source.Length > 80)
        {
            source = source[..80].Trim('_');
        }

        return string.IsNullOrWhiteSpace(source) ? category : $"{category}:{source}";
    }

    private static string? FindNewestCrashReport(string installRoot)
    {
        var crashRoot = Path.Combine(installRoot, "crash-reports");
        if (!Directory.Exists(crashRoot))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(crashRoot, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string ReadTail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        var text = File.ReadAllText(path);
        const int maxLength = 160_000;
        if (text.Length <= maxLength)
        {
            return text;
        }

        // Хвоста мало: latest.log обрезается по 2 МБ, и решающая строка (например OutOfMemoryError)
        // часто оказывается ЗА пределами хвоста — из-за этого краши уходили в «unknown». Поэтому к
        // хвосту добавляем строки с высокозначимыми маркерами, найденные в любом месте файла.
        var tail = text[^maxLength..];
        var highlights = CollectHighlightLines(text[..^maxLength]);
        return highlights.Length == 0 ? tail : highlights + Environment.NewLine + tail;
    }

    // Маркеры, ради которых стоит просматривать лог целиком, а не только хвост.
    private static readonly string[] HighSignalMarkers =
    [
        "OutOfMemoryError",
        "Java heap space",
        "JEIPluginManager.onRuntimeUnavailable",
        "Invalid paths argument, contained no existing paths",
        "Could not find or load main class",
        "UnsupportedClassVersionError",
        "Failed to initialize GLFW"
    ];

    private static string CollectHighlightLines(string head)
    {
        var found = new List<string>();
        foreach (var line in head.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (HighSignalMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(line);
                if (found.Count == 40)
                {
                    break;
                }
            }
        }

        return string.Join(Environment.NewLine, found);
    }

    private sealed record CrashFinding(string Category, string Summary, string Signature, string Evidence);
}

public sealed record CrashAnalysisResult(
    string Summary,
    string Details,
    string LatestLogPath,
    string? CrashReportPath,
    string Category,
    string Signature,
    string Evidence,
    bool HasCrashReport,
    string ExitCodeDescription,
    string ExitCodeHex,
    string LogTail,
    bool HasHsErr);
