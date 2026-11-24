<#
.SYNOPSIS
  Publica TraductorVozTeams.exe (portable, x64, autocontenido) y opcionalmente lo instala para el usuario actual.

.DESCRIPTION
  - Salida portable: publish\TraductorVozTeams\TraductorVozTeams.exe
  - ZIP:             dist\TraductorVozTeams-<version>-win-x64.zip
  - -Install:        copia a %LOCALAPPDATA%\Programs\TraductorVozTeams y crea accesos directos.
  La configuracion y las credenciales NUNCA se incluyen: viven en %LOCALAPPDATA%\TraductorVozTeams.
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\TraductorVozTeams.App\TraductorVozTeams.App.csproj'
$publishDir = Join-Path $root 'publish\TraductorVozTeams'
$distDir = Join-Path $root 'dist'

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'TraductorVozTeams.sln') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Las pruebas fallaron.' }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'La publicacion fallo.' }

$exe = Join-Path $publishDir 'TraductorVozTeams.exe'
$forbidden = Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Name -in @('settings.json', '.env') -or $_.Extension -eq '.bin' }
if ($forbidden) { throw "La carpeta publicada contiene archivos de configuracion o secretos: $($forbidden.FullName -join ', ')" }

$report = Join-Path ([IO.Path]::GetTempPath()) 'TraductorVozTeams-selftest.txt'
$process = Start-Process $exe -ArgumentList '--selftest', "`"$report`"" -Wait -PassThru
Get-Content $report -Encoding UTF8 | Write-Host
if ($process.ExitCode -ne 0) { throw 'El autodiagnostico del ejecutable publicado fallo.' }

$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
New-Item -ItemType Directory -Force $distDir | Out-Null
$zip = Join-Path $distDir "TraductorVozTeams-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# El antivirus puede retener el .exe recien ejecutado unos segundos.
for ($attempt = 1; $attempt -le 10; $attempt++) {
    try { Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -ErrorAction Stop; break }
    catch { if ($attempt -eq 10) { throw }; Start-Sleep -Seconds 3 }
}
Write-Host "Ejecutable portable: $exe"
Write-Host "ZIP: $zip"

if ($Install) {
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\TraductorVozTeams'
    New-Item -ItemType Directory -Force $installDir | Out-Null
    Copy-Item (Join-Path $publishDir '*') $installDir -Recurse -Force
    $installedExe = Join-Path $installDir 'TraductorVozTeams.exe'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs'))) {
        $shortcut = $shell.CreateShortcut((Join-Path $folder 'Traductor de voz para reuniones en Windows más Azure.lnk'))
        $shortcut.TargetPath = $installedExe
        $shortcut.WorkingDirectory = $installDir
        $shortcut.IconLocation = "$installedExe,0"
        $shortcut.Description = 'Traductor de voz para reuniones en Windows más Azure'
        $shortcut.Save()
    }
    Write-Host "Instalado en: $installedExe (accesos directos en Escritorio y menu Inicio)"
}
