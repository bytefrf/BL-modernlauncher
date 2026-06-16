using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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
        return TryGetMemory(out var status) ? (long)(status.ullTotalPhys / (1024 * 1024)) : null;
    }

    private static long? TryGetAvailableRamMb()
    {
        return TryGetMemory(out var status) ? (long)(status.ullAvailPhys / (1024 * 1024)) : null;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
