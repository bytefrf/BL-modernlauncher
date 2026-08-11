using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Launcher.App.Models;
using Launcher.App.Platform;
using Microsoft.Win32;

namespace Launcher.App.Services;

public static partial class JavaValidationService
{
    public static string GetManagedJavaHome(string installRoot, int majorVersion = 17)
    {
        return Path.Combine(installRoot, ".launcher", "runtime", $"java-{majorVersion}");
    }

    public static string GetManagedConsoleJavaPath(string installRoot, int majorVersion = 17)
    {
        return Path.Combine(GetManagedJavaHome(installRoot, majorVersion), "bin", HostPlatform.JavaExecutableName);
    }

    public static async Task<JavaValidationResult> ValidateJavaAsync(string installRoot, LauncherManifest manifest, UserSettings settings, CancellationToken cancellationToken)
    {
        var requiredMajor = manifest.Game.JavaMajorVersion > 0 ? manifest.Game.JavaMajorVersion : 17;
        var configuredJava = string.IsNullOrWhiteSpace(settings.JavaExecutable)
            ? manifest.Game.JavaExecutable
            : settings.JavaExecutable;
        var candidates = ResolveConsoleJavaCandidates(installRoot, configuredJava, requiredMajor);
        JavaProbeResult? bestFound = null;
        Exception? lastException = null;

        foreach (var javaPath in candidates)
        {
            try
            {
                var probe = await ProbeJavaAsync(javaPath, cancellationToken);
                if (probe is null)
                {
                    continue;
                }

                bestFound ??= probe;
                if (probe.MajorVersion == requiredMajor)
                {
                    return new JavaValidationResult(true, probe.Path, probe.MajorVersion, $"Java {requiredMajor} найдена: {probe.Path}");
                }
            }
            catch (Exception exception)
            {
                lastException = exception;
            }
        }

        if (bestFound is not null)
        {
            return new JavaValidationResult(
                false,
                bestFound.Path,
                bestFound.MajorVersion,
                bestFound.MajorVersion is null
                    ? $"Не удалось определить версию Java. Нужна Java {requiredMajor}. Путь: {bestFound.Path}"
                    : $"Нужна Java {requiredMajor}, но найдена Java {bestFound.MajorVersion}. Путь: {bestFound.Path}");
        }

        var fallbackPath = candidates.FirstOrDefault() ?? "java.exe";
        return new JavaValidationResult(
            false,
            fallbackPath,
            null,
            lastException is null
                ? $"Не удалось определить версию Java. Нужна Java {requiredMajor}. Путь: {fallbackPath}"
                : $"Ошибка проверки Java: {lastException.Message}");
    }

    public static string GetPreferredLaunchJavaPath(string consoleJavaPath)
    {
        if (string.IsNullOrWhiteSpace(consoleJavaPath))
        {
            return "javaw.exe";
        }

        if (Path.GetFileName(consoleJavaPath).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
        {
            var javawPath = Path.Combine(Path.GetDirectoryName(consoleJavaPath) ?? string.Empty, "javaw.exe");
            if (File.Exists(javawPath))
            {
                return javawPath;
            }
        }

        return consoleJavaPath;
    }

    public static IReadOnlyList<string> ResolveConsoleJavaCandidates(string installRoot, string configuredValue, int managedJavaMajor = 17)
    {
        var candidates = new List<string>();
        // Сначала управляемая Java нужной версии (java-{major}); java-17 добавляем как фолбэк для
        // обратной совместимости (Forge-сборки — major=17, поведение не меняется).
        candidates.Add(GetManagedConsoleJavaPath(installRoot, managedJavaMajor > 0 ? managedJavaMajor : 17));
        if (managedJavaMajor != 17)
        {
            candidates.Add(GetManagedConsoleJavaPath(installRoot, 17));
        }

        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            // Настройка может указывать на бесконсольный javaw — рядом с ним лежит обычный java,
            // а версию мы умеем спросить только у него.
            if (Path.GetFileName(configuredValue).Equals(HostPlatform.JavawExecutableName, StringComparison.OrdinalIgnoreCase)
                && !HostPlatform.JavawExecutableName.Equals(HostPlatform.JavaExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(Path.GetDirectoryName(configuredValue) ?? string.Empty, HostPlatform.JavaExecutableName));
            }

            candidates.Add(configuredValue);
            if (!Path.IsPathRooted(configuredValue))
            {
                candidates.Add(Path.Combine(installRoot, configuredValue));
                candidates.Add(Path.Combine(installRoot, "jre", "bin", configuredValue));
            }
        }

        AddInstalledJavaCandidates(candidates, HostPlatform.JavaExecutableName);
        candidates.Add(HostPlatform.JavaExecutableName);

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddInstalledJavaCandidates(List<string> candidates, string executableName)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(Path.Combine(javaHome, "bin", executableName));
        }

