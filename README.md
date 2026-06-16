# Forge Launcher

Windows-лаунчер для Minecraft 1.20.1 с Forge и модами, где сам лаунчер и клиентская сборка загружаются с сервера.

## Состав

- `Launcher.Bootstrapper` проверяет обновления самого лаунчера
- `Launcher.App` синхронизирует файлы клиента, модов и конфигов
- `launcher.config.json` хранит URL серверного манифеста
- `server/launcher-manifest.example.json` показывает формат манифеста
- `tools/New-DistributionManifest.ps1` собирает список файлов с хэшами SHA-256

## Базовая схема

1. Загружаете на сервер zip с опубликованным `Launcher.App`
2. Загружаете `launcher-manifest.json`
3. Загружаете папку `game/`, где лежат `versions`, `libraries`, `assets`, `mods`, `config` и любые другие нужные папки
4. Пользователь запускает `Launcher.Bootstrapper.exe`
5. Bootstrapper обновляет `Launcher.App`, а сам лаунчер догружает измененные файлы клиента и запускает Forge

## Настройка

Отредактируйте `launcher.config.json` и поставьте реальный URL:

```json
{
  "launcherName": "Forge Launcher",
  "manifestUrl": "https://your-domain.com/minecraft/launcher-manifest.json",
  "distributionRoot": "%AppData%\\ForgeLauncher",
  "launcherExecutable": "Launcher.App.exe",
  "launcherVersionFile": "launcher.version"
}
```

## Сборка

```powershell
dotnet build .\MinecraftLauncher.slnx
dotnet publish .\Launcher.App\Launcher.App.csproj -c Release -r win-x64 --self-contained false
dotnet publish .\Launcher.Bootstrapper\Launcher.Bootstrapper.csproj -c Release -r win-x64 --self-contained false
```

## Загрузка

Готовые сборки — на странице [Releases](https://github.com/bytefrf/BL-modernlauncher/releases).

## Подпись кода (Code signing)

Сборки для Windows подписываются сертификатом, предоставленным
[SignPath Foundation](https://signpath.org/) (бесплатная подпись кода для open-source проектов).

## Конфиденциальность

Лаунчер собирает анонимную техническую телеметрию (её можно отключить в настройках).
Подробности — в [PRIVACY.md](PRIVACY.md).

## Дисклеймер / Disclaimer

TerraFirmaGreg-Modern — сторонний комьюнити-модпак, созданный не нами и не связанный с этим
проектом. Этот репозиторий содержит **только исходный код лаунчера**; модпак, моды и файлы игры
Minecraft в него не входят и скачиваются во время работы.

TerraFirmaGreg-Modern is a third-party community modpack, not authored by or affiliated with this
project. This repository contains **only the launcher source code**; it does not include the
modpack, mods, or Minecraft game files, which are downloaded at runtime.

## Лицензия

[MIT](LICENSE)

