#Requires -Version 7.2
<#
.SYNOPSIS
    Builds, verifies and packages the Windows distribution of Hentai Reader
    (specs/004-windows-installer/contracts/build-installer.md).

.DESCRIPTION
    Release build and tests, self-contained publish, verification of the published files against
    windows/installer/distribution-rules.json, Inno Setup compilation with a manifest check, and the
    portable ZIP of the same verified folder. Stops at the first failure; a failed run leaves no
    installer and no ZIP in the output folder.

.EXAMPLE
    pwsh windows/build/Build-Installer.ps1
    pwsh windows/build/Build-Installer.ps1 -BootstrapTools -SmokeTest
#>
[CmdletBinding()]
param(
    [switch] $SkipTests,
    [switch] $SmokeTest,
    [switch] $BootstrapTools,
    [string] $InnoSetupPath,
    [string] $OutputDir = 'artifacts/installer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Windows = Join-Path $Root 'windows'
$OutputDir = [IO.Path]::GetFullPath((Join-Path $Root $OutputDir))
$PublishDir = Join-Path $Root 'artifacts/publish/win-x64'
$Script = Join-Path $Windows 'installer/HentaiReader.iss'
$Tools = Get-Content (Join-Path $Windows 'installer/tools.json') -Raw | ConvertFrom-Json
$Summary = [ordered]@{}

# Optional code signing (research R11): a signtool command line with $f for the file, e.g.
# signtool sign /fd sha256 /a /tr http://timestamp.digicert.com /td sha256 $f
$SignCommand = $env:HENTAIREADER_SIGN_COMMAND

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    try {
        & $Action
    }
    catch {
        throw "Step '$Name' failed: $($_.Exception.Message)"
    }
}

function Invoke-Native([string] $File, [string[]] $Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$([IO.Path]::GetFileName($File)) exited with code $LASTEXITCODE"
    }
}

