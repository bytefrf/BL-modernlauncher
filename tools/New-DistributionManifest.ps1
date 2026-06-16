param(
    [Parameter(Mandatory = $true)]
    [string]$ContentRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [string]$MainVersionId = "forge-1.20.1-47.2.0",
    [string]$GameVersion = "1.0.0",
    [string]$LauncherVersion = "1.0.0",
    [string]$LauncherPackageUrl = "launcher/launcher-win-x64.zip",
    [string]$LauncherSha256 = "PUT_SHA256_OF_LAUNCHER_ZIP_HERE"
)

$contentRoot = (Resolve-Path $ContentRoot).Path
$baseUri = $BaseUrl.TrimEnd('/')
$files = Get-ChildItem -Path $contentRoot -File -Recurse | ForEach-Object {
    $relative = [System.IO.Path]::GetRelativePath($contentRoot, $_.FullName).Replace('\', '/')
    $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    [pscustomobject]@{
        path   = $relative
        url    = "game/$relative"
        sha256 = $hash
        size   = $_.Length
    }
}

$manifest = [ordered]@{
    launcher = [ordered]@{
        version    = $LauncherVersion
        packageUrl = $LauncherPackageUrl
        sha256     = $LauncherSha256
    }
    game = [ordered]@{
        version        = $GameVersion
        description    = "Сборка Minecraft 1.20.1 с Forge и модами"
        mainVersionId  = $MainVersionId
        javaExecutable = "javaw.exe"
        deleteOrphans  = $false
        preservePaths  = @("saves", "screenshots", "logs", "resourcepacks", "shaderpacks")
        javaArguments  = @("-XX:+UseG1GC")
        gameArguments  = @()
        files          = $files
    }
}

$manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputPath -Encoding UTF8
Write-Host "Manifest created: $OutputPath"
Write-Host "Upload game files to: $baseUri/game/"
