using System.Net.Sockets;
using System.Text.Json;
using Launcher.App;
using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Platform;
using Launcher.App.Services;

// Режим проверки NeoForge/Forge-рантайма по ModpackManifest (как в каталог-режиме лаунчера):
//   SmokeTest --runtime <manifestUrl> [--install-only]
// Гоняет ровно тот код, что и UI: RuntimeInstallService.EnsureRuntimeAsync + диагностический запуск.
if (args.Length >= 2 && args[0].Equals("--runtime", StringComparison.OrdinalIgnoreCase))
{
    await RunRuntimeTestAsync(args[1], args.Contains("--install-only", StringComparer.OrdinalIgnoreCase));
    return;
}

// Прогон крэш-анализатора по распакованным саппорт-бандлам: SmokeTest --analyze-bundles <папка>
if (args.Length >= 2 && args[0].Equals("--analyze-bundles", StringComparison.OrdinalIgnoreCase))
{
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    var total = 0;
    foreach (var dir in Directory.EnumerateDirectories(args[1]))
    {
        // Пересобираем РЕАЛЬНУЮ раскладку установки из бандла (в бандле она другая):
        // logs\latest.log, logs\minecraft-stderr.log, crash-reports\*, hs_err_pid*.log в корне.
        // InstallRoot из бандла важен: по нему анализатор решает, виновата ли кириллица в пути.
        // Поэтому и рабочую папку делаем с кириллицей, если она была у игрока.
        var contextPath = Path.Combine(dir, "launcher-context.txt");
        var installRoot = File.Exists(contextPath)
            ? File.ReadLines(contextPath)
                .FirstOrDefault(line => line.TrimStart('﻿').StartsWith("InstallRoot:", StringComparison.Ordinal))
                ?.Split(':', 2)[1].Trim() ?? string.Empty
            : string.Empty;
        var nonAsciiPath = installRoot.Any(c => c > (char)127);

        var work = Path.Combine(Path.GetTempPath(), (nonAsciiPath ? "аб-" : "ab-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "logs"));
        Directory.CreateDirectory(Path.Combine(work, "crash-reports"));
        Directory.CreateDirectory(Path.Combine(work, ".launcher", "logs"));
        void CopyFirst(string pattern, string dest)
        {
            var f = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).FirstOrDefault();
            if (f != null) File.Copy(f, dest, true);
        }
        CopyFirst("latest.log", Path.Combine(work, "logs", "latest.log"));
        CopyFirst("minecraft-stderr.log", Path.Combine(work, "logs", "minecraft-stderr.log"));
        foreach (var cr in Directory.EnumerateFiles(dir, "crash-*.txt", SearchOption.AllDirectories))
            File.Copy(cr, Path.Combine(work, "crash-reports", Path.GetFileName(cr)), true);
        foreach (var he in Directory.EnumerateFiles(dir, "hs_err_pid*.log", SearchOption.AllDirectories))
            File.Copy(he, Path.Combine(work, Path.GetFileName(he)), true);
        // Лог лаунчера нужен анализатору: по нему отличается «не докачались файлы» от «заблокирован Mojang».
        foreach (var ll in Directory.EnumerateFiles(dir, "launcher-*.log", SearchOption.AllDirectories))
            File.Copy(ll, Path.Combine(work, ".launcher", "logs", Path.GetFileName(ll)), true);

        var r = CrashAnalyzerService.Analyze(work, -1073741819);
        counts[r.Category] = counts.GetValueOrDefault(r.Category) + 1;
        total++;
        if (r.Category == "launch_classpath")
        {
            Console.WriteLine($"  [{Path.GetFileName(dir)}] nonAscii={nonAsciiPath} root={installRoot}");
            Console.WriteLine($"      -> {r.Summary}");
        }
        try { Directory.Delete(work, true); } catch { }
    }
    Console.WriteLine($"бандлов: {total}");
    foreach (var kv in counts.OrderByDescending(k => k.Value))
    {
        Console.WriteLine($"  {kv.Value,3}  {kv.Key}");
    }
    return;
}

// Сплошная проверка выложенного зеркала: SmokeTest --verify-mirror <baseUrl> <локальнаяПапка> [sha1Выборка]
// Размер сверяется у ВСЕХ объектов (ловит порчу от ASCII-режима FTP), sha1 — у случайной выборки.
if (args.Length >= 3 && args[0].Equals("--verify-mirror", StringComparison.OrdinalIgnoreCase))
{
    var baseUrl = args[1].TrimEnd('/');
    var local = args[2];
    var sha1Sample = args.Length >= 4 ? int.Parse(args[3]) : 40;

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var indexPath = Directory.EnumerateFiles(Path.Combine(local, "assets", "indexes"), "*.json").First();
    using var indexDoc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
    var objects = indexDoc.RootElement.GetProperty("objects").EnumerateObject()
        .Select(o => (Name: o.Name, Hash: o.Value.GetProperty("hash").GetString()!, Size: o.Value.GetProperty("size").GetInt64()))
        .GroupBy(o => o.Hash).Select(g => g.First()).ToList();
    Console.WriteLine($"Индекс: {Path.GetFileName(indexPath)}, уникальных объектов: {objects.Count}");

    var rng = new Random(20260803);
    var sha1Set = objects.OrderBy(_ => rng.Next()).Take(sha1Sample).Select(o => o.Hash).ToHashSet(StringComparer.Ordinal);

    var problems = new System.Collections.Concurrent.ConcurrentBag<string>();
    var done = 0;
    var gate = new SemaphoreSlim(24);
    var tasks = objects.Select(async o =>
    {
        await gate.WaitAsync();
        try
        {
            var url = $"{baseUrl}/assets/objects/{o.Hash[..2]}/{o.Hash}";
            if (sha1Set.Contains(o.Hash))
            {
                var bytes = await http.GetByteArrayAsync(url);
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).ToLowerInvariant();
                if (bytes.LongLength != o.Size) { problems.Add($"РАЗМЕР {o.Name} [{o.Hash}]: ждали {o.Size}, получили {bytes.LongLength}"); }
                else if (actual != o.Hash) { problems.Add($"SHA1   {o.Name} [{o.Hash}]: получили {actual}"); }
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) { problems.Add($"HTTP   {o.Name} [{o.Hash}]: {(int)response.StatusCode}"); }
                else
                {
                    var total = response.Content.Headers.ContentRange?.Length;
                    if (total != o.Size) { problems.Add($"РАЗМЕР {o.Name} [{o.Hash}]: ждали {o.Size}, получили {total}"); }
                }
            }
        }
        catch (Exception ex) { problems.Add($"ОШИБКА {o.Name} [{o.Hash}]: {ex.Message}"); }
        finally
        {
            var current = Interlocked.Increment(ref done);
            if (current % 500 == 0) { Console.WriteLine($"  проверено {current}/{objects.Count}"); }
            gate.Release();
        }
    });
    await Task.WhenAll(tasks);

    Console.WriteLine($"\nПроверено объектов: {objects.Count} (из них sha1: {sha1Set.Count})");
    Console.WriteLine($"Проблем: {problems.Count}");
    foreach (var p in problems.Take(25)) { Console.WriteLine($"  {p}"); }
    Console.WriteLine($"VERIFY_MIRROR={(problems.IsEmpty ? "PASS" : "FAIL")}");
    return;
}

