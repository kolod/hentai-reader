#Requires -Version 7.2
<#
.SYNOPSIS
    Verifies a publish folder (and optionally the installer manifest) against
    windows/installer/distribution-rules.json.

.DESCRIPTION
    Checks (specs/004-windows-installer, research R3):
      rules              no path is both required and forbidden
      allowedExtensions  every file has an allowed extension
      forbidden          no file matches a forbidden pattern
      required           every required file exists ({source} = every /sources/<id> with <id>.js)
      bundledScripts     every bundled source script is byte-identical to /sources, none extra
      priLanguages       HentaiReader.pri has a candidate of the probe resource in every language
      peMachine          native PE files are x64 (IL-only managed images are accepted)
      productVersion     HentaiReader.exe has the expected product version
      manifest           (with -Manifest) the installer manifest lists exactly these files, same SHA-256
    Runs every check, writes a JSON report (-Report) and exits 1 when any check fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string] $Version,
    [string] $Manifest,
    [string] $Report,
    [string] $Rules = (Join-Path (Split-Path $PSScriptRoot -Parent) 'installer/distribution-rules.json'),
    [string] $MakePri
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Path = (Resolve-Path $Path).Path.TrimEnd('\', '/')
$RuleSet = Get-Content $Rules -Raw | ConvertFrom-Json
$Files = @(Get-ChildItem $Path -Recurse -File | ForEach-Object {
        [pscustomobject]@{ Relative = $_.FullName.Substring($Path.Length + 1).Replace('\', '/'); Item = $_ }
    })
$Checks = [System.Collections.Generic.List[object]]::new()

function Add-Check([string] $Name, [string[]] $Problems) {
    $Checks.Add([ordered]@{ name = $Name; passed = ($Problems.Count -eq 0); details = @($Problems) })
}

# Glob relative to the publish root: '**/' any folders (including none), '*' within a name, '?' one character.
function ConvertTo-GlobRegex([string] $Glob) {
    $pattern = [regex]::Escape($Glob.Replace('\', '/'))
    $pattern = $pattern.Replace('\*\*/', '(?:.*/)?').Replace('/\*\*', '(?:/.*)?').Replace('\*', '[^/]*').Replace('\?', '[^/]')
    return [regex]::new("^$pattern$", 'IgnoreCase')
}

# COFF machine of a PE file and whether it is an IL-only managed image; $null when not a PE file.
function Get-PeInfo([string] $File) {
    $stream = [IO.File]::OpenRead($File)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 0x40 -or $reader.ReadUInt16() -ne 0x5A4D) { return $null }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { return $null }
        $machine = $reader.ReadUInt16()
        $stream.Position = $peOffset + 24
        $magic = $reader.ReadUInt16()
        # Data directory 14 (CLR runtime header): offset 208 in PE32+, 192 in PE32 from the optional header start.
        $stream.Position = $peOffset + 24 + ($magic -eq 0x20B ? 224 : 208)
        $clrRva = $reader.ReadUInt32()
        return [pscustomobject]@{ Machine = $machine; Managed = $clrRva -ne 0 }
    }
    finally {
        $stream.Dispose()
    }
}

# makepri.exe of the Microsoft.Windows.SDK.BuildTools version the app project pins.
function Find-MakePri {
    if ($MakePri) { return $MakePri }
    $project = Get-Content (Join-Path $RepoRoot 'windows/src/HentaiReader.App/HentaiReader.App.csproj') -Raw
    if ($project -notmatch 'Microsoft\.Windows\.SDK\.BuildTools" Version="([^"]+)"') { throw 'Microsoft.Windows.SDK.BuildTools version not found in the app project' }
    $packages = $env:NUGET_PACKAGES ?? (Join-Path $env:USERPROFILE '.nuget/packages')
    $found = Get-ChildItem (Join-Path $packages "microsoft.windows.sdk.buildtools/$($Matches[1])/bin") -Recurse -Filter makepri.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    if (-not $found) { throw "makepri.exe of Microsoft.Windows.SDK.BuildTools $($Matches[1]) not found under $packages (restore the app project first)" }
    return $found.FullName
}

$forbidden = @($RuleSet.forbidden | ForEach-Object { [pscustomobject]@{ Glob = $_; Regex = ConvertTo-GlobRegex $_ } })
$sources = @(Get-ChildItem (Join-Path $RepoRoot 'sources') -Directory | Where-Object { Test-Path (Join-Path $_.FullName "$($_.Name).js") } |
        ForEach-Object Name)
$required = @(foreach ($pattern in $RuleSet.required) {
        $pattern -like '*{source}*' ? @($sources | ForEach-Object { $pattern.Replace('{source}', $_) }) : @($pattern)
    })

# rules: a path cannot be both required and forbidden.
Add-Check 'rules' @(foreach ($requiredPath in $required) {
        foreach ($rule in $forbidden) { if ($rule.Regex.IsMatch($requiredPath)) { "Rules conflict: $requiredPath is required and forbidden ($($rule.Glob))" } }
    })

# allowedExtensions
$allowed = @($RuleSet.allowedExtensions)
Add-Check 'allowedExtensions' @($Files | Where-Object { $allowed -notcontains $_.Item.Extension.ToLowerInvariant() } |
        ForEach-Object { "Extension not allowed: $($_.Relative)" })

# forbidden
Add-Check 'forbidden' @(foreach ($file in $Files) {
        foreach ($rule in $forbidden) {
            if ($rule.Regex.IsMatch($file.Relative)) { "Forbidden file: $($file.Relative) (rule $($rule.Glob))" }
        }
    })

# required
$present = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$Files | ForEach-Object { [void]$present.Add($_.Relative) }
Add-Check 'required' @($required | Where-Object { -not $present.Contains($_) } | ForEach-Object { "Required file missing: $_" })

# bundledScripts: a stale or extra script would run in the app (Constitution III, research R5).
Add-Check 'bundledScripts' @(
    foreach ($id in $sources) {
        $bundled = Join-Path $Path "Sources/$id/$id.js"
        if (-not (Test-Path $bundled)) { continue }  # reported by 'required'
        $original = Join-Path $RepoRoot "sources/$id/$id.js"
        if ((Get-FileHash $bundled).Hash -ne (Get-FileHash $original).Hash) { "Sources/$id/$id.js differs from sources/$id/$id.js" }
    }
    $Files | Where-Object { $_.Relative -like 'Sources/*' } | Where-Object {
        $parts = $_.Relative.Split('/')
        $parts.Count -ne 3 -or $sources -notcontains $parts[1] -or $parts[2] -ne "$($parts[1]).js"
    } | ForEach-Object { "Unexpected file in Sources: $($_.Relative)" })

# priLanguages: .resw files are compiled into the PRI, so the languages are checked there.
$pri = Join-Path $Path 'HentaiReader.pri'
Add-Check 'priLanguages' @(
    if (-not (Test-Path $pri)) { 'HentaiReader.pri missing' }
    else {
        $dump = Join-Path ([IO.Path]::GetTempPath()) "hentaireader-pri-$PID"
        try {
            $output = & (Find-MakePri) dump /if $pri /of $dump /o /dt detailed 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) { "makepri dump failed: $output" }
            else {
                [xml] $xml = Get-Content "$dump.xml" -Raw
                $resource = $xml.SelectNodes('//NamedResource') | Where-Object { $_.uri -like "*/$($RuleSet.priResourceProbe)" } | Select-Object -First 1
                if (-not $resource) { "Resource $($RuleSet.priResourceProbe) not found in HentaiReader.pri" }
                else {
                    $found = @($resource.SelectNodes('.//Candidate/QualifierSet/Qualifier[@name="Language"]') | ForEach-Object { $_.value.ToUpperInvariant() })
                    $RuleSet.languages | Where-Object { $found -notcontains $_.ToUpperInvariant() } | ForEach-Object { "Language missing in HentaiReader.pri: $_" }
                }
            }
        }
        finally {
            Remove-Item "$dump.xml" -ErrorAction SilentlyContinue
        }
    })

# peMachine: IL-only images are i386 with a CLR header; everything else (native and ReadyToRun) must be x64.
$expectedMachine = [Convert]::ToUInt16($RuleSet.nativeMachine.x64, 16)
Add-Check 'peMachine' @(foreach ($file in $Files | Where-Object { $_.Item.Extension -in '.dll', '.exe' }) {
        $pe = Get-PeInfo $file.Item.FullName
        if ($null -eq $pe) { "Not a PE file: $($file.Relative)"; continue }
        if ($pe.Machine -eq 0x014C -and $pe.Managed) { continue }
        if ($pe.Machine -ne $expectedMachine) { '{0}: machine 0x{1:X4}, expected 0x{2:X4}' -f $file.Relative, $pe.Machine, $expectedMachine }
    })

# productVersion: the SDK appends "+<commit>"; only the release version is compared.
$exe = Join-Path $Path 'HentaiReader.exe'
$productVersion = (Test-Path $exe) ? (((Get-Item $exe).VersionInfo.ProductVersion ?? '') -split '\+')[0] : '(missing)'
Add-Check 'productVersion' @(if ($productVersion -ne $Version) { "HentaiReader.exe product version $productVersion, expected $Version" })

# manifest: written by Inno Setup (OutputManifestFile), tab-separated with a header row.
if ($Manifest) {
    $rows = Import-Csv $Manifest -Delimiter "`t"
    $packed = @{}
    foreach ($row in $rows) {
        # Inno Setup 7 writes long-path names (\\?\C:\... or \\?\UNC\server\...).
        $source = $row.SourceFilename.Replace('/', '\')
        if ($source.StartsWith('\\?\UNC\')) { $source = '\\' + $source.Substring(8) }
        elseif ($source.StartsWith('\\?\')) { $source = $source.Substring(4) }
        $relative = $source.StartsWith("$Path\", [StringComparison]::OrdinalIgnoreCase) ? $source.Substring($Path.Length + 1).Replace('\', '/') : "(outside publish folder) $source"
        $packed[$relative] = $row.SHA256Sum
    }

    Add-Check 'manifest' @(
        $packed.Keys | Where-Object { -not $present.Contains($_) } | ForEach-Object { "Packed but not verified: $_" }
        foreach ($file in $Files) {
            if (-not $packed.ContainsKey($file.Relative)) { "Verified but not packed: $($file.Relative)"; continue }
            $hash = (Get-FileHash $file.Item.FullName -Algorithm SHA256).Hash
            if ($hash -ne $packed[$file.Relative]) { "SHA-256 differs between folder and installer: $($file.Relative)" }
        })
}

$signature = (Test-Path $exe) ? (Get-AuthenticodeSignature $exe).Status : 'NotSigned'
$result = [ordered]@{
    version = $Version
    architecture = 'x64'
    publishDir = $Path
    fileCount = $Files.Count
    totalBytes = ($Files | Measure-Object { $_.Item.Length } -Sum).Sum
    signed = "$signature" -eq 'Valid'
    checks = $Checks
    passed = -not ($Checks | Where-Object { -not $_.passed })
}

if ($Report) {
    $result | ConvertTo-Json -Depth 5 | Set-Content $Report -Encoding utf8
}

foreach ($check in $Checks) {
    if ($check.passed) {
        Write-Host "  [ok]   $($check.name)"
    }
    else {
        Write-Host "  [FAIL] $($check.name)" -ForegroundColor Red
        $check.details | Select-Object -First 20 | ForEach-Object { Write-Host "         $_" -ForegroundColor Red }
    }
}

exit ($result.passed ? 0 : 1)
