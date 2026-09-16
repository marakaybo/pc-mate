<#
.SYNOPSIS
    Сборка агента PC MATE: служба + помощник в трее, при наличии Inno Setup — установщик.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -SelfContained          # без установки .NET на целевом ПК (~150 МБ)
    ./build.ps1 -Version 1.1.0          # версия попадёт в имя файла и свойства exe
    ./build.ps1 -Configuration Debug -SkipInstaller
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SelfContained,

    [switch]$SkipInstaller,

    [string]$Runtime = 'win-x64',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'

function Find-Dotnet {
    $candidates = @(
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks) { return $candidate }
    }

    throw 'Не найден .NET SDK 8. Установите его: https://dotnet.microsoft.com/download/dotnet/8.0'
}

$dotnet = Find-Dotnet
Write-Host "dotnet: $dotnet" -ForegroundColor DarkGray

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$commonArgs = @(
    '--configuration', $Configuration
    '--runtime', $Runtime
    '--output', $publishDir
    '--nologo'
    "-p:Version=$Version"
    "-p:FileVersion=$Version.0"
    "-p:AssemblyVersion=$Version.0"
)
if ($SelfContained) {
    $commonArgs += @('--self-contained', 'true', '-p:PublishSingleFile=false')
} else {
    $commonArgs += @('--self-contained', 'false')
}

Write-Host "`n[1/3] Сборка службы…" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'src\PcMate.Agent.Service\PcMate.Agent.Service.csproj') @commonArgs
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать службу.' }

Write-Host "`n[2/3] Сборка помощника в трее…" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'src\PcMate.Agent.Tray\PcMate.Agent.Tray.csproj') @commonArgs
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать помощник.' }

Copy-Item (Join-Path $root 'src\PcMate.Agent.Tray\pcmate.ico') $publishDir -Force -ErrorAction SilentlyContinue

Write-Host "`n[3/3] Установщик…" -ForegroundColor Cyan
$setupPath = $null

if ($SkipInstaller) {
    Write-Host 'Пропущено (-SkipInstaller).' -ForegroundColor DarkGray
} else {
    $iscc = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\InnoSetup6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if ($iscc) {
        $selfContainedFlag = if ($SelfContained) { '1' } else { '0' }
        & $iscc "/DSourceDir=$publishDir" "/DAppVersion=$Version" "/DSelfContained=$selfContainedFlag" `
            (Join-Path $root 'installer\pcmate.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup вернул ошибку.' }

        $setupPath = Join-Path $distDir "PcMate-Setup-$Version.exe"
    } else {
        Write-Warning 'Inno Setup 6 не найден — установщик не собран.'
        Write-Host 'Скачать: https://jrsoftware.org/isdl.php' -ForegroundColor DarkGray
        Write-Host 'Или портативно, без прав администратора:' -ForegroundColor DarkGray
        Write-Host '  innosetup-6.7.3.exe /VERYSILENT /PORTABLE=1 /DIR="$env:LOCALAPPDATA\Programs\InnoSetup6"' -ForegroundColor DarkGray
        Write-Host 'Для локальной установки без установщика используйте: ./install.ps1' -ForegroundColor DarkGray
    }
}

if ($setupPath -and (Test-Path $setupPath)) {
    # Контрольная сумма нужна тем, кто скачивает установщик с GitHub:
    # это единственный способ убедиться, что файл не подменили по дороге.
    $hash = (Get-FileHash $setupPath -Algorithm SHA256).Hash.ToLower()
    $checksumFile = "$setupPath.sha256"
    "$hash  $(Split-Path -Leaf $setupPath)" | Out-File $checksumFile -Encoding ascii -NoNewline

    Write-Host "`nУстановщик готов:" -ForegroundColor Green
    Write-Host "  $setupPath" -ForegroundColor Gray
    Write-Host "  $([math]::Round((Get-Item $setupPath).Length / 1MB, 2)) МБ" -ForegroundColor DarkGray
    Write-Host "  SHA-256: $hash" -ForegroundColor DarkGray
} else {
    Write-Host "`nГотово. Файлы: $publishDir" -ForegroundColor Green
    Get-ChildItem $publishDir -Filter '*.exe' | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor DarkGray }
}
