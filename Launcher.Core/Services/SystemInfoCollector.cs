using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Launcher.App.Platform;

namespace Launcher.App.Services;

/// <summary>
/// Собирает обезличенные данные о системе для телеметрии: ОС, архитектура,
/// CPU, RAM, локаль, версия .NET. Без персональных данных — только агрегируемые метрики.
/// </summary>
public static class SystemInfoCollector
{
    public static Dictionary<string, object?> Collect()
    {
        return new Dictionary<string, object?>
        {
            ["osVersion"] = Environment.OSVersion.VersionString,
            ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["is64BitOs"] = Environment.Is64BitOperatingSystem,
            ["is64BitProcess"] = Environment.Is64BitProcess,
            ["cpuCores"] = Environment.ProcessorCount,
            ["ramTotalMb"] = TryGetTotalRamMb(),
            ["ramAvailableMb"] = TryGetAvailableRamMb(),
            ["culture"] = CultureInfo.CurrentCulture.Name,
            ["uiCulture"] = CultureInfo.CurrentUICulture.Name,
            ["dotnetVersion"] = RuntimeInformation.FrameworkDescription,
            ["machineHash"] = HashStable(Environment.MachineName + "|" + Environment.UserName)
        };
    }

    private static long? TryGetTotalRamMb()
    {
        if (!HostPlatform.IsWindows)
        {
            return TryGetUnixTotalRamMb();
        }

        return TryGetMemory(out var status) ? (long)(status.ullTotalPhys / (1024 * 1024)) : null;
    }

    private static long? TryGetAvailableRamMb()
    {
        if (!HostPlatform.IsWindows)
        {
            return TryGetUnixAvailableRamMb();
        }

        return TryGetMemory(out var status) ? (long)(status.ullAvailPhys / (1024 * 1024)) : null;
    }

    /// <summary>
    /// Общий объём памяти на Unix. На Linux берём из <c>/proc/meminfo</c>, на macOS — у <c>sysctl</c>.
    /// </summary>
    private static long? TryGetUnixTotalRamMb()
    {
        if (HostPlatform.IsLinux)
        {
            return ReadMemInfoValueMb("MemTotal:");
        }

        // macOS: hw.memsize отдаёт объём в байтах одной строкой.
        var raw = RunTool("/usr/sbin/sysctl", "-n", "hw.memsize");
        return long.TryParse(raw?.Trim(), out var bytes) ? bytes / (1024 * 1024) : null;
    }

    private static long? TryGetUnixAvailableRamMb()
    {
        if (HostPlatform.IsLinux)
        {
            // MemAvailable учитывает освобождаемый кэш — это ближе к смыслу «сколько реально можно занять»,
            // чем MemFree, и точнее соответствует ullAvailPhys на Windows.
            return ReadMemInfoValueMb("MemAvailable:");
        }

        // macOS: vm_stat печатает страницы. Свободными считаем free + inactive + speculative.
        var raw = RunTool("/usr/bin/vm_stat");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        long pageSize = 4096;
        var pageSizeMatch = System.Text.RegularExpressions.Regex.Match(raw, @"page size of (\d+) bytes");
        if (pageSizeMatch.Success && long.TryParse(pageSizeMatch.Groups[1].Value, out var parsedPageSize))
        {
            pageSize = parsedPageSize;
        }

        long pages = 0;
        foreach (var key in new[] { "Pages free", "Pages inactive", "Pages speculative" })
        {
            var match = System.Text.RegularExpressions.Regex.Match(raw, key + @":\s+(\d+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out var value))
            {
                pages += value;
            }
        }

        return pages > 0 ? pages * pageSize / (1024 * 1024) : null;
    }

    /// <summary>Читает значение из <c>/proc/meminfo</c>; там килобайты.</summary>
    private static long? ReadMemInfoValueMb(string key)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith(key, StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out var kilobytes))
                {
                    return kilobytes / 1024;
                }
            }
        }
        catch
        {
            // Телеметрия не должна падать из-за недоступного /proc.
        }

        return null;
    }

    private static string? RunTool(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetMemory(out MEMORYSTATUSEX status)
    {
        status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        try
        {
            return GlobalMemoryStatusEx(ref status);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Короткий необратимый хэш для подсчёта уникальных машин без раскрытия имени.</summary>
    private static string HashStable(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
