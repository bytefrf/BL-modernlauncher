using System.Diagnostics;
using System.Text;
using Launcher.App.Platform;

namespace Launcher.App.Services;

/// <summary>
/// Удаляет лаунчер. Сам исполняемый файл запущен и не может удалить себя на ходу, поэтому (как и при
/// самообновлении) запускаем отдельный скрипт: он ждёт выхода процесса лаунчера,
/// удаляет указанные пути и в конце удаляет сам себя.
/// </summary>
/// <remarks>
/// Скрипт свой для каждой ОС: PowerShell на Windows, POSIX-шелл на Linux и macOS.
/// </remarks>
public sealed class UninstallService
{
    public void Uninstall(UninstallRequest request)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "BLModernTFGM", "uninstall");
        Directory.CreateDirectory(tempRoot);

        if (!HostPlatform.IsWindows)
        {
            UninstallUnix(request, tempRoot);
            return;
        }

        var scriptPath = Path.Combine(tempRoot, $"uninstall-{Guid.NewGuid():N}.ps1");

        var script = new StringBuilder();
        script.AppendLine($"$processIdToWait = {request.ProcessId}");
        script.AppendLine("while (Get-Process -Id $processIdToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }");
        script.AppendLine("Start-Sleep -Seconds 1");

        // Файлы игры удаляем выборочно: всё, кроме сохраняемых папок (миры, скриншоты).
        if (request.RemoveGameData && !string.IsNullOrWhiteSpace(request.GameDataRoot))
        {
            var preserve = string.Join(",", request.PreserveFolderNames.Select(name => $"'{EscapePowerShell(name)}'"));
            script.AppendLine($"$gameRoot = '{EscapePowerShell(request.GameDataRoot)}'");
            script.AppendLine($"$preserve = @({preserve})");
            script.AppendLine("if (Test-Path -LiteralPath $gameRoot) {");
            // Пропускаем preserve-папки И вложенные установки других сборок (там свой .launcher\modpack.version),
            // чтобы удаление одной сборки не снесло соседнюю, лежащую в её подпапке.
            script.AppendLine("  Get-ChildItem -LiteralPath $gameRoot -Force | Where-Object { $preserve -notcontains $_.Name -and -not (Test-Path -LiteralPath (Join-Path $_.FullName '.launcher\\modpack.version')) } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }");
            script.AppendLine("  if (-not (Get-ChildItem -LiteralPath $gameRoot -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $gameRoot -Recurse -Force -ErrorAction SilentlyContinue }");
            script.AppendLine("}");
        }

        foreach (var path in request.PathsToRemove.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            script.AppendLine($"$p = '{EscapePowerShell(path)}'");
            script.AppendLine("if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue }");
        }

        script.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");

        File.WriteAllText(scriptPath, script.ToString());

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            // Рабочую папку держим в корне %Temp%, чтобы не заблокировать удаление папки установки
            // (cwd процесса нельзя удалить).
            WorkingDirectory = Path.GetTempPath()
        });
    }

    /// <summary>
    /// Linux и macOS: тот же сценарий на POSIX-шелле. Запускается через <c>/bin/sh</c>, чтобы
    /// не зависеть от того, установлен ли bash (в macOS по умолчанию zsh).
    /// </summary>
    private static void UninstallUnix(UninstallRequest request, string tempRoot)
    {
        var scriptPath = Path.Combine(tempRoot, $"uninstall-{Guid.NewGuid():N}.sh");

        var script = new StringBuilder();
        script.Append("#!/bin/sh\n");
        // kill -0 не посылает сигнал, а только проверяет, жив ли процесс.
        script.Append($"while kill -0 {request.ProcessId} 2>/dev/null; do sleep 0.3; done\n");
        script.Append("sleep 1\n");

        if (request.RemoveGameData && !string.IsNullOrWhiteSpace(request.GameDataRoot))
        {
            script.Append($"game_root={EscapeShell(request.GameDataRoot)}\n");
            script.Append("if [ -d \"$game_root\" ]; then\n");
            script.Append("  for entry in \"$game_root\"/* \"$game_root\"/.[!.]*; do\n");
            script.Append("    [ -e \"$entry\" ] || continue\n");
            script.Append("    name=$(basename \"$entry\")\n");
            script.Append("    keep=0\n");

            foreach (var name in request.PreserveFolderNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                script.Append($"    [ \"$name\" = {EscapeShell(name)} ] && keep=1\n");
            }

            // Вложенная установка другой сборки опознаётся по своему маркеру — её не трогаем,
            // иначе удаление одной сборки снесло бы соседнюю.
            script.Append("    [ -f \"$entry/.launcher/modpack.version\" ] && keep=1\n");
            script.Append("    [ \"$keep\" = \"0\" ] && rm -rf \"$entry\"\n");
            script.Append("  done\n");
            script.Append("  rmdir \"$game_root\" 2>/dev/null\n");
            script.Append("fi\n");
        }

        foreach (var path in request.PathsToRemove.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            script.Append($"rm -rf {EscapeShell(path)}\n");
        }

        script.Append("rm -f \"$0\"\n");

        File.WriteAllText(scriptPath, script.ToString());

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { scriptPath },
            UseShellExecute = false,
            CreateNoWindow = true,
            // Рабочую папку держим в корне временных файлов, чтобы не заблокировать удаление
            // папки установки (текущий каталог процесса удалить нельзя).
            WorkingDirectory = Path.GetTempPath()
        });
    }

    private static string EscapePowerShell(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>
    /// Оборачивает значение в одинарные кавычки для POSIX-шелла: внутри них спецсимволов нет,
    /// а сама кавычка закрывается и вставляется экранированной.
    /// </summary>
    private static string EscapeShell(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

public sealed record UninstallRequest(
    int ProcessId,
    IReadOnlyList<string> PathsToRemove,
    bool RemoveGameData,
    string? GameDataRoot,
    IReadOnlyList<string> PreserveFolderNames);