function Invoke-Sign([string] $File) {
    $command = $SignCommand.Replace('$f', "`"$File`"")
    & cmd.exe /d /c $command
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $File (exit code $LASTEXITCODE)" }
}

function Format-Size([long] $Bytes) {
    [string]::Format([cultureinfo]::InvariantCulture, '{0:N1} MB', $Bytes / 1MB)
}

function Get-InnoSetupVersion([string] $Iscc) {
    # ISCC has no file version; it reports the engine version while compiling. Compile a probe without output.
    $probe = Join-Path ([IO.Path]::GetTempPath()) "hentaireader-iscc-probe-$PID.iss"
    Set-Content $probe "[Setup]`r`nAppName=Probe`r`nAppVersion=1`r`nDefaultDirName={tmp}\Probe`r`nOutput=no" -Encoding utf8
    try {
        $output = & $Iscc $probe 2>&1 | Out-String
        if ($output -match 'Compiler engine version: Inno Setup (\S+)') { return $Matches[1] }
        throw "Could not read the Inno Setup version from: $output"
    }
    finally {
        Remove-Item $probe -ErrorAction SilentlyContinue
    }
}

function Install-InnoSetup {
    $target = Join-Path $Root '.tools/innosetup'
    $download = Join-Path $Root ".tools/download/$(Split-Path $Tools.innoSetup.url -Leaf)"
    New-Item -ItemType Directory -Force (Split-Path $download) | Out-Null
    if (-not (Test-Path $download)) {
        Write-Host "Downloading $($Tools.innoSetup.url)"
        Invoke-WebRequest $Tools.innoSetup.url -OutFile $download
    }

    $hash = (Get-FileHash $download -Algorithm SHA256).Hash
    if ($hash -ne $Tools.innoSetup.sha256) {
        Remove-Item $download
        throw "SHA-256 mismatch for the Inno Setup installer: $hash, expected $($Tools.innoSetup.sha256)"
    }

    $signature = Get-AuthenticodeSignature $download
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch [regex]::Escape($Tools.innoSetup.signer)) {
        throw "Unexpected signature on the Inno Setup installer: $($signature.Status) $($signature.SignerCertificate.Subject)"
    }

    $process = Start-Process $download -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/PORTABLE=1', "/DIR=`"$target`"")
    if ($process.ExitCode -ne 0) {
        throw "Inno Setup installation failed with exit code $($process.ExitCode)"
    }

    return Join-Path $target 'ISCC.exe'
}

function Find-Iscc {
    $candidates = @(
        $InnoSetupPath,
        $env:INNO_SETUP_PATH,
        (Join-Path $Root '.tools/innosetup/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 7/ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }

    $iscc = $candidates | Select-Object -First 1
    if (-not $iscc -and $BootstrapTools) {
        $iscc = Install-InnoSetup
    }

    if (-not $iscc) {
        throw "Inno Setup $($Tools.innoSetup.version) not found. Run with -BootstrapTools, or install it: winget install $($Tools.innoSetup.wingetId) --version $($Tools.innoSetup.version)"
    }

    $version = Get-InnoSetupVersion $iscc
    if ($version -ne $Tools.innoSetup.version) {
        throw "Inno Setup $version found at $iscc, but windows/installer/tools.json pins $($Tools.innoSetup.version). Run with -BootstrapTools or install the pinned version."
    }

    return $iscc
}

function Test-Distribution([string[]] $Extra) {
    Invoke-Native pwsh (@('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-Distribution.ps1'), '-Path', $PublishDir,
            '-Version', $version, '-Report', (Join-Path $OutputDir 'verification-x64.json')) + $Extra)
}

try {
    Invoke-Step 'Prepare output folders' {
        # MakePri (Windows SDK) fails with "path not found" on deep paths (reference installer, ~130 characters
        # below the repository root).
        if ($Root.Length -gt 120) {
            throw "Repository path is $($Root.Length) characters long ($Root). Clone it to a path of at most 120 characters; longer paths break the Windows SDK resource tools."
        }

        foreach ($folder in $OutputDir, $PublishDir) {
            if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
            New-Item -ItemType Directory -Force $folder | Out-Null
        }
    }

    $version = $null
    $iscc = $null
    Invoke-Step 'Read version and check tools' {
        $script:version = (& dotnet msbuild (Join-Path $Windows 'Directory.Build.props') -getProperty:Version | Out-String).Trim()
        if ($script:version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected version '$($script:version)' in windows/Directory.Build.props" }
        $script:iscc = Find-Iscc
        $Summary.Version = $script:version
        $Summary.InnoSetup = $Tools.innoSetup.version
        $Summary.Signing = $SignCommand ? 'signed' : 'unsigned (HENTAIREADER_SIGN_COMMAND not set)'
    }

    Invoke-Step 'Release build (warnings as errors)' {
        Invoke-Native dotnet @('build', (Join-Path $Windows 'HentaiReader.slnx'), '-c', 'Release', '-p:Platform=x64', '-nologo', '-v', 'q')
    }

    if ($SkipTests) {
        Write-Warning 'Tests skipped (-SkipTests). Do not distribute this build.'
        $Summary.Tests = 'skipped'
    }
    else {
        Invoke-Step 'Source and runner tests' {
            Push-Location $Root
            try {
                # The contract JSON schemas are generated, not committed; a clean clone has none.
                Invoke-Native npm @('run', 'schema', '--silent')
                Invoke-Native npm @('test', '--silent')
            }
            finally { Pop-Location }
        }
        Invoke-Step 'Windows tests' {
            Invoke-Native dotnet @('test', '--project', (Join-Path $Windows 'tests/HentaiReader.Host.Tests'), '-c', 'Release', '--no-build')
        }
        $Summary.Tests = 'passed'
    }

    Invoke-Step 'Publish win-x64' {
        Invoke-Native dotnet @('publish', (Join-Path $Windows 'src/HentaiReader.App/HentaiReader.App.csproj'),
            '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64', '-o', $PublishDir, '-nologo', '-v', 'q')
    }

    if ($SignCommand) {
        Invoke-Step 'Sign app binaries' {
            foreach ($name in 'HentaiReader.exe', 'HentaiReader.dll', 'HentaiReader.Host.dll') {
                Invoke-Sign (Join-Path $PublishDir $name)
            }
        }
    }

    Invoke-Step 'Verify published files' { Test-Distribution @() }

    $files = Get-ChildItem $PublishDir -Recurse -File
    $publishBytes = ($files | Measure-Object Length -Sum).Sum
    $Summary.Publish = "$($files.Count) files, $(Format-Size $publishBytes)"

    Invoke-Step 'Compile installer' {
        $arguments = @('/Q', "/DAppVersion=$version", "/DSourceDir=$PublishDir", "/O$OutputDir")
        if ($SignCommand) { $arguments += @('/DSign', "/Ssign=$SignCommand") }
        Invoke-Native $iscc ($arguments + $Script)
    }

    # The compiler's manifest proves the installer contains exactly the verified files (research R3).
    Invoke-Step 'Verify installer content' { Test-Distribution @('-Manifest', (Join-Path $OutputDir 'manifest-x64.txt')) }

    $installer = Get-Item (Join-Path $OutputDir "HentaiReader-Setup-$version-x64.exe")
    $Summary.Installer = "$($installer.Name) ($(Format-Size $installer.Length))"
    if ($installer.Length -gt $publishBytes * 1.1) { throw "Installer is larger than the publish output + 10 % (SC-006)" }

    Invoke-Step 'Portable ZIP' {
        $zip = Join-Path $OutputDir "HentaiReader-$version-x64-portable.zip"
        Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $zip -CompressionLevel Optimal
        $Summary.Portable = "$([IO.Path]::GetFileName($zip)) ($(Format-Size (Get-Item $zip).Length))"
    }

    if ($SmokeTest) {
        $smoke = Join-Path $Root 'artifacts/smoke'
        Invoke-Step 'Smoke test' {
            if (Test-Path $smoke) { Remove-Item $smoke -Recurse -Force }
            # A lower-version build of the same content is the downgrade and upgrade-from candidate.
            Invoke-Native $iscc @('/Q', '/DAppVersion=0.0.1', "/DSourceDir=$PublishDir", "/O$smoke", $Script)
            Invoke-Native pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-InstallerSmoke.ps1'),
                '-Installer', $installer.FullName, '-Version', $version,
                '-OlderInstaller', (Join-Path $smoke 'HentaiReader-Setup-0.0.1-x64.exe'), '-Report', (Join-Path $OutputDir 'smoke-x64.json'))
        }
        $Summary.SmokeTest = 'passed'
    }

    $Summary.Result = 'passed'
    Write-Host "Build succeeded: version $version" -ForegroundColor Green
}
catch {
    # A failed run must not leave an installer or a ZIP behind (spec, User Story 2).
    Get-ChildItem $OutputDir -Include '*.exe', '*.zip' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
    Write-Host $_.Exception.Message -ForegroundColor Red
    $Summary.Result = "FAILED: $($_.Exception.Message)"
}
finally {
    if (Test-Path $OutputDir) {
        $Summary.GetEnumerator() | ForEach-Object { '{0}: {1}' -f $_.Key, $_.Value } |
            Set-Content (Join-Path $OutputDir 'summary.txt') -Encoding utf8
    }
}

exit ($Summary.Result -eq 'passed' ? 0 : 1)