        if (HostPlatform.IsWindows)
        {
            AddWindowsJavaCandidates(candidates, executableName);
        }
        else if (HostPlatform.IsMacOS)
        {
            AddMacJavaCandidates(candidates, executableName);
        }
        else
        {
            AddLinuxJavaCandidates(candidates, executableName);
        }

        foreach (var directory in EnumerateJavaDirectories())
        {
            candidates.Add(Path.Combine(directory, "bin", executableName));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsJavaCandidates(List<string> candidates, string executableName)
    {
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java", "jdk-17", "bin", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java", "jdk-21", "bin", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium", "jdk-17", "bin", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium", "jdk-21", "bin", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files", "Oracle", "Java", "javapath", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Oracle", "Java", "javapath", executableName));

        AddRegistryCandidates(candidates, executableName);
    }

    /// <summary>
    /// macOS: штатный способ — <c>/usr/libexec/java_home</c>, плюс каталог, куда ставятся все JDK.
    /// </summary>
    private static void AddMacJavaCandidates(List<string> candidates, string executableName)
    {
        foreach (var home in QueryMacJavaHomes())
        {
            candidates.Add(Path.Combine(home, "bin", executableName));
        }

        // Внутри .jdk-бандла настоящий JAVA_HOME лежит в Contents/Home.
        const string bundleRoot = "/Library/Java/JavaVirtualMachines";
        if (Directory.Exists(bundleRoot))
        {
            foreach (var bundle in SafeEnumerateDirectories(bundleRoot))
            {
                candidates.Add(Path.Combine(bundle, "Contents", "Home", "bin", executableName));
            }
        }

        candidates.Add(Path.Combine("/opt/homebrew/opt/openjdk/bin", executableName));
        candidates.Add(Path.Combine("/usr/local/opt/openjdk/bin", executableName));
    }

    /// <summary>
    /// Спрашивает у macOS пути ко всем установленным JDK. Утилита есть в системе всегда,
    /// но её отсутствие или таймаут не должны ломать поиск — тогда работают остальные кандидаты.
    /// </summary>
    private static IEnumerable<string> QueryMacJavaHomes()
    {
        const string tool = "/usr/libexec/java_home";
        if (!File.Exists(tool))
        {
            yield break;
        }

        string output;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = "-V",
                RedirectStandardOutput = true,
                // java_home печатает список именно в stderr, stdout остаётся пустым.
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                yield break;
            }

            output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                yield break;
            }
        }
        catch
        {
            yield break;
        }

        foreach (var line in output.Split('\n'))
        {
            // Строка вида: «    17.0.9 (arm64) "Eclipse Adoptium" - "..." /path/to/Contents/Home»
            var index = line.IndexOf(" /", StringComparison.Ordinal);
            if (index >= 0)
            {
                var home = line[(index + 1)..].Trim();
                if (home.Length > 1 && Directory.Exists(home))
                {
                    yield return home;
                }
            }
        }
    }

    /// <summary>
    /// Linux: пакетные менеджеры кладут JDK в <c>/usr/lib/jvm</c>, остальные пути — частые альтернативы.
    /// </summary>
    private static void AddLinuxJavaCandidates(List<string> candidates, string executableName)
    {
        foreach (var root in new[] { "/usr/lib/jvm", "/usr/lib64/jvm", "/opt/java", "/opt" })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in SafeEnumerateDirectories(root))
            {
                candidates.Add(Path.Combine(directory, "bin", executableName));
            }
        }

        candidates.Add(Path.Combine("/usr/bin", executableName));
        candidates.Add(Path.Combine("/usr/local/bin", executableName));

        // SDKMAN! ставит JDK в домашний каталог пользователя.
        var home = LauncherPaths.GetHomeRoot();
        if (!string.IsNullOrWhiteSpace(home))
        {
            var sdkman = Path.Combine(home, ".sdkman", "candidates", "java");
            if (Directory.Exists(sdkman))
            {
                foreach (var directory in SafeEnumerateDirectories(sdkman))
                {
                    candidates.Add(Path.Combine(directory, "bin", executableName));
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateJavaDirectories()
    {
        // Перебор версий внутри вендорских папок — это раскладка Windows; на Unix
        // соответствующие каталоги уже перечислены выше.
        if (!HostPlatform.IsWindows)
        {
            yield break;
        }

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu")
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return directory;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryCandidates(List<string> candidates, string executableName)
    {
        foreach (var registryPath in new[]
        {
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\Eclipse Adoptium\JDK",
            @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\Azul Systems\Zulu"
        })
        {
            AddRegistryCandidates(candidates, RegistryView.Registry64, registryPath, executableName);
            AddRegistryCandidates(candidates, RegistryView.Registry32, registryPath, executableName);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryCandidates(List<string> candidates, RegistryView view, string subKeyPath, string executableName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var subKey = baseKey.OpenSubKey(subKeyPath);
            if (subKey is null)
            {
                return;
            }

            foreach (var childName in subKey.GetSubKeyNames())
            {
                using var child = subKey.OpenSubKey(childName);
                if (child is null)
                {
                    continue;
                }

                AddRegistryJavaHome(candidates, child, executableName);

                foreach (var grandChildName in child.GetSubKeyNames())
                {
                    using var grandChild = child.OpenSubKey(grandChildName);
                    if (grandChild is not null)
                    {
                        AddRegistryJavaHome(candidates, grandChild, executableName);
                    }
                }
            }
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryJavaHome(List<string> candidates, RegistryKey key, string executableName)
    {
        var javaHome = key.GetValue("JavaHome") as string
            ?? key.GetValue("Path") as string
            ?? key.GetValue("Home") as string;

        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(Path.Combine(javaHome, "bin", executableName));
        }
    }

    private static async Task<JavaProbeResult?> ProbeJavaAsync(string javaPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-version");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = $"{await stdoutTask}{Environment.NewLine}{await stderrTask}";
        return new JavaProbeResult(javaPath, ParseMajorVersion(output), output);
    }

    private static int? ParseMajorVersion(string output)
    {
        var match = JavaVersionRegex().Match(output);
        if (!match.Success)
        {
            return null;
        }

        var version = match.Groups["version"].Value;
        if (version.StartsWith("1.", StringComparison.Ordinal))
        {
            return int.TryParse(version.Split('.')[1], out var legacy) ? legacy : null;
        }

        return int.TryParse(version.Split('.')[0], out var modern) ? modern : null;
    }

    [GeneratedRegex("version\\s+\"(?<version>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex JavaVersionRegex();

    private sealed record JavaProbeResult(string Path, int? MajorVersion, string Output);
}

public sealed record JavaValidationResult(bool IsOk, string JavaPath, int? MajorVersion, string Message);
