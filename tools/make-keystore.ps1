<#
.SYNOPSIS
    Создаёт ключ подписи Android-приложения PC MATE и готовит секреты для GitHub Actions.

.DESCRIPTION
    Приложение раздаётся через GitHub Releases, а не через магазин, поэтому
    подписывать APK нужно своим ключом. Ключ создаётся один раз и живёт вечно:
    Android разрешает обновлять приложение только тем же ключом, которым его
    подписали в первый раз.

    ПОТЕРЯ КЛЮЧА = невозможность выпускать обновления. Пользователям придётся
    удалить приложение и поставить заново. Сделайте резервную копию.

    Скрипт ничего не отправляет в сеть и не сохраняет пароли: всё остаётся у вас.

.EXAMPLE
    ./tools/make-keystore.ps1
    ./tools/make-keystore.ps1 -OutputDir "D:\secrets" -Alias pcmate
#>
[CmdletBinding()]
param(
    [string]$OutputDir = "$env:USERPROFILE\.pcmate",
    [string]$Alias = 'pcmate',
    [int]$ValidityDays = 10950,   # 30 лет: Google требует срок до 2033+, берём с запасом
    [string]$Name = 'PC MATE'
)

$ErrorActionPreference = 'Stop'

function Find-Keytool {
    $candidates = @(
        (Get-Command keytool -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    )

    foreach ($root in @($env:JAVA_HOME, "$env:ProgramFiles\Android\Android Studio\jbr", "$env:ProgramFiles\Eclipse Adoptium")) {
        if ($root -and (Test-Path $root)) {
            $candidates += Get-ChildItem -Path $root -Filter keytool.exe -Recurse -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty FullName -First 1
        }
    }

    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) {
        throw @'
Не найден keytool. Он входит в состав Java (JDK).

Проще всего:
  winget install EclipseAdoptium.Temurin.17.JDK
затем перезапустите PowerShell и повторите.

Либо укажите путь вручную через переменную JAVA_HOME.
'@
    }

    return $found
}

$keytool = Find-Keytool
Write-Host "keytool: $keytool" -ForegroundColor DarkGray

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$keystorePath = Join-Path $OutputDir 'pcmate-release.jks'

if (Test-Path $keystorePath) {
    Write-Warning "Ключ уже существует: $keystorePath"
    Write-Host 'Если создать новый, обновления для уже установленных приложений станут невозможны.' -ForegroundColor Yellow
    $answer = Read-Host 'Перезаписать? (напишите "да" для подтверждения)'
    if ($answer -ne 'да') {
        Write-Host 'Отменено.' -ForegroundColor DarkGray
        return
    }
    Remove-Item $keystorePath -Force
}

Write-Host ''
Write-Host 'Придумайте пароль для ключа (минимум 6 символов).' -ForegroundColor Cyan
Write-Host 'Сохраните его в менеджере паролей: восстановить его невозможно.' -ForegroundColor Yellow
$securePassword = Read-Host 'Пароль' -AsSecureString
$confirm = Read-Host 'Повторите пароль' -AsSecureString

$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword))
$plainConfirm = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirm))

if ($plain -ne $plainConfirm) { throw 'Пароли не совпали.' }
if ($plain.Length -lt 6) { throw 'Пароль короче 6 символов — Android такой не примет.' }

Write-Host ''
Write-Host 'Создаём ключ…' -ForegroundColor Cyan

$dname = "CN=$Name, OU=PC MATE, O=PC MATE, C=RU"
& $keytool -genkeypair -v `
    -keystore $keystorePath `
    -alias $Alias `
    -keyalg RSA `
    -keysize 4096 `
    -validity $ValidityDays `
    -storepass $plain `
    -keypass $plain `
    -dname $dname 2>&1 | Where-Object { $_ -notmatch '^\s*$' }

if ($LASTEXITCODE -ne 0) { throw 'keytool вернул ошибку.' }

# Отпечаток пригодится, чтобы убедиться, что APK подписан именно этим ключом.
$fingerprint = (& $keytool -list -v -keystore $keystorePath -alias $Alias -storepass $plain 2>&1 |
    Select-String 'SHA256:' | Select-Object -First 1).ToString().Trim()

$base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($keystorePath))
$base64Path = Join-Path $OutputDir 'pcmate-release.jks.base64.txt'
$base64 | Out-File $base64Path -Encoding ascii -NoNewline

Write-Host ''
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host ' Ключ создан' -ForegroundColor Green
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host ''
Write-Host "  Файл ключа:  $keystorePath"
Write-Host "  Алиас:       $Alias"
Write-Host "  $fingerprint"
Write-Host ''
Write-Host 'Что сделать дальше:' -ForegroundColor Cyan
Write-Host ''
Write-Host '  1. Положите копию файла ключа туда, где не потеряете:'
Write-Host '     менеджер паролей, зашифрованный архив, второй носитель.'
Write-Host '     Без него обновлять приложение будет НЕЛЬЗЯ.'
Write-Host ''
Write-Host '  2. Добавьте секреты в GitHub: Settings → Secrets and variables → Actions:'
Write-Host ''
Write-Host '     ANDROID_KEYSTORE_BASE64   содержимое файла' -ForegroundColor Gray
Write-Host "                               $base64Path" -ForegroundColor DarkGray
Write-Host '     ANDROID_KEYSTORE_PASSWORD ваш пароль' -ForegroundColor Gray
Write-Host '     ANDROID_KEY_ALIAS         ' -NoNewline -ForegroundColor Gray; Write-Host $Alias -ForegroundColor Gray
Write-Host '     ANDROID_KEY_PASSWORD      ваш пароль' -ForegroundColor Gray
Write-Host ''
Write-Host '  3. Удалите файл с base64 после добавления секрета:' -ForegroundColor Yellow
Write-Host "     Remove-Item '$base64Path'" -ForegroundColor DarkGray
Write-Host ''
