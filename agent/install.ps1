<#
.SYNOPSIS
    Установка агента PC MATE без Inno Setup: копирует файлы, заводит службу,
    правило брандмауэра и автозапуск помощника. Запускать от администратора.

.EXAMPLE
    ./install.ps1                 # собрать и установить
    ./install.ps1 -SkipBuild      # установить уже собранное из ./publish
    ./install.ps1 -Uninstall      # удалить
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Uninstall,
    [string]$InstallDir = "$env:ProgramFiles\PC MATE",
    [int]$LanPort = 8760
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $root 'publish'
$serviceName = 'PcMateAgent'
$trayExe = 'PcMate.Tray.exe'
$serviceExe = 'PcMate.Agent.Service.exe'
$fwTcp = 'PC MATE (локальная сеть)'
$fwUdp = 'PC MATE (Wake-on-LAN)'

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Нужны права администратора. Откройте PowerShell от имени администратора.'
    }
}

function Stop-Everything {
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($trayExe)) -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        Write-Host 'Останавливаем службу…' -ForegroundColor DarkGray
        try { Stop-Service -Name $serviceName -Force -ErrorAction Stop } catch {}
        Start-Sleep -Seconds 1
    }
}

Assert-Admin

if ($Uninstall) {
    Write-Host 'Удаление PC MATE…' -ForegroundColor Cyan
    Stop-Everything

    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        & sc.exe delete $serviceName | Out-Null
    }

    & netsh.exe advfirewall firewall delete rule name="$fwTcp" 2>$null | Out-Null
    & netsh.exe advfirewall firewall delete rule name="$fwUdp" 2>$null | Out-Null
    & schtasks.exe /Delete /TN '\PC MATE' /F 2>$null | Out-Null

    Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' `
        -Name 'PC MATE' -ErrorAction SilentlyContinue

    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host 'PC MATE удалён. Данные в C:\ProgramData\PcMate сохранены.' -ForegroundColor Green
    return
}

if (-not $SkipBuild) {
    Write-Host 'Сборка…' -ForegroundColor Cyan
    & (Join-Path $root 'build.ps1') -SkipInstaller
}

if (-not (Test-Path (Join-Path $publishDir $serviceExe))) {
    throw "Не найдено $publishDir\$serviceExe. Соберите проект: ./build.ps1"
}

Stop-Everything

Write-Host "Копируем файлы в $InstallDir…" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item (Join-Path $publishDir '*') $InstallDir -Recurse -Force

Write-Host 'Регистрируем службу…' -ForegroundColor Cyan
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

& sc.exe create $serviceName binPath= "`"$InstallDir\$serviceExe`"" start= auto DisplayName= 'PC MATE Agent' | Out-Null
& sc.exe description $serviceName 'Удалённое управление компьютером с телефона: питание, сценарии, расписания.' | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Write-Host 'Настраиваем брандмауэр…' -ForegroundColor Cyan
& netsh.exe advfirewall firewall delete rule name="$fwTcp" 2>$null | Out-Null
& netsh.exe advfirewall firewall add rule name="$fwTcp" dir=in action=allow protocol=TCP localport=$LanPort profile=private,domain | Out-Null
& netsh.exe advfirewall firewall delete rule name="$fwUdp" 2>$null | Out-Null
& netsh.exe advfirewall firewall add rule name="$fwUdp" dir=in action=allow protocol=UDP localport=9 profile=private,domain | Out-Null

Write-Host 'Готовим питание (гибернация + таймеры пробуждения)…' -ForegroundColor Cyan
& powercfg.exe /hibernate on 2>$null | Out-Null
& powercfg.exe /setacvalueindex scheme_current 238c9fa8-0aad-41ed-83f4-97be242c8f20 bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d 1 | Out-Null
& powercfg.exe /setactive scheme_current | Out-Null

Write-Host 'Добавляем автозапуск помощника…' -ForegroundColor Cyan
Set-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'PC MATE' -Value "`"$InstallDir\$trayExe`""

Write-Host 'Запускаем службу…' -ForegroundColor Cyan
Start-Service -Name $serviceName
Start-Sleep -Seconds 2

$service = Get-Service -Name $serviceName
Write-Host "Служба: $($service.Status)" -ForegroundColor Green

Start-Process -FilePath (Join-Path $InstallDir $trayExe) -ArgumentList '--first-run'

Write-Host @"

PC MATE установлен.

  Служба:    $serviceName ($InstallDir\$serviceExe)
  Помощник:  $InstallDir\$trayExe (значок в трее)
  Данные:    C:\ProgramData\PcMate
  Журналы:   C:\ProgramData\PcMate\logs

Сейчас откроется окно с QR-кодом — отсканируйте его приложением на телефоне.
"@ -ForegroundColor Green
