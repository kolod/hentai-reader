#Requires -Version 7.2
<#
.SYNOPSIS
    Unattended install / upgrade / start / running-app / repair / downgrade / uninstall check of the
    installer (specs/004-windows-installer, research R10).

.DESCRIPTION
    Installs for the current user into a temporary folder, so it needs no administrator rights. Refuses
    to run when Hentai Reader is already installed on this machine. The real data folder
    (%LOCALAPPDATA%\HentaiReader) gets a sentinel file that must survive upgrade and uninstall; the
    sentinel is removed afterwards. The app itself is started with HENTAIREADER_DATA pointing to a
    temporary folder and never passes the adult confirmation, so it sends no request and must store
    nothing. Writes a JSON report.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Installer,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OlderInstaller,
    [string] $Report
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E0B7C2A-4F1D-4C8B-9A35-2D7E91B4F6C3}_is1'
$DataFolder = Join-Path $env:LOCALAPPDATA 'HentaiReader'
$Sentinel = Join-Path $DataFolder '.installer-smoke-sentinel'
$Temp = Join-Path ([IO.Path]::GetTempPath()) "hentaireader-smoke-$PID"
$InstallDir = Join-Path $Temp 'program'
$AppData = Join-Path $Temp 'data'
$LogDir = $Report ? (Join-Path (Split-Path $Report) 'smoke-logs') : (Join-Path $Temp 'logs')
$Steps = [System.Collections.Generic.List[object]]::new()
$Silent = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')

