namespace Launcher.App.Services;

/// <summary>Что именно лаунчер может починить сам после разбора краша.</summary>
public enum RepairKind
{
    /// <summary>Удалить конкретный файл (битый конфиг мода) — игра создаст его заново.</summary>
    DeleteFile,

    /// <summary>Снести неполный мод-лоадер, чтобы он переустановился при следующем запуске.</summary>
    ReinstallLoader
}

/// <summary>
/// План починки: что удаляем и что показать игроку на кнопке.
/// </summary>
/// <param name="Targets">Полные пути. Всё внутри папки установки — проверено при построении плана.</param>
public sealed record RepairPlan(
    RepairKind Kind,
    string ButtonText,
    string Description,
    IReadOnlyList<string> Targets);

public sealed record RepairResult(bool Success, string Message);

/// <summary>
/// Превращает разбор краша в конкретное действие.
/// </summary>
/// <remarks>
/// Классификатор и раньше точно называл причину и путь к файлу, но чинить игрок должен был руками:
/// найти папку, найти файл, удалить, не промахнуться. Для части категорий это механическая работа,
/// которую лаунчер может сделать сам — здесь решается, для каких именно.
/// Осознанно НЕ чиним то, где удаление может стоить игроку прогресса: конфиги типа SERVER лежат
/// внутри мира, и точное имя мира из лога неизвестно.
/// </remarks>
public static class CrashRepairPlanner
{
    public static RepairPlan? Plan(CrashAnalysisResult analysis, string installRoot, bool neoForge = false)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return null;
        }

        var root = Path.GetFullPath(installRoot);

        if (string.Equals(analysis.Category, "config_corrupted", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(analysis.RepairTargetPath))
        {
            var target = Path.GetFullPath(Path.Combine(root, analysis.RepairTargetPath));

            // Путь пришёл из лога игры, то есть из данных, которым нельзя доверять на слово:
            // за пределы папки установки не выпускаем.
            if (!IsInside(root, target) || !File.Exists(target))
            {
                return null;
            }

            return new RepairPlan(
                RepairKind.DeleteFile,
                "Удалить битый файл",
                $"Лаунчер удалит {analysis.RepairTargetPath} — игра создаст его заново при следующем запуске. " +
                "Мир и постройки не пострадают.",
                [target]);
        }

        if (string.Equals(analysis.Category, "forge_incomplete", StringComparison.Ordinal) ||
            string.Equals(analysis.Category, "launch_classpath", StringComparison.Ordinal))
        {
            // Те же папки, что чистит самолечение в RuntimeInstallService: версии лоадера,
            // его библиотеки и пропатченный клиент. Моды, конфиги и миры не трогаем.
            var loaderLibraries = neoForge
                ? Path.Combine(root, "libraries", "net", "neoforged")
                : Path.Combine(root, "libraries", "net", "minecraftforge");

            var targets = new[]
            {
                Path.Combine(root, "versions"),
                loaderLibraries,
                Path.Combine(root, "libraries", "net", "minecraft", "client")
            }.Where(Directory.Exists).ToList();

            if (targets.Count == 0)
            {
                return null;
            }

            return new RepairPlan(
                RepairKind.ReinstallLoader,
                "Переустановить Forge",
                "Лаунчер удалит неполную установку загрузчика и поставит её заново при следующем запуске. " +
                "Моды, настройки и миры останутся на месте, докачается около 300 МБ.",
                targets);
        }

        return null;
    }

    /// <summary>Выполняет план. Ошибки не пробрасываются: починка не должна ронять лаунчер.</summary>
    public static RepairResult Apply(RepairPlan plan)
    {
        var removed = 0;
        foreach (var target in plan.Targets)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                    removed++;
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                    removed++;
                }
            }
            catch (Exception exception)
            {
                return new RepairResult(false, $"Не удалось удалить {Path.GetFileName(target)}: {exception.Message}");
            }
        }

        if (removed == 0)
        {
            return new RepairResult(false, "Чинить нечего: файлы уже отсутствуют.");
        }

        return new RepairResult(true, plan.Kind == RepairKind.ReinstallLoader
            ? "Готово. Нажми «Играть» — лаунчер поставит загрузчик заново."
            : "Готово. Нажми «Играть» — игра создаст файл настроек заново.");
    }

    private static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
