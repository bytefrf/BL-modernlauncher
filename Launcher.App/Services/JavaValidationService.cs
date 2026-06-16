using System.Diagnostics;
using System.Text.RegularExpressions;
using Launcher.App.Models;
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
        return Path.Combine(GetManagedJavaHome(installRoot, majorVersion), "bin", "java.exe");
    }

    public static async Task<JavaValidationResult> ValidateJavaAsync(string installRoot, LauncherManifest manifest, UserSettings settings, CancellationToken cancellationToken)
    {
        var configuredJava = string.IsNullOrWhiteSpace(settings.JavaExecutable)
            ? manifest.Game.JavaExecutable
            : settings.JavaExecutable;
        var candidates = ResolveConsoleJavaCandidates(installRoot, configuredJava);
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
                if (probe.MajorVersion == 17)
                {
                    return new JavaValidationResult(true, probe.Path, probe.MajorVersion, $"Java 17 найдена: {probe.Path}");
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
                    ? $"Не удалось определить версию Java. Нужна Java 17. Путь: {bestFound.Path}"
                    : $"Нужна Java 17, но найдена Java {bestFound.MajorVersion}. Путь: {bestFound.Path}");
        }

        var fallbackPath = candidates.FirstOrDefault() ?? "java.exe";
        return new JavaValidationResult(
            false,
            fallbackPath,
            null,
            lastException is null
                ? $"Не удалось определить версию Java. Нужна Java 17. Путь: {fallbackPath}"
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

    public static IReadOnlyList<string> ResolveConsoleJavaCandidates(string installRoot, string configuredValue)
    {
        var candidates = new List<string>();
        candidates.Add(GetManagedConsoleJavaPath(installRoot));

        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            if (Path.GetFileName(configuredValue).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(Path.GetDirectoryName(configuredValue) ?? string.Empty, "java.exe"));
            }

            candidates.Add(configuredValue);
            if (!Path.IsPathRooted(configuredValue))
            {
                candidates.Add(Path.Combine(installRoot, configuredValue));
                candidates.Add(Path.Combine(installRoot, "jre", "bin", configuredValue));
            }
        }

        AddInstalledJavaCandidates(candidates, "java.exe");
        candidates.Add("java.exe");

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

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java", "jdk-17", "bin", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java", "jdk-21", "bin", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium", "jdk-17", "bin", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium", "jdk-21", "bin", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files", "Oracle", "Java", "javapath", executableName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Oracle", "Java", "javapath", executableName));

        AddRegistryCandidates(candidates, executableName);

        foreach (var directory in EnumerateJavaDirectories())
        {
            candidates.Add(Path.Combine(directory, "bin", executableName));
        }
    }

    private static IEnumerable<string> EnumerateJavaDirectories()
    {
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