// Проверка фейловера зеркал: SmokeTest --mirror
// Первый адрес заведомо мёртвый — файл обязан доехать со второго, и наоборот, при всех мёртвых
// адресах в тексте ошибки должны остаться ВСЕ хосты (по ним ErrorClassifier узнаёт блокировку Mojang).
if (args.Length >= 1 && args[0].Equals("--mirror", StringComparison.OrdinalIgnoreCase))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var service = new RuntimeInstallService(http);
    var download = typeof(RuntimeInstallService)
        .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        .First(m => m.Name == "DownloadFileAsync" && m.GetParameters()[0].ParameterType != typeof(string));

    async Task<Exception?> TryDownload(string[] urls, string target)
    {
        try
        {
            await (Task)download.Invoke(service,
                [urls, target, 0L, string.Empty, string.Empty, null, CancellationToken.None, string.Empty])!;
            return null;
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            // Разворачиваем только обёртку рефлексии: нужное сообщение со списком адресов — снаружи.
            return ex.InnerException ?? ex;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    var work = Path.Combine(Path.GetTempPath(), "mirror-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    const string dead = "https://mirror-does-not-exist.bl-modern.ru/version_manifest_v2.json";
    const string real = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    var target1 = Path.Combine(work, "fallback.json");
    var error1 = await TryDownload([dead, real], target1);
    var ok1 = error1 is null && File.Exists(target1) && new FileInfo(target1).Length > 0;
    Console.WriteLine($"  [{(ok1 ? "OK  " : "FAIL")}] мёртвое зеркало -> Mojang: скачано={File.Exists(target1)} {(error1 is null ? "" : error1.Message)}");

    var error2 = await TryDownload([dead, "https://mirror-also-dead.bl-modern.ru/x.json"], Path.Combine(work, "none.json"));
    var text = error2?.Message ?? string.Empty;
    var ok2 = error2 is not null && text.Contains("mirror-does-not-exist") && text.Contains("mirror-also-dead");
    Console.WriteLine($"  [{(ok2 ? "OK  " : "FAIL")}] все адреса мертвы -> в ошибке перечислены все: {text}");

    try { Directory.Delete(work, true); } catch { }
    Console.WriteLine($"MIRROR_RESULT={(ok1 && ok2 ? "PASS" : "FAIL")}");
    return;
}

// Проверка классификатора ошибок на исключениях из реальных саппорт-логов: SmokeTest --classify
if (args.Length >= 1 && args[0].Equals("--classify", StringComparison.OrdinalIgnoreCase))
{
    var passed = 0;
    var failed = 0;
    void Expect(string name, Exception exception, string expectedTitle)
    {
        var info = ErrorClassifier.Classify(exception);
        var ok = info.Title == expectedTitle;
        if (ok) { passed++; } else { failed++; }
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {name}: '{info.Title}' (ждали '{expectedTitle}')");
    }

    // Vel_Winter, 03.08: DNS не резолвит piston-data.mojang.com при скачивании client.jar.
    // Раньше классифицировалось как «Ошибка сети», игрок чинил интернет вместо DNS/VPN.
    var mojangDns = new IOException(
        "Не удалось загрузить файл после нескольких попыток: https://piston-data.mojang.com/v1/objects/0c3e/client.jar",
        new HttpRequestException(
            "Этот хост неизвестен. (piston-data.mojang.com:443)",
            new SocketException((int)SocketError.HostNotFound)));
    Expect("Mojang DNS при загрузке client.jar", mojangDns, "Нет доступа к серверам Mojang");

    // Тот же отказ, но напрямую HttpRequestException (метаданные версии, без обёртки IOException).
    var mojangMeta = new HttpRequestException(
        "Этот хост неизвестен. (piston-meta.mojang.com:443)",
        new SocketException((int)SocketError.HostNotFound));
    Expect("Mojang DNS при загрузке манифеста версий", mojangMeta, "Нет доступа к серверам Mojang");

    // Регресс: недоступность САЙТА лаунчера не должна уехать в ветку про Mojang.
    var siteDns = new HttpRequestException(
        "Этот хост неизвестен. (bl-modern.ru:443)",
        new SocketException((int)SocketError.HostNotFound));
    Expect("DNS на bl-modern.ru", siteDns, "Сайт лаунчера недоступен");

    // Регресс: обычный обрыв закачки архива сборки остаётся обычной ошибкой сети.
    var archiveReset = new IOException(
        "Не удалось загрузить файл после нескольких попыток: https://bl-modern.ru/download/tfgm.zip",
        new SocketException((int)SocketError.ConnectionReset));
    Expect("обрыв закачки архива сборки", archiveReset, "Ошибка сети");

    Console.WriteLine($"CLASSIFY_RESULT={(failed == 0 ? "PASS" : "FAIL")} ok={passed} fail={failed}");
    return;
}

// Лог ошибки и текст для буфера обмена: SmokeTest --error-report (без сети)
// Формат общий у WPF и Avalonia — расхождение здесь означает, что саппорт-бандлы
// с Linux/macOS придут в другом формате, чем с Windows.
if (args.Length >= 1 && args[0].Equals("--error-report", StringComparison.OrdinalIgnoreCase))
{
    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    var info = ErrorClassifier.Classify(new HttpRequestException(
        "Этот хост неизвестен. (bl-modern.ru:443)",
        new SocketException((int)SocketError.HostNotFound)));
    var exception = new InvalidOperationException("тестовое исключение");

    var content = ErrorReport.BuildLogContent(
        @"C:\forge", info, exception, "1.3.2", "0.13.4", "https://bl-modern.ru/api/catalog.php");

    Expect("в логе есть заголовок ошибки", content.Contains($"Заголовок: {info.Title}"), info.Title);
    Expect("в логе есть версия лаунчера", content.Contains("Версия лаунчера: 1.3.2"), "1.3.2");
    Expect("в логе есть версия сборки", content.Contains("Версия модпака: 0.13.4"), "0.13.4");
    Expect("в логе есть папка установки", content.Contains(@"Папка установки: C:\forge"), @"C:\forge");
    Expect("в логе есть технические детали", content.Contains("тестовое исключение"), "есть");
    Expect("шаги «что сделать» перечислены дефисом",
        info.Actions.All(action => content.Contains("- " + action)), $"{info.Actions.Count} шт.");

    // Файл действительно создаётся, и путь ведёт в .launcher/logs выбранной папки.
    var sandbox = Path.Combine(Path.GetTempPath(), "bl-error-report-" + Guid.NewGuid().ToString("N")[..8]);
    var written = ErrorReport.Write(sandbox, info, exception, "1.3.2", "0.13.4", "-", out var failure);
    Expect("лог записан без ошибок", failure is null, failure ?? "нет ошибок");
    Expect("файл лога существует", written.Length > 0 && File.Exists(written), written);
    Expect("лог лежит в .launcher/logs",
        written.Replace('\\', '/').Contains("/.launcher/logs/"), written);
    try { Directory.Delete(sandbox, true); } catch { /* временная папка */ }

    // Недоступная папка не должна ронять показ окна ошибки — только вернуть причину.
    var blocked = ErrorReport.Write("\0неверный путь", info, exception, "1.3.2", "0.13.4", "-", out var blockedFailure);
    Expect("недоступная папка не роняет разбор", blocked.Length == 0 && blockedFailure is not null,
        blockedFailure ?? "(нет причины)");

    var clipboard = ErrorReport.BuildClipboardText(info, @"C:\forge\.launcher\logs\launcher-error.log");
    Expect("в буфере есть заголовок", clipboard.StartsWith(info.Title, StringComparison.Ordinal), info.Title);
    Expect("в буфере есть путь к логу", clipboard.Contains(@"C:\forge\.launcher\logs\launcher-error.log"), "есть");
    Expect("в буфере есть раздел «Что сделать»", clipboard.Contains("Что сделать:"), "есть");

    Console.WriteLine($"ERROR_REPORT_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Способ установки и план обновления: SmokeTest --update-plan (без сети)
// Главное, что проверяем: установленный системным пакетом лаунчер НЕ пытается обновить себя сам,
// а Windows ведёт себя ровно как раньше.
if (args.Length >= 1 && args[0].Equals("--update-plan", StringComparison.OrdinalIgnoreCase))
{
    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    // AppImage узнаётся по переменной APPIMAGE, которую выставляет сам образ.
    var appImage = LauncherInstallation.BuildPlan("/tmp/.mount_blXYZ/usr/bin/Launcher.Avalonia", "/home/user/BL-modern.AppImage");
    Expect("AppImage распознан", appImage.Kind == LauncherInstallationKind.AppImage, appImage.Kind.ToString());
    Expect("AppImage обновляет себя сам", appImage.CanSelfUpdate, "да");
    Expect("цель обновления — сам файл образа",
        appImage.TargetPath == "/home/user/BL-modern.AppImage", appImage.TargetPath);

    // Установка пакетом: /opt и /usr.
    foreach (var path in new[] { "/opt/bl-modern/Launcher.Avalonia", "/usr/lib/bl-modern/Launcher.Avalonia" })
    {
        var package = LauncherInstallation.BuildPlan(path, appImagePath: string.Empty);
        Expect($"пакет распознан ({path})", package.Kind == LauncherInstallationKind.SystemPackage, package.Kind.ToString());
        Expect("пакет НЕ обновляет себя сам", !package.CanSelfUpdate, "верно");
        Expect("игроку объясняют, что делать", package.Instruction.Contains("пакет"), package.Instruction[..40] + "…");
    }

    // Портативная распаковка в домашней папке обновляется как раньше.
    var portable = LauncherInstallation.BuildPlan("/home/user/launcher/Launcher.Avalonia", appImagePath: string.Empty);
    Expect("портативная установка распознана", portable.Kind == LauncherInstallationKind.Portable, portable.Kind.ToString());
    Expect("портативная обновляет себя сама", portable.CanSelfUpdate, "да");

    // Windows: путь в Program Files не должен считаться системным пакетом.
    var windows = LauncherInstallation.BuildPlan(@"C:\Program Files\BL-modern\Launcher.App.exe", appImagePath: string.Empty);
    Expect("windows-путь не считается пакетом", windows.Kind != LauncherInstallationKind.SystemPackage, windows.Kind.ToString());
    Expect("windows обновляет себя сам", windows.CanSelfUpdate, "да");

    // Выбор пакета из манифеста. Старое поле PackageUrl обязано продолжать работать.
    var launcherInfo = new ManifestLauncherInfo { PackageUrl = "https://bl-modern.ru/download/launcher.zip", Sha256 = "ABC" };
    var legacy = LauncherInstallation.ResolvePackage(launcherInfo, LauncherInstallationKind.Portable);
    Expect("старый PackageUrl работает", legacy?.Url == "https://bl-modern.ru/download/launcher.zip", legacy?.Url ?? "(нет)");

    // Системному пакету общий zip не подходит — он его не получает.
    var forPackage = LauncherInstallation.ResolvePackage(launcherInfo, LauncherInstallationKind.SystemPackage);
    Expect("системному пакету общий zip не отдаётся", forPackage is null, forPackage?.Url ?? "(нет)");

    launcherInfo.PackagesByInstall["appimage"] = new ManifestLauncherPackage { Url = "https://bl-modern.ru/download/BL-modern.AppImage", Sha256 = "DEF" };
    launcherInfo.PackagesByInstall["package"] = new ManifestLauncherPackage { DownloadPageUrl = "https://bl-modern.ru/download/" };

    var forAppImage = LauncherInstallation.ResolvePackage(launcherInfo, LauncherInstallationKind.AppImage);
    Expect("для AppImage берётся свой пакет", forAppImage?.Url.EndsWith(".AppImage") == true, forAppImage?.Url ?? "(нет)");

    var pageForPackage = LauncherInstallation.ResolvePackage(launcherInfo, LauncherInstallationKind.SystemPackage);
    Expect("для пакета есть страница загрузки",
        !string.IsNullOrWhiteSpace(pageForPackage?.DownloadPageUrl), pageForPackage?.DownloadPageUrl ?? "(нет)");

    Console.WriteLine($"UPDATE_PLAN_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Механика самообновления AppImage: SmokeTest --appimage-update (только Unix, без сети)
// Проверяем не загрузку, а самое опасное: подменяется ли файл образа и запускается ли новый.
// Ошибка здесь оставляет игрока без лаунчера вообще.
if (args.Length >= 1 && args[0].Equals("--appimage-update", StringComparison.OrdinalIgnoreCase))
{
    if (OperatingSystem.IsWindows())
    {
        Console.WriteLine("APPIMAGE_UPDATE_RESULT=SKIP (только Linux/macOS)");
        return;
    }

    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    var sandbox = Path.Combine(Path.GetTempPath(), "bl-appimage-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(sandbox);

    // «Старый образ» и «новый образ» — обычные скрипты: так видно, какой из них запустился.
    var target = Path.Combine(sandbox, "BL-modern.AppImage");
    var marker = Path.Combine(sandbox, "started.txt");
    File.WriteAllText(target, "#!/bin/sh\necho OLD > " + marker + "\n");
    File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    var package = Path.Combine(sandbox, "downloaded.AppImage");
    File.WriteAllText(package, "#!/bin/sh\necho NEW > " + marker + "\n");

    // Процесс, завершения которого будет ждать скрипт обновления.
    using var placeholder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"sleep 1\""));
    new LauncherSelfUpdateService(new HttpClient())
        .ApplyAppImageUpdateAndRestart(package, target, placeholder!.Id);

    await placeholder.WaitForExitAsync();

    // Скрипт ждёт выхода процесса, потом меняет файл и запускает новый образ.
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline && (!File.Exists(marker) || File.ReadAllText(marker).Trim() != "NEW"))
    {
        await Task.Delay(300);
    }

    Expect("новый образ запущен", File.Exists(marker) && File.ReadAllText(marker).Trim() == "NEW",
        File.Exists(marker) ? File.ReadAllText(marker).Trim() : "(маркера нет)");
    Expect("файл образа заменён", File.Exists(target) && File.ReadAllText(target).Contains("NEW"),
        File.Exists(target) ? "содержит NEW" : "(файла нет)");
    Expect("бит запуска сохранён",
        File.Exists(target) && (File.GetUnixFileMode(target) & UnixFileMode.UserExecute) != 0,
        File.Exists(target) ? File.GetUnixFileMode(target).ToString() : "(файла нет)");
    Expect("скачанный файл не остался мусором", !File.Exists(package), package);

    try { Directory.Delete(sandbox, true); } catch { /* временная папка */ }

    Console.WriteLine($"APPIMAGE_UPDATE_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Механика самообновления бандла .app: SmokeTest --macbundle-update (только Unix, без сети)
// Проверяем замену каталога целиком: точечная правка внутри бандла ломает подпись,
// а неудачная замена оставляет игрока без приложения.
if (args.Length >= 1 && args[0].Equals("--macbundle-update", StringComparison.OrdinalIgnoreCase))
{
    if (OperatingSystem.IsWindows())
    {
        Console.WriteLine("MACBUNDLE_UPDATE_RESULT=SKIP (только Linux/macOS)");
        return;
    }

    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    var sandbox = Path.Combine(Path.GetTempPath(), "bl-bundle-" + Guid.NewGuid().ToString("N")[..8]);
    var bundle = Path.Combine(sandbox, "BL-modern.app");
    Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
    File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), "<plist>OLD</plist>");
    File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "Launcher.Avalonia"), "OLD");

    // Файл игрока внутри бандла быть не должен, но проверим, что старый бандл именно ЗАМЕНЯЕТСЯ,
    // а не смешивается с новым: иначе после обновления остаются файлы прошлой версии.
    File.WriteAllText(Path.Combine(bundle, "Contents", "stale.txt"), "мусор прошлой версии");

    // Готовим «скачанный» архив: внутри — новый бандл.
    var newBundleRoot = Path.Combine(sandbox, "staging");
    var newBundle = Path.Combine(newBundleRoot, "BL-modern.app");
    Directory.CreateDirectory(Path.Combine(newBundle, "Contents", "MacOS"));
    File.WriteAllText(Path.Combine(newBundle, "Contents", "Info.plist"), "<plist>NEW</plist>");
    File.WriteAllText(Path.Combine(newBundle, "Contents", "MacOS", "Launcher.Avalonia"), "NEW");

    var package = Path.Combine(sandbox, "update.zip");
    System.IO.Compression.ZipFile.CreateFromDirectory(newBundleRoot, package);
    Directory.Delete(newBundleRoot, true);

    using var placeholder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"sleep 1\""));
    new LauncherSelfUpdateService(new HttpClient())
        .ApplyMacBundleUpdateAndRestart(package, bundle, placeholder!.Id);

    await placeholder.WaitForExitAsync();

    var plist = Path.Combine(bundle, "Contents", "Info.plist");
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline && (!File.Exists(plist) || !File.ReadAllText(plist).Contains("NEW")))
    {
        await Task.Delay(300);
    }

    Expect("бандл на месте", Directory.Exists(bundle), bundle);
    Expect("содержимое обновилось", File.Exists(plist) && File.ReadAllText(plist).Contains("NEW"),
        File.Exists(plist) ? File.ReadAllText(plist) : "(нет Info.plist)");
    Expect("файлы прошлой версии убраны", !File.Exists(Path.Combine(bundle, "Contents", "stale.txt")), "stale.txt удалён");
    Expect("временная копия .old не осталась", !Directory.Exists(bundle + ".old"), bundle + ".old");

    try { Directory.Delete(sandbox, true); } catch { /* временная папка */ }

    Console.WriteLine($"MACBUNDLE_UPDATE_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Интерфейсные звуки: SmokeTest --sound
// Проверка на слух — синтез общий, а вот проигрыватель у каждой ОС свой
// (winmm на Windows, afplay/paplay/aplay на Unix).
if (args.Length >= 1 && args[0].Equals("--sound", StringComparison.OrdinalIgnoreCase))
{
    var sounds = new LauncherSoundService { Enabled = true };

    Console.WriteLine("Звук готовности сборки...");
    sounds.PlayReady();
    await Task.Delay(1500);

    Console.WriteLine("Звук достижения...");
    sounds.PlayAchievement();
    await Task.Delay(2000);

    Console.WriteLine("SOUND_DONE (если тишина — в системе нет проигрывателя или звукового выхода)");
    return;
}

// Разбор крашей игры: SmokeTest --crash (без сети)
// Проверяем распознавание причин по логам и расшифровку кодов выхода. Отдельный интерес —
// Linux/macOS: там другие имена драйверов и другие коды (128+сигнал).
if (args.Length >= 1 && args[0].Equals("--crash", StringComparison.OrdinalIgnoreCase))
{
    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    // Готовим папку сборки с логом, как её видит анализатор: <install>/logs/latest.log.
    var sandbox = Path.Combine(Path.GetTempPath(), "bl-crash-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(Path.Combine(sandbox, "logs"));

    CrashAnalysisResult AnalyzeLog(string logText, int exitCode)
    {
        File.WriteAllText(Path.Combine(sandbox, "logs", "latest.log"), logText);
        return CrashAnalyzerService.Analyze(sandbox, exitCode);
    }

    // Linux: драйверы. На Windows такие строки не встречаются, поэтому списки не пересекаются.
    var nvidiaLinux = AnalyzeLog("# Problematic frame:\n# C  [libnvidia-glcore.so.550.90+0x1234]", 139);
    Expect("NVIDIA на Linux распознан", nvidiaLinux.Category == "graphics_driver", nvidiaLinux.Category);
    Expect("совет про prime-run, а не про javaw.exe",
        nvidiaLinux.Summary.Contains("prime-run") && !nvidiaLinux.Summary.Contains("javaw"),
        nvidiaLinux.Summary[..Math.Min(70, nvidiaLinux.Summary.Length)]);

    var mesa = AnalyzeLog("# C  [radeonsi_dri.so+0x99]", 139);
    Expect("Mesa/AMD на Linux распознан", mesa.Category == "graphics_driver", mesa.Category);

    var swrast = AnalyzeLog("[main/INFO]: OpenGL renderer: llvmpipe (LLVM 15, 256 bits) swrast", 0);
    Expect("программный рендеринг распознан", swrast.Category == "graphics_software_render", swrast.Category);

    var display = AnalyzeLog("GLFW error 65550: X11: Failed to open display :0", 1);
    Expect("нет дисплея X11", display.Category == "graphics_display", display.Category);

    // macOS
    var firstThread = AnalyzeLog("Exception in thread \"main\" java.lang.IllegalStateException: " +
        "Please run the JVM with -XstartOnFirstThread", 1);
    Expect("macOS: -XstartOnFirstThread", firstThread.Category == "macos_first_thread", firstThread.Category);

    var wrongArch = AnalyzeLog("dyld: no suitable image found. mach-o, but wrong architecture", 1);
    Expect("macOS: не та архитектура Java", wrongArch.Category == "java_wrong_arch", wrongArch.Category);

    // Регресс: windows-паттерны продолжают работать.
    var nvidiaWindows = AnalyzeLog("# C  [nvoglv64.dll+0x8a1b2]", unchecked((int)0xC0000005));
    Expect("NVIDIA на Windows не сломался", nvidiaWindows.Category == "graphics_driver", nvidiaWindows.Category);

    // Коды выхода. На Unix это 128+сигнал, кодов NTSTATUS там не бывает.
    var exit139 = CrashAnalyzerService.DescribeExitCode(139);
    var exit137 = CrashAnalyzerService.DescribeExitCode(137);
    if (HostPlatform.IsWindows)
    {
        Expect("Windows: 0xC0000005 расшифрован",
            CrashAnalyzerService.DescribeExitCode(unchecked((int)0xC0000005)).Contains("ACCESS_VIOLATION"),
            CrashAnalyzerService.DescribeExitCode(unchecked((int)0xC0000005)));
        Expect("Windows: код 139 остаётся числом", exit139 == "код 139", exit139);
    }
    else
    {
        Expect("Unix: 139 → SIGSEGV", exit139.Contains("SIGSEGV"), exit139);
        Expect("Unix: 137 → нехватка памяти", exit137.Contains("SIGKILL") && exit137.Contains("памяти"), exit137);
    }

    // Приватность: в «уликах» не должно остаться ни windows-пути, ни домашней папки Unix —
    // они уезжают в телеметрию.
    var pathLeak = AnalyzeLog("# C  [libnvidia-glcore.so] loaded from /home/ivan/games/.launcher/runtime", 139);
    Expect("домашняя папка Unix вычищена из улик",
        !pathLeak.Evidence.Contains("/home/ivan"), pathLeak.Evidence);

    var winPathLeak = AnalyzeLog(@"# C  [nvoglv64.dll+0x1] C:\Users\Ivan\forge\mods", unchecked((int)0xC0000005));
    Expect("windows-путь вычищен из улик",
        !winPathLeak.Evidence.Contains(@"C:\Users"), winPathLeak.Evidence);

    try { Directory.Delete(sandbox, true); } catch { /* временная папка */ }

    Console.WriteLine($"CRASH_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Прямой пинг MC-серверов: SmokeTest --ping <host> [host2 ...]
if (args.Length >= 1 && args[0].Equals("--ping", StringComparison.OrdinalIgnoreCase))
{
    var hosts = args.Skip(1).ToArray();
    if (hosts.Length == 0) { hosts = ["play.bl-modern.ru", "tfgm2.bl-modern.ru"]; }
    var pinger = new MinecraftServerPinger();
    foreach (var host in hosts)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var r = await pinger.PingAsync(host, cts.Token);
        Console.WriteLine(r is null
            ? $"  {host} -> НЕДОСТУПЕН"
            : $"  {host} -> ONLINE players={r.PlayersOnline}/{r.PlayersMax} version='{r.Version}'");
    }
    return;
}

// Проверка создания ярлыка на рабочем столе (создаёт, проверяет, удаляет).
if (args.Length >= 1 && args[0].Equals("--shortcut", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        var path = DesktopShortcutService.Create("BLM Shortcut SelfTest");
        var exists = File.Exists(path);
        var sizeOk = exists && new FileInfo(path).Length > 0;
        Console.WriteLine($"SHORTCUT_CREATED path={path}");
        Console.WriteLine($"SHORTCUT_EXISTS={exists} nonEmpty={sizeOk}");
        if (exists) { File.Delete(path); Console.WriteLine($"cleaned up, stillExists={File.Exists(path)}"); }
        Console.WriteLine(exists && sizeOk ? "SHORTCUT_RESULT=PASS" : "SHORTCUT_RESULT=FAIL");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SHORTCUT_RESULT=FAIL ({ex.Message})");
    }
    return;
}

// Юнит-проверки логики выбора папки установки (без сети/диска, кроме маркеров во временной папке).
if (args.Length >= 1 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
{
    RunResolveSelfTest();
    return;
}

// Описание системы для телеметрии: SmokeTest --platform
// Нужен потому, что сайт считает игроков по строке os_version. Если Linux и macOS отдадут
// неразличимые строки (как было с «Unix 6.6.87»), в админке они сольются в одну графу.
if (args.Length >= 1 && args[0].Equals("--platform", StringComparison.OrdinalIgnoreCase))
{
    var passed = 0;
    var failed = 0;
    void Check(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { passed++; } else { failed++; }
    }

    var description = HostPlatform.OsDescription;
    Console.WriteLine($"os_version = «{description}»  семейство = {HostPlatform.OsFamilyName}");

    Check("описание непустое", !string.IsNullOrWhiteSpace(description), description);
    // 64 — длина колонки os_version в launcher_telemetry_events; сайт молча обрежет остальное.
    Check("влезает в колонку сайта (≤64)", description.Length <= 64, $"{description.Length} символов");

    var expectedPrefix = HostPlatform.IsWindows ? "Microsoft Windows" : HostPlatform.IsMacOS ? "macOS" : "Linux";
    Check("система узнаётся по началу строки",
        description.StartsWith(expectedPrefix, StringComparison.Ordinal),
        $"ожидали «{expectedPrefix}…»");

    // Главное свойство: строка НЕ должна начинаться с «Unix» — именно из-за этого
    // Linux и macOS были неотличимы в отчёте.
    Check("не «Unix» (Linux и macOS различимы)",
        !description.StartsWith("Unix", StringComparison.OrdinalIgnoreCase),
        description);

    if (HostPlatform.IsWindows)
    {
        // Регресс: на Windows значение обязано остаться ровно прежним, иначе в статистике
        // сайта появится вторая группа тех же самых игроков.
        Check("на Windows значение не изменилось",
            description == Environment.OSVersion.VersionString,
            Environment.OSVersion.VersionString);
    }
    else
    {
        // Повторный вызов идёт из кэша (sw_vers/os-release читаются один раз за запуск).
        Check("значение стабильно между вызовами", description == HostPlatform.OsDescription, "совпало");
    }

    Console.WriteLine($"PLATFORM_RESULT={(failed == 0 ? "PASS" : "FAIL")} ok={passed} fail={failed}");
    Environment.ExitCode = failed == 0 ? 0 : 1;
    return;
}

// Проверка встроенных ресурсов (без сети): SmokeTest --embedded
// Ресурсы лежат в той же сборке, что и читающий их код (Assembly.GetExecutingAssembly()).
// После переезда кода в Launcher.Core такая ошибка иначе всплыла бы только у игрока в рантайме.
if (args.Length >= 1 && args[0].Equals("--embedded", StringComparison.OrdinalIgnoreCase))
{
    var okCount = 0;
    var failCount = 0;
    void Report(string name, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {name}: {detail}");
        if (ok) { okCount++; } else { failCount++; }
    }

    try
    {
        var embedded = await new ModpackManifestClient(new HttpClient()).GetEmbeddedDefaultManifestAsync();
        Report("встроенный modpack-manifest", true, $"id={embedded.Modpack.Id} версия сборки={embedded.Modpack.Version}");
    }
    catch (Exception exception)
    {
        Report("встроенный modpack-manifest", false, exception.Message);
    }

    try
    {
        // Раскладывает шаблон servers.dat из встроенного ресурса во временную папку.
        var work = Path.Combine(Path.GetTempPath(), "blm-embedded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            MultiplayerServerListService.EnsureServer(work, "BL-modern", "play.bl-modern.ru");
            var serversDat = Path.Combine(work, "servers.dat");
            Report("встроенный servers.dat", File.Exists(serversDat), File.Exists(serversDat)
                ? $"{new FileInfo(serversDat).Length} байт"
                : "файл не создан");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }
    catch (Exception exception)
    {
        Report("встроенный servers.dat", false, exception.Message);
    }

    Console.WriteLine($"EMBEDDED_RESULT={(failCount == 0 ? "PASS" : "FAIL")} ok={okCount} fail={failCount}");
    Environment.ExitCode = failCount == 0 ? 0 : 1;
    return;
}

// Цепочка «сайт → кэш → вшитый резерв» для манифеста сборки: SmokeTest --manifest-fallback
// Сети не требует: недостижимый хост эмулирует падение сайта. Логика вынесена из MainWindow
// в ядро, поэтому проверяется без запуска окна.
if (args.Length >= 1 && args[0].Equals("--manifest-fallback", StringComparison.OrdinalIgnoreCase))
{
    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var resolver = new ModpackManifestResolver(new ModpackManifestClient(http));
    // Имя заведомо не резолвится — попадаем ровно в ту ветку, что и при недоступном сайте.
    const string deadUrl = "https://manifest-host-that-does-not-exist.invalid/modpack-manifest.json";

    var work = Path.Combine(Path.GetTempPath(), "blm-manifest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try
    {
        // 1. Кэша нет → должен приехать вшитый резерв, а не исключение.
        var emptyCache = Path.Combine(work, "absent.json");
        var embedded = await resolver.ResolveAsync(deadUrl, emptyCache);
        Expect("нет сайта и нет кэша → вшитый резерв",
            embedded.Source == ModpackManifestSource.Embedded,
            $"источник={embedded.Source}, id={embedded.Manifest.Modpack.Id}");

        // 2. Кэш есть → берём его, а не вшитый (он свежее).
        var cachePath = Path.Combine(work, "cached.json");
        var cachedManifest = embedded.Manifest;
        cachedManifest.Modpack.Version = "9.9.9-из-кэша";
        await new ModpackManifestClient(http).SaveManifestCacheAsync(cachedManifest, cachePath);

        var cached = await resolver.ResolveAsync(deadUrl, cachePath);
        Expect("нет сайта, есть кэш → кэш",
            cached.Source == ModpackManifestSource.Cached && cached.Manifest.Modpack.Version == "9.9.9-из-кэша",
            $"источник={cached.Source}, версия={cached.Manifest.Modpack.Version}");

        // 3. Причина отказа доезжает до вызывающего — по ней окно выбирает текст для игрока.
        Expect("причина отказа заполнена", !string.IsNullOrWhiteSpace(cached.Reason), cached.Reason ?? "(пусто)");

        // 4. Манифест запуска для режима архива: имя Java обязано быть под текущую ОС.
        var launch = LauncherManifestFactory.CreateArchiveModeLaunchManifest(null, null);
        Expect("Java в манифесте запуска — под текущую ОС",
            launch.Game.JavaExecutable == HostPlatform.JavawExecutableName,
            launch.Game.JavaExecutable);
    }
    finally
    {
        try { Directory.Delete(work, true); } catch { }
    }

    Console.WriteLine($"MANIFEST_FALLBACK_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Логика каталога сборок и маркера установки: SmokeTest --catalog
// Ни сети, ни окна не требует — всё вынесено из MainWindow в ядро.
if (args.Length >= 1 && args[0].Equals("--catalog", StringComparison.OrdinalIgnoreCase))
{
    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    var work = Path.Combine(Path.GetTempPath(), "blm-catalog-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try
    {
        // Маркер установки: <id>:<версия>
        var installed = Path.Combine(work, "tfgm-here");
        Directory.CreateDirectory(Path.Combine(installed, ".launcher"));
        File.WriteAllText(Path.Combine(installed, ".launcher", "modpack.version"), "tfgm:0.13.4");

        Expect("маркер: своя сборка найдена", ModpackInstallMarker.IsInstalledAt(installed, "tfgm"), installed);
        Expect("маркер: чужая сборка не считается своей", !ModpackInstallMarker.IsInstalledAt(installed, "stoneblock4"), "stoneblock4");
        Expect("маркер: версия прочитана", ModpackInstallMarker.ReadVersion(installed, "tfgm") == "0.13.4",
            ModpackInstallMarker.ReadVersion(installed, "tfgm") ?? "(null)");
        Expect("маркер: пустой путь не роняет", !ModpackInstallMarker.IsInstalledAt(null, "tfgm"), "null → false");
        Expect("маркер: папки нет → не установлена",
            !ModpackInstallMarker.IsInstalledAt(Path.Combine(work, "нет-такой"), "tfgm"), "несуществующая папка");

        // Выбор сборки при запуске
        var packs = new List<CatalogModpackEntry>
        {
            new() { Id = "tfgm", Name = "TFG Modern" },
            new() { Id = "stoneblock4", Name = "StoneBlock 4" }
        };
        Expect("каталог: берётся ранее выбранная", ModpackCatalogLogic.ChoosePreferred(packs, "stoneblock4")?.Id == "stoneblock4", "stoneblock4");
        Expect("каталог: неизвестный id → первая", ModpackCatalogLogic.ChoosePreferred(packs, "нет-такой")?.Id == "tfgm", "tfgm");
        Expect("каталог: пустой список → null", ModpackCatalogLogic.ChoosePreferred([], "tfgm") is null, "null");

        // Изоляция папок включается ТОЛЬКО при нескольких сборках: иначе одиночным
        // пользователям сменилась бы папка установки и сборка перекачалась бы заново.
        Expect("каталог: одна сборка → без изоляции папок",
            !ModpackCatalogLogic.ShouldIsolateInstallRoots([packs[0]]), "1 сборка");
        Expect("каталог: несколько сборок → изоляция папок",
            ModpackCatalogLogic.ShouldIsolateInstallRoots(packs), "2 сборки");

        // Состояние метки в списке
        var settings = new UserSettings();
        settings.ModpackInstallRoots["stoneblock4"] = installed; // там лежит tfgm, значит не установлена
        Expect("состояние: выбранная + кнопка «Установить» → не установлена",
            ModpackCatalogLogic.GetInstallState("tfgm", "tfgm", PrimaryActionState.Install, settings) == ModpackInstallState.NotInstalled, "NotInstalled");
        Expect("состояние: выбранная + кнопка «Обновить» → есть обновление",
            ModpackCatalogLogic.GetInstallState("tfgm", "tfgm", PrimaryActionState.Update, settings) == ModpackInstallState.UpdateAvailable, "UpdateAvailable");
        Expect("состояние: чужая папка с другой сборкой → не установлена",
            ModpackCatalogLogic.GetInstallState("stoneblock4", "tfgm", PrimaryActionState.Play, settings) == ModpackInstallState.NotInstalled, "NotInstalled");
        Expect("состояние: папка неизвестна → метки нет",
            ModpackCatalogLogic.GetInstallState("другая", "tfgm", PrimaryActionState.Play, settings) == ModpackInstallState.Unknown, "Unknown");
    }
    finally
    {
        try { Directory.Delete(work, true); } catch { }
    }

    Console.WriteLine($"CATALOG_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Состояние главной кнопки: SmokeTest --primary-action
// Логика тонкая и уже стоила игрокам лишних перекачек, поэтому закрыта тестами целиком.
if (args.Length >= 1 && args[0].Equals("--primary-action", StringComparison.OrdinalIgnoreCase))
{
    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    // Конфигурация в режиме прямого архива — только тогда кнопка вообще меняет состояние.
    var config = new LauncherConfiguration { ModpackManifestUrl = "https://example.invalid/m.json" };
    ModpackManifest Manifest(string id, string version, bool force = false, bool allowDowngrade = false)
    {
        var m = new ModpackManifest();
        m.Modpack.Id = id;
        m.Modpack.Version = version;
        m.Updates.ForceReinstall = force;
        m.Updates.AllowDowngrade = allowDowngrade;
        return m;
    }

    var manifest = Manifest("tfgm", "0.13.4");

    Expect("маркера нет → Установить",
        PrimaryActionResolver.Resolve(manifest, config, null, false) == PrimaryActionState.Install, "Install");
    Expect("версии совпали → Играть",
        PrimaryActionResolver.Resolve(manifest, config, "tfgm:0.13.4", false) == PrimaryActionState.Play, "Play");
    Expect("установлена старее → Обновить",
        PrimaryActionResolver.Resolve(manifest, config, "tfgm:0.13.1", false) == PrimaryActionState.Update, "Update");

    // Ключевой случай: каталог не ответил, читается манифест со СТАРОЙ версией, а у игрока
    // стоит новее. Откатывать нечего — кнопка обязана звать «Играть», иначе она бесконечно
    // предлагала бы обновление вхолостую.
    Expect("установлена НОВЕЕ манифеста → Играть (без отката)",
        PrimaryActionResolver.Resolve(Manifest("tfgm", "0.13.1"), config, "tfgm:0.13.4", false) == PrimaryActionState.Play, "Play");
    Expect("установлена новее, но откат разрешён → Обновить",
        PrimaryActionResolver.Resolve(Manifest("tfgm", "0.13.1", allowDowngrade: true), config, "tfgm:0.13.4", false) == PrimaryActionState.Update, "Update");

    Expect("forceReinstall → Обновить даже при совпадении версий",
        PrimaryActionResolver.Resolve(Manifest("tfgm", "0.13.4", force: true), config, "tfgm:0.13.4", false) == PrimaryActionState.Update, "Update");
    Expect("обновление лаунчера важнее всего",
        PrimaryActionResolver.Resolve(manifest, config, "tfgm:0.13.4", true) == PrimaryActionState.LauncherUpdate, "LauncherUpdate");

    // Чужая сборка в папке: номера версий разных сборок несравнимы, поэтому защита от отката
    // на них не действует. Раньше маркер «stoneblock4:1.0.0» при выбранной tfgm 0.13.4 читался
    // как «установлено новее» и кнопка врала «Играть». Теперь при несовпадении id — «Установить».
    Expect("чужая сборка с бОльшим номером → Установить",
        PrimaryActionResolver.Resolve(manifest, config, "stoneblock4:1.0.0", false) == PrimaryActionState.Install, "Install");
    Expect("чужая сборка с меньшим номером → Установить",
        PrimaryActionResolver.Resolve(manifest, config, "stoneblock4:0.1.0", false) == PrimaryActionState.Install, "Install");
    Expect("чужая сборка + allowDowngrade → всё равно Установить",
        PrimaryActionResolver.Resolve(Manifest("tfgm", "0.13.4", allowDowngrade: true), config, "stoneblock4:1.0.0", false) == PrimaryActionState.Install, "Install");
    Expect("маркер без id не считается чужой сборкой (откат не предлагается)",
        PrimaryActionResolver.Resolve(Manifest("tfgm", "0.13.1"), config, "0.13.4", false) == PrimaryActionState.Play, "Play");

    Expect("ожидаемая версия = id:версия",
        PrimaryActionResolver.GetExpectedArchiveVersion(manifest, config) == "tfgm:0.13.4",
        PrimaryActionResolver.GetExpectedArchiveVersion(manifest, config));

    // Сравнение версий лаунчера
    Expect("1.3.2 новее 1.3.1", PrimaryActionResolver.IsRemoteVersionNewer("1.3.1", "1.3.2"), "true");
    Expect("1.3.2 не новее себя", !PrimaryActionResolver.IsRemoteVersionNewer("1.3.2", "1.3.2"), "false");
    Expect("откат лаунчера не предлагается", !PrimaryActionResolver.IsRemoteVersionNewer("1.3.2", "1.3.1"), "false");
    // Суффикс +git ломал разбор версии — бутстраппер из-за него обновлялся бесконечно.
    Expect("суффикс +git отбрасывается",
        !PrimaryActionResolver.IsRemoteVersionNewer("1.3.2+abc123", "1.3.2"), "false");

    var launcherManifest = Manifest("tfgm", "0.13.4");
    launcherManifest.Launcher.Version = "1.3.9";
    Expect("нет packageUrl → обновления лаунчера нет",
        !PrimaryActionResolver.IsLauncherUpdateAvailable(launcherManifest, "1.3.2", out _), "packageUrl пуст");
    launcherManifest.Launcher.PackageUrl = "https://example.invalid/launcher.zip";
    Expect("есть packageUrl и версия новее → обновление лаунчера",
        PrimaryActionResolver.IsLauncherUpdateAvailable(launcherManifest, "1.3.2", out _), "1.3.9 > 1.3.2");

    Console.WriteLine($"PRIMARY_ACTION_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Изоляция тестового профиля: SmokeTest --profile-isolation
// Главное, что проверяем: БЕЗ ключа --profile пути обязаны остаться ровно прежними,
// иначе у игроков «потеряются» настройки и сборка.
if (args.Length >= 1 && args[0].Equals("--profile-isolation", StringComparison.OrdinalIgnoreCase))
{
    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    var config = new LauncherConfiguration();
    // Своя папка игрока должна быть абсолютной для текущей ОС: "C:\forge" на Unix — это
    // относительное имя, и путь достроился бы от рабочего каталога.
    var playerRoot = HostPlatform.IsWindows ? @"C:\forge" : "/opt/forge";
    var settings = new UserSettings { InstallRoot = playerRoot };
    var manifest = new ModpackManifest();
    manifest.Modpack.Id = "tfgm";
    manifest.Install.Root = @"%AppData%\ForgeLauncher";

    // 1. Обычный режим — всё как раньше.
    LauncherProfile.Use(null);
    Expect("без профиля: папка данных прежняя", LauncherProfile.DataFolderName == "ForgeLauncher", LauncherProfile.DataFolderName);
    var normalSettings = config.GetUserSettingsPath();
    Expect("без профиля: настройки в ForgeLauncher",
        normalSettings == Path.Combine(appData, "ForgeLauncher", ".launcher", "user-settings.json"), normalSettings);
    var normalRoot = ModpackCatalogLogic.ResolveEffectiveInstallRoot(config, manifest, settings);
    Expect("без профиля: путь установки игрока сохранён", normalRoot == playerRoot, normalRoot);

    // 2. Изолированный профиль — и настройки, и установка уходят в сторону.
    LauncherProfile.Use("test");
    Expect("с профилем: папка данных отдельная", LauncherProfile.DataFolderName == "ForgeLauncher-test", LauncherProfile.DataFolderName);
    var testSettings = config.GetUserSettingsPath();
    Expect("с профилем: настройки отдельные", testSettings.Contains("ForgeLauncher-test"), testSettings);
    var testRoot = ModpackCatalogLogic.ResolveEffectiveInstallRoot(config, manifest, settings);
    Expect("с профилем: установка НЕ в C:\\forge", !testRoot.Equals(@"C:\forge", StringComparison.OrdinalIgnoreCase), testRoot);
    Expect("с профилем: установка внутри папки профиля", testRoot.Contains("ForgeLauncher-test"), testRoot);

    // ГЛАВНОЕ. Установщики (ArchiveInstallService, RuntimeInstallService) считают папку сами,
    // вызывая UserSettings.ResolveInstallRoot напрямую. Первая версия изоляции проверяла только
    // обёртку выше по стеку — и реальная установка ушла в рабочую папку игрока.
    // Проверяем именно тот вызов, который делают установщики.
    var installerRoot = settings.ResolveInstallRoot(manifest.Install.Root, config.GetDistributionRoot());
    Expect("с профилем: путь УСТАНОВЩИКА изолирован",
        installerRoot.Contains("ForgeLauncher-test"), installerRoot);
    Expect("с профилем: путь установщика не ведёт в рабочую папку",
        !installerRoot.Equals(@"C:\forge", StringComparison.OrdinalIgnoreCase)
        && !installerRoot.TrimEnd('\\').EndsWith("ForgeLauncher", StringComparison.OrdinalIgnoreCase),
        installerRoot);

    var installerRootPerPack = settings.ResolveInstallRoot(manifest.Install.Root, config.GetDistributionRoot(), "tfgm");
    Expect("с профилем: путь установщика для сборки в подпапке профиля",
        installerRootPerPack.Contains("ForgeLauncher-test") && installerRootPerPack.EndsWith("tfgm"),
        installerRootPerPack);

    // 3. Разбор командной строки и защита от мусора в имени.
    LauncherProfile.Use(null);
    LauncherProfile.UseFromCommandLine(["Launcher.exe", "--profile=demo"]);
    Expect("ключ --profile=demo распознан", LauncherProfile.Name == "demo", LauncherProfile.Name ?? "(null)");
    LauncherProfile.Use("../evil");
    Expect("разделители в имени вычищены",
        LauncherProfile.Name is not null && !LauncherProfile.Name.Contains('/') && !LauncherProfile.Name.Contains('\\'),
        LauncherProfile.Name ?? "(null)");

    // 4. Возврат в обычный режим не оставляет следов.
    LauncherProfile.Use(null);
    Expect("сброс профиля возвращает прежние пути", config.GetUserSettingsPath() == normalSettings, config.GetUserSettingsPath());

    Console.WriteLine($"PROFILE_ISOLATION_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Разбор Markdown новостей: SmokeTest --markdown
// Разбор общий для WPF и Avalonia, поэтому расхождение здесь = новости выглядят
// по-разному на Windows и на Linux.
if (args.Length >= 1 && args[0].Equals("--markdown", StringComparison.OrdinalIgnoreCase))
{
    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    var blocks = MarkdownParser.Parse("# Заголовок\n\nАбзац с **жирным** и *курсивом*.\n\n- пункт один\n- пункт два\n\n1. первый\n\n> цитата\n\n---\n\n```\nкод\n```");
    Expect("заголовок распознан", blocks[0].Kind == MarkdownBlockKind.Heading && blocks[0].HeadingLevel == 1, $"{blocks[0].Kind} ур.{blocks[0].HeadingLevel}");
    Expect("абзац распознан", blocks[1].Kind == MarkdownBlockKind.Paragraph, blocks[1].Kind.ToString());
    Expect("маркированный список", blocks[2].Kind == MarkdownBlockKind.ListItem && blocks[2].ListMarker == "•", blocks[2].ListMarker);
    Expect("нумерованный список", blocks.Any(b => b.ListMarker == "1."), "1.");
    Expect("цитата", blocks.Any(b => b.Kind == MarkdownBlockKind.Quote), "Quote");
    Expect("линейка", blocks.Any(b => b.Kind == MarkdownBlockKind.Rule), "Rule");
    var code = blocks.FirstOrDefault(b => b.Kind == MarkdownBlockKind.CodeBlock);
    Expect("блок кода", code is not null && code.RawText.Contains("код"), code?.RawText ?? "(нет)");

    // Строчная разметка — та самая, где алгоритм переключателей отличается от «выделить кусок».
    var inl = MarkdownParser.ParseInlines("обычный **жирный** *курсив* ~~зачёркнутый~~ `код`");
    Expect("жирный", inl.Any(i => i.Bold && i.Text.Contains("жирный")), "есть");
    Expect("курсив", inl.Any(i => i.Italic && i.Text.Contains("курсив")), "есть");
    Expect("зачёркнутый", inl.Any(i => i.Strikethrough && i.Text.Contains("зачёркнутый")), "есть");
    Expect("код", inl.Any(i => i.Code && i.Text.Contains("код")), "есть");

    // Вложенность: маркеры переключают состояние, поэтому «b» должен быть И жирным, И курсивом.
    var nested = MarkdownParser.ParseInlines("**a *b* c**");
    Expect("вложенность: b жирный и курсив",
        nested.Any(i => i.Text.Trim() == "b" && i.Bold && i.Italic),
        string.Join(" | ", nested.Select(i => $"'{i.Text}'{(i.Bold ? "B" : "")}{(i.Italic ? "I" : "")}")));

    // Одиночная звёздочка не должна съедать остаток строки.
    var single = MarkdownParser.ParseInlines("2 * 2 = 4");
    Expect("одиночная звёздочка — обычный текст",
        string.Concat(single.Select(i => i.Text)) == "2 * 2 = 4",
        string.Concat(single.Select(i => i.Text)));

    // Экранирование
    var escaped = MarkdownParser.ParseInlines(@"\*не курсив\*");
    Expect("экранирование \\*", string.Concat(escaped.Select(i => i.Text)) == "*не курсив*" && !escaped.Any(i => i.Italic),
        string.Concat(escaped.Select(i => i.Text)));

    // Ссылки
    var link = MarkdownParser.ParseInlines("текст [сайт](https://bl-modern.ru) дальше");
    Expect("ссылка выделена", link.Any(i => i.LinkUrl == "https://bl-modern.ru" && i.Text == "сайт"),
        link.FirstOrDefault(i => i.LinkUrl is not null)?.LinkUrl ?? "(нет)");

    // Картинка вырезается из текста и отдаётся отдельно
    var rest = MarkdownParser.ExtractFirstImage("до ![alt](https://site/img.png) после", out var img);
    Expect("картинка извлечена", img == "https://site/img.png", img);
    Expect("разметка картинки убрана из текста", !rest.Contains("!["), rest.Trim());

    Console.WriteLine($"MARKDOWN_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Логика списка серверов: SmokeTest --servers  (без сети)
if (args.Length >= 1 && args[0].Equals("--servers", StringComparison.OrdinalIgnoreCase))
{
    var ok = 0;
    var bad = 0;
    void Expect(string name, bool condition, string detail)
    {
        Console.WriteLine($"  [{(condition ? "OK  " : "FAIL")}] {name}: {detail}");
        if (condition) { ok++; } else { bad++; }
    }

    // Склонения — самое хрупкое: ошибка не ломает работу, но видна каждому игроку.
    var cases = new (int Count, string Expected)[]
    {
        (0, "игроков"), (1, "игрок"), (2, "игрока"), (4, "игрока"), (5, "игроков"),
        (11, "игроков"), (12, "игроков"), (14, "игроков"), (21, "игрок"), (22, "игрока"),
        (25, "игроков"), (101, "игрок"), (111, "игроков"), (114, "игроков")
    };
    foreach (var (count, expected) in cases)
    {
        var actual = GameServerStatusService.PlayersCaption(count);
        Expect($"склонение {count}", actual == expected, $"{actual} (ждали {expected})");
    }

    // Сервера берутся из манифеста сборки, иначе — запасной список.
    var empty = new ModpackManifest();
    Expect("нет серверов в манифесте → запасной список",
        GameServerStatusService.GetServers(empty).Count == GameServerStatusService.DefaultServers.Count, "2 сервера");

    var withServers = new ModpackManifest();
    withServers.Servers.Add(new ManifestServerEntry { Name = "Тест", Host = " test.example.com " });
    withServers.Servers.Add(new ManifestServerEntry { Name = "", Host = "noname.example.com" });
    withServers.Servers.Add(new ManifestServerEntry { Name = "Пустой", Host = "   " });
    var list = GameServerStatusService.GetServers(withServers);
    Expect("серверы из манифеста", list.Count == 2, $"{list.Count} шт.");
    Expect("адрес обрезается от пробелов", list[0].Host == "test.example.com", list[0].Host);
    Expect("без имени показываем адрес", list[1].DisplayName == "noname.example.com", list[1].DisplayName);

    // Состояние по результату пинга
    var def = new GameServerDefinition("Сервер", "host.example.com");
    var offline = GameServerStatusService.BuildStatus(def, null);
    Expect("нет ответа → недоступен", !offline.IsOnline && offline.StatusText == "Недоступен" && offline.PlayersText == "—", offline.StatusText);

    var online = GameServerStatusService.BuildStatus(def, new MinecraftPingResult(23, 30, "1.20.1"));
    Expect("ответил → онлайн", online.IsOnline && online.StatusText == "Онлайн", online.StatusText);
    Expect("в подписи адрес и счёт", online.Detail == "host.example.com · 23/30", online.Detail);
    Expect("склонение в карточке", online.PlayersCaption == "игрока", online.PlayersCaption);

    var summary = GameServerStatusService.BuildSummary([online, offline]);
    Expect("сводка считает только онлайн", summary.Contains("23") && summary.Contains("1/2"), summary);
    Expect("все оффлайн → отдельный текст",
        GameServerStatusService.BuildSummary([offline]) == "Серверы сейчас оффлайн",
        GameServerStatusService.BuildSummary([offline]));

    Console.WriteLine($"SERVERS_RESULT={(bad == 0 ? "PASS" : "FAIL")} ok={ok} fail={bad}");
    Environment.ExitCode = bad == 0 ? 0 : 1;
    return;
}

// Тест установки/обновления архива сборки: SmokeTest --archive <manifestUrl>
// Папка через SMOKETEST_INSTALL_ROOT. Гоняет ArchiveInstallService.InstallAsync.
if (args.Length >= 2 && args[0].Equals("--archive", StringComparison.OrdinalIgnoreCase))
{
    await RunArchiveTestAsync(args[1]);
    return;
}

await RunLegacySyncTestAsync(args);
return;

static async Task RunRuntimeTestAsync(string manifestUrl, bool installOnly)
{
    // Папку установки можно переопределить через SMOKETEST_INSTALL_ROOT (например, чтобы прогнать
    // запуск на уже установленной сборке tfgm в %AppData%\ForgeLauncher).
    var overrideRoot = Environment.GetEnvironmentVariable("SMOKETEST_INSTALL_ROOT");
    var installRoot = Environment.ExpandEnvironmentVariables(
        string.IsNullOrWhiteSpace(overrideRoot) ? @"%AppData%\BL-modern\stoneblock4-test" : overrideRoot);
    var configuration = new LauncherConfiguration
    {
        LauncherName = "NeoForge Runtime Test",
        ModpackManifestUrl = manifestUrl,
        DistributionRoot = installRoot
    };

    using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    Console.WriteLine($"Manifest: {manifestUrl}");
    var manifest = await new ModpackManifestClient(httpClient).GetManifestAsync(manifestUrl);
    Console.WriteLine($"Loader={manifest.Modpack.Loader} MC={manifest.Modpack.MinecraftVersion} " +
                      $"loaderVer={manifest.Modpack.LoaderVersion} mainVersionId={manifest.Runtime.MainVersionId} java={manifest.Runtime.JavaVersion}");

    // InstallRoot фиксируем явно, чтобы install (manifest.Install.Root) и launch (DistributionRoot) попали в одну папку.
    var settings = new UserSettings { Username = "Tester", MemoryMb = manifest.Runtime.MemoryMbDefault, InstallRoot = installRoot };

    var lastPct = -1;
    var progress = new Progress<FileSyncProgress>(p =>
    {
        var pct = (int)p.Percentage;
        if (pct != lastPct || !string.IsNullOrWhiteSpace(p.LogLine))
        {
            lastPct = pct;
            Console.WriteLine($"  [{pct,3}%] {p.Message}{(string.IsNullOrWhiteSpace(p.LogLine) ? "" : "  | " + p.LogLine)}");
        }
    });

    Console.WriteLine("=== EnsureRuntimeAsync (Java + vanilla + loader + assets) ===");
    await new RuntimeInstallService(httpClient).EnsureRuntimeAsync(configuration, manifest, settings, progress, CancellationToken.None);

    var versionJson = Path.Combine(installRoot, "versions", manifest.Runtime.MainVersionId, $"{manifest.Runtime.MainVersionId}.json");
    Console.WriteLine($"RUNTIME_OK loaderVersionJson exists={File.Exists(versionJson)} -> {versionJson}");
    var managedJava = Path.Combine(installRoot, ".launcher", "runtime", $"java-{manifest.Runtime.JavaVersion}", "bin", "java.exe");
    Console.WriteLine($"MANAGED_JAVA exists={File.Exists(managedJava)} -> {managedJava}");

    if (installOnly)
    {
        Console.WriteLine("INSTALL_ONLY done.");
        return;
    }

    var launchManifest = new LauncherManifest
    {
        Launcher = new LauncherUpdateInfo { Version = "test" },
        Game = new GameDistributionInfo
        {
            Version = manifest.Modpack.Version,
            MainVersionId = manifest.Runtime.MainVersionId,
            JavaExecutable = string.IsNullOrWhiteSpace(manifest.Runtime.JavaExecutable) ? "javaw.exe" : manifest.Runtime.JavaExecutable,
            JavaMajorVersion = manifest.Runtime.JavaVersion > 0 ? manifest.Runtime.JavaVersion : 17,
            JavaArguments = manifest.Runtime.JvmArgs,
            GameArguments = manifest.Runtime.GameArgs
        }
    };

    Console.WriteLine("=== LaunchForDiagnosticsAsync (90s, headless) ===");
    var result = await new MinecraftLaunchService().LaunchForDiagnosticsAsync(configuration, launchManifest, settings, TimeSpan.FromSeconds(90));
    Console.WriteLine($"DIAGNOSTIC file={result.FileName}");
    Console.WriteLine($"exited={result.Exited} exitCode={result.ExitCode}");
    Console.WriteLine($"stdout={result.StdoutPath}");
    Console.WriteLine($"stderr={result.StderrPath}");
}

static void RunResolveSelfTest()
{
    var temp = Path.Combine(Path.GetTempPath(), "blm-resolve-" + Guid.NewGuid().ToString("N"));
    var baseDir = Path.Combine(temp, "base");
    Directory.CreateDirectory(Path.Combine(baseDir, ".launcher"));
    File.WriteAllText(Path.Combine(baseDir, ".launcher", "modpack.version"), "tfgm:0.12.8-flat");

    var pass = 0; var fail = 0;
    void Check(string name, string actual, string expected)
    {
        var ok = string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}\n         got={actual}\n         exp={expected}");
        if (ok) pass++; else fail++;
    }
    void CheckTrue(string name, bool cond, string detail)
    {
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}  ({detail})");
        if (cond) pass++; else fail++;
    }

    // Пути в проверках должны быть АБСОЛЮТНЫМИ для текущей ОС: "C:\mr" на Linux и macOS —
    // это относительное имя папки, и ожидание сравнивалось бы с рабочим каталогом раннера.
    var mr = HostPlatform.IsWindows ? @"C:\mr" : "/mnt/mr";
    var fb = HostPlatform.IsWindows ? @"C:\fb" : "/mnt/fb";
    var ignored = HostPlatform.IsWindows ? @"C:\ignored" : "/mnt/ignored";
    var perPackRoot = HostPlatform.IsWindows ? @"D:\custom\sb" : "/mnt/custom/sb";

    // 1. Дефолтный игрок (InstallRoot пуст), мульти-режим → manifestRoot (ничего не меняется).
    var s1 = new UserSettings { InstallRoot = string.Empty };
    Check("default user → manifestRoot", s1.ResolveInstallRoot(mr, fb, "tfgm"), mr);

    // 2. Кастомный путь, мульти, сборка УЖЕ установлена в базе (маркер tfgm) → не переезжает.
    var s2 = new UserSettings { InstallRoot = baseDir };
    Check("custom+installed → base (no relocate)", s2.ResolveInstallRoot(ignored, fb, "tfgm"), baseDir);

    // 3. Кастомный путь, мульти, НОВАЯ сборка (нет в базе) → подпапка <id>.
    Check("custom+new → base\\id", s2.ResolveInstallRoot(ignored, fb, "stoneblock4"), Path.Combine(baseDir, "stoneblock4"));

    // 4. Одиночный режим (catalogModpackId=null) → база как есть, без подпапки.
    Check("single mode → base as-is", s2.ResolveInstallRoot(ignored, fb, null), baseDir);

    // 5. Явный per-pack override побеждает.
    var s5 = new UserSettings { InstallRoot = baseDir };
    s5.ModpackInstallRoots["stoneblock4"] = perPackRoot;
    Check("per-pack override wins", s5.ResolveInstallRoot(ignored, fb, "stoneblock4"), perPackRoot);

    // 6. ignoreModpackOverride игнорирует override → дефолт (подпапка).
    Check("ignoreOverride → default subfolder", s5.ResolveInstallRoot(ignored, fb, "stoneblock4", ignoreModpackOverride: true), Path.Combine(baseDir, "stoneblock4"));

    // 7. Опасный id с разделителями/.. не выходит за пределы базы.
    var r7 = s2.ResolveInstallRoot(ignored, fb, "..\\..\\evil/x");
    CheckTrue("malicious id stays under base", r7.StartsWith(Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase), r7);

    // 8. Пустой/битый маркер в базе → считаем «не установлено» → подпапка.
    var baseDir2 = Path.Combine(temp, "base2");
    Directory.CreateDirectory(Path.Combine(baseDir2, ".launcher"));
    File.WriteAllText(Path.Combine(baseDir2, ".launcher", "modpack.version"), "");
    var s8 = new UserSettings { InstallRoot = baseDir2 };
    Check("empty marker → subfolder", s8.ResolveInstallRoot(ignored, fb, "tfgm"), Path.Combine(baseDir2, "tfgm"));

    // 9. Раскрытие путей из конфигурации. На Windows обязано совпадать с прежним поведением
    //    (ExpandEnvironmentVariables), иначе у игроков уедет папка установки.
    //    На Linux/macOS %AppData% должен превратиться в реальный каталог, а не остаться текстом.
    const string configuredRoot = @"%AppData%\ForgeLauncher";
    var expanded = LauncherPaths.Expand(configuredRoot);
    if (OperatingSystem.IsWindows())
    {
        Check("expand: как раньше на Windows", expanded, Environment.ExpandEnvironmentVariables(configuredRoot));
    }
    else
    {
        CheckTrue("expand: %AppData% раскрыт", !expanded.Contains('%'), expanded);
        CheckTrue("expand: нет обратных слэшей", !expanded.Contains('\\'), expanded);
        CheckTrue("expand: путь абсолютный", Path.IsPathRooted(expanded), expanded);
    }

    CheckTrue("expand: пустая строка не падает", LauncherPaths.Expand(null) == string.Empty, "null → \"\"");

    // 10. Найдено ПЕРВЫМ ЗАПУСКОМ на настоящей Ubuntu: GetFolderPath(ApplicationData) вернул там
    //     пустую строку, и «%AppData%\ForgeLauncher» превращался в папку с буквальным именем
    //     «%AppData%» рядом с бинарником — данные лаунчера уезжали не туда.
    var appDataRoot = LauncherPaths.GetApplicationDataRoot();
    CheckTrue("папка данных не пустая", appDataRoot.Length > 0, appDataRoot);
    CheckTrue("папка данных абсолютная", Path.IsPathRooted(appDataRoot), appDataRoot);
    CheckTrue("домашний каталог не пустой", LauncherPaths.GetHomeRoot().Length > 0, LauncherPaths.GetHomeRoot());
    CheckTrue("в раскрытом пути не осталось токенов", !expanded.Contains('%'), expanded);

    try { Directory.Delete(temp, true); } catch { }
    Console.WriteLine($"\nSELFTEST_RESULT pass={pass} fail={fail}");
}

static async Task RunArchiveTestAsync(string manifestUrl)
{
    var overrideRoot = Environment.GetEnvironmentVariable("SMOKETEST_INSTALL_ROOT");
    var installRoot = Environment.ExpandEnvironmentVariables(
        string.IsNullOrWhiteSpace(overrideRoot) ? @"%TEMP%\sb-archive-test" : overrideRoot);

    var configuration = new LauncherConfiguration { LauncherName = "Archive Test", DistributionRoot = installRoot };
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    var manifest = await new ModpackManifestClient(httpClient).GetManifestAsync(manifestUrl);
    Console.WriteLine($"Pack {manifest.Modpack.Id} v{manifest.Modpack.Version} -> {installRoot}");

    var settings = new UserSettings { Username = "Tester", MemoryMb = 4096, InstallRoot = installRoot };
    var lastPct = -1;
    var progress = new Progress<FileSyncProgress>(p =>
    {
        var pct = (int)p.Percentage;
        if (pct != lastPct || !string.IsNullOrWhiteSpace(p.LogLine))
        {
            lastPct = pct;
            Console.WriteLine($"  [{pct,3}%] {p.Message}{(string.IsNullOrWhiteSpace(p.LogLine) ? "" : "  | " + p.LogLine)}");
        }
    });

    var summary = await new ArchiveInstallService(httpClient).InstallAsync(configuration, manifest, settings, progress, CancellationToken.None);
    Console.WriteLine($"ARCHIVE_DONE installed={summary.Installed} root={summary.InstallPath}");
}

static async Task RunLegacySyncTestAsync(string[] args)
{
    var distributionRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForgeLauncher");
    var configuration = new LauncherConfiguration
    {
        LauncherName = "Smoke Test",
        ManifestUrl = "http://127.0.0.1:8080/launcher-manifest.json",
        DistributionRoot = distributionRoot,
        LauncherExecutable = "Launcher.App.exe",
        LauncherVersionFile = "launcher.version"
    };

    using var httpClient = new HttpClient();
    var manifestClient = new ManifestClient(httpClient);
    var manifest = await manifestClient.GetManifestAsync(configuration.ManifestUrl);

    var syncService = new FileSyncService(httpClient);
    var syncSummary = await syncService.SyncAsync(configuration, manifest, null, CancellationToken.None);
    Console.WriteLine($"SYNC_OK downloaded={syncSummary.DownloadedFiles} skipped={syncSummary.SkippedFiles}");

    var userSettings = new UserSettings { Username = "Player", MemoryMb = 4096 };
    var launchService = new MinecraftLaunchService();
    if (args.Contains("--diagnostic", StringComparer.OrdinalIgnoreCase))
    {
        var result = await launchService.LaunchForDiagnosticsAsync(configuration, manifest, userSettings, TimeSpan.FromSeconds(90));
        Console.WriteLine($"DIAGNOSTIC_LAUNCH file={result.FileName}");
        Console.WriteLine($"exited={result.Exited} exitCode={result.ExitCode}");
        Console.WriteLine($"stdout={result.StdoutPath}");
        Console.WriteLine($"stderr={result.StderrPath}");
        Console.WriteLine(result.ArgumentsPreview);
    }
    else
    {
        var result = await launchService.LaunchAsync(configuration, manifest, userSettings);
        Console.WriteLine($"LAUNCH_OK file={result.FileName}");
        Console.WriteLine(result.ArgumentsPreview);
    }
}