function Get-Installation {
    foreach ($hive in 'HKCU:', 'HKLM:') {
        $key = Join-Path $hive $UninstallKey
        if (Test-Path $key) {
            $entry = Get-ItemProperty $key
            return [pscustomobject]@{ Hive = $hive; Version = $entry.DisplayVersion; Location = $entry.InstallLocation.TrimEnd('\') }
        }
    }
    return $null
}

function Invoke-Setup([string] $File, [string[]] $Arguments, [string] $Log) {
    $process = Start-Process $File -ArgumentList ($Arguments + "/LOG=`"$(Join-Path $LogDir $Log)`"") -PassThru
    if (-not $process.WaitForExit(300000)) {
        $process.Kill()
        throw "$([IO.Path]::GetFileName($File)) did not finish within 5 minutes"
    }
    return $process.ExitCode
}

function Test-Step([string] $Name, [scriptblock] $Check) {
    try {
        $detail = & $Check
        $Steps.Add([ordered]@{ name = $Name; passed = $true; details = "$detail" })
        Write-Host "  [ok]   $Name $detail"
    }
    catch {
        $Steps.Add([ordered]@{ name = $Name; passed = $false; details = $_.Exception.Message })
        Write-Host "  [FAIL] $Name $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

function Assert-Installed([string] $Expected) {
    $installation = Get-Installation
    if (-not $installation) { throw 'no uninstall entry' }
    if ($installation.Version -ne $Expected) { throw "uninstall entry version $($installation.Version), expected $Expected" }
    if ($installation.Hive -ne 'HKCU:') { throw "installed in $($installation.Hive), expected the current user scope" }
    if ($installation.Location -ne $InstallDir) { throw "installed in $($installation.Location), expected $InstallDir" }
}

if (Get-Installation) {
    throw 'Hentai Reader is installed on this machine. Uninstall it before running the smoke test.'
}

$createdDataFolder = -not (Test-Path $DataFolder)
New-Item -ItemType Directory -Force $DataFolder, $LogDir, $AppData | Out-Null
Set-Content $Sentinel 'installer smoke test'
$exe = Join-Path $InstallDir 'HentaiReader.exe'
$failed = $false

try {
    Test-Step 'install older version (silent, current user)' {
        $code = Invoke-Setup $OlderInstaller ($Silent + @('/CURRENTUSER', "/DIR=`"$InstallDir`"")) 'install-older.log'
        if ($code -ne 0) { throw "exit code $code" }
        Assert-Installed '0.0.1'
        'exit 0, version 0.0.1'
    }

    # A script left behind by an older version must not survive an upgrade (research R5).
    $stale = Join-Path $InstallDir 'Sources/removed/removed.js'
    New-Item -ItemType Directory -Force (Split-Path $stale) | Out-Null
    Set-Content $stale 'export const manifest = {};'

    Test-Step 'silent upgrade in place' {
        $code = Invoke-Setup $Installer $Silent 'upgrade.log'
        if ($code -ne 0) { throw "exit code $code" }
        Assert-Installed $Version
        $product = ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]
        if ($product -ne $Version) { throw "HentaiReader.exe version $product, expected $Version" }
        if (Test-Path $stale) { throw 'stale source script survived the upgrade' }
        if (-not (Test-Path $Sentinel)) { throw 'the data folder sentinel was removed' }
        "exit 0, version $Version, same folder and scope, stale script removed"
    }

    Test-Step 'app starts and stores nothing before the adult confirmation' {
        $env:HENTAIREADER_DATA = $AppData
        try {
            $app = Start-Process $exe -PassThru
        }
        finally {
            Remove-Item Env:HENTAIREADER_DATA
        }
        Start-Sleep -Seconds 10
        if ($app.HasExited) { throw "the app exited after start with code $($app.ExitCode)" }
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(20000)) { $app.Kill(); $app.WaitForExit() }
        $stored = @(Get-ChildItem $AppData -Recurse -Force)
        if ($stored.Count -gt 0) { throw "the app wrote $($stored.Count) item(s) before confirmation: $($stored.Name -join ', ')" }
        'running after 10 s, nothing stored'
    }

    Test-Step 'refused while the app runs' {
        # The app holds this mutex; the installer must not replace files of a running app (FR-019).
        $mutex = [System.Threading.Mutex]::new($false, 'HentaiReader.Running')
        try {
            $code = Invoke-Setup $Installer $Silent 'running.log'
        }
        finally {
            $mutex.Dispose()
        }
        if ($code -eq 0) { throw 'the installer ran while the app mutex was held' }
        "exit $code"
    }

    Test-Step 'repair (same version)' {
        $code = Invoke-Setup $Installer $Silent 'repair.log'
        if ($code -ne 0) { throw "exit code $code" }
        Assert-Installed $Version
        'exit 0'
    }

    Test-Step 'downgrade refused' {
        $code = Invoke-Setup $OlderInstaller $Silent 'downgrade.log'
        if ($code -ne 1) { throw "exit code $code, expected 1" }
        Assert-Installed $Version
        'exit 1, version unchanged'
    }

    Test-Step 'uninstall keeps user data' {
        $code = Invoke-Setup (Join-Path $InstallDir 'unins000.exe') $Silent 'uninstall.log'
        if ($code -ne 0) { throw "exit code $code" }
        # The uninstaller finishes from a temporary copy; wait for the folder to disappear.
        for ($i = 0; $i -lt 60 -and (Test-Path $InstallDir); $i++) { Start-Sleep -Milliseconds 500 }
        if (Test-Path $InstallDir) { throw "program folder still exists: $InstallDir" }
        if (Get-Installation) { throw 'uninstall entry still exists' }
        $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Hentai Reader.lnk'
        if (Test-Path $shortcut) { throw "Start menu shortcut still exists: $shortcut" }
        $running = @(Get-Process HentaiReader -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir*" })
        if ($running.Count -gt 0) { throw 'a HentaiReader process from the program folder is still running' }
        if (-not (Test-Path $Sentinel)) { throw 'the data folder sentinel was removed' }
        'exit 0, program folder, shortcut and entry removed, data kept'
    }
}
catch {
    $failed = $true
}
finally {
    Get-Process HentaiReader -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir*" } | Stop-Process -Force
    if (Get-Installation) {
        $uninstaller = Join-Path $InstallDir 'unins000.exe'
        if (Test-Path $uninstaller) { [void](Invoke-Setup $uninstaller $Silent 'cleanup.log') }
    }
    Remove-Item $Sentinel -ErrorAction SilentlyContinue
    if ($createdDataFolder) { Remove-Item $DataFolder -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item $InstallDir, $AppData -Recurse -Force -ErrorAction SilentlyContinue

    if ($Report) {
        [ordered]@{ installer = [IO.Path]::GetFileName($Installer); version = $Version; passed = -not $failed; steps = $Steps; logs = $LogDir } |
            ConvertTo-Json -Depth 4 | Set-Content $Report -Encoding utf8
    }
}

exit ($failed ? 1 : 0)
