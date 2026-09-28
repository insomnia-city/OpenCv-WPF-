<#
  package_release.ps1 - build the distributable package (delivery agreement 7, item 2).

  Produces a single folder plus a zip:

    dist/HalconWorkflow-<version>-win-x64/
      <app files>                 self-contained or framework-dependent publish output
      LICENSE                     proprietary, (c) 2026 Chen Lang
      THIRD-PARTY-NOTICES.md      dependency ledger
      third-party/...             verbatim upstream license texts (Apache-2.0, LGPL note)
      docs/DEPLOYMENT.md          install / layout / config / uninstall
      MANIFEST.txt                file inventory
      SHA256SUMS.txt              per-file hashes, plus the zip's own hash
    dist/HalconWorkflow-<version>-win-x64.zip

  Version defaults to dev-<date>-<short sha> rather than a semantic version on purpose:
  the project has no Version property and no release tag yet (agreement 7, item 4), so
  claiming 1.0.0 here would assert a versioning decision that has not been made.

  The compliance checks inspect the produced files rather than trusting the MSBuild
  deletion patterns. That distinction is not academic: option B originally only removed
  runtimes\win-x64\native\*, which a RID-specific publish flattens into the output root,
  so the LGPL FFmpeg DLL (27.3 MB) was silently shipped. Checking content is what caught
  it and what keeps it caught.

  ASCII-only output: this file is UTF-8 without BOM and Windows PowerShell 5.1
  mis-decodes non-ASCII in such scripts.
#>
[CmdletBinding()]
param(
    # Release label. Default derived from date + short commit.
    [string] $Version = '',

    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',

    # Self-contained ships the .NET runtime, so the target machine needs nothing installed.
    # Switch off for a much smaller package that requires .NET 9 Desktop Runtime.
    [switch] $FrameworkDependent,

    [string] $Configuration = 'Release',
    [string] $DistRoot = (Join-Path $env:TEMP 'halcon-dist'),
    [switch] $SkipZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
Set-Location -LiteralPath $repoRoot

function Step { param([string] $T) Write-Host ''; Write-Host ("== " + $T) -ForegroundColor Cyan }
function Ok   { param([string] $M) Write-Host ("   PASS  " + $M) -ForegroundColor Green }
function Bad  { param([string] $M) Write-Host ("   FAIL  " + $M) -ForegroundColor Red }

# dotnet writes diagnostics to stderr; under ErrorActionPreference=Stop that becomes a
# terminating NativeCommandError in Windows PowerShell 5.1, so relax it around the call.
function Invoke-Dotnet {
    param([string[]] $Arguments)
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = (& dotnet @Arguments 2>&1 | Out-String)
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $saved
    }
    return [pscustomobject] @{ ExitCode = $code; Output = $text }
}

# ---------------------------------------------------------------- version
if ([string]::IsNullOrEmpty($Version)) {
    $sha = (git rev-parse --short HEAD 2>$null | Out-String).Trim()
    if ([string]::IsNullOrEmpty($sha)) { $sha = 'nosha' }
    $Version = 'dev-' + (Get-Date -Format 'yyyyMMdd') + '-' + $sha
}
$pkgName = 'HalconWorkflow-' + $Version + '-' + $Runtime
$stage = Join-Path $DistRoot $pkgName
$zipPath = $pkgName + '.zip'

Write-Host ("Packaging " + $pkgName) -ForegroundColor White
Write-Host ("  mode      : " + $(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))
Write-Host ("  dist root : " + $DistRoot)

Remove-Item -LiteralPath $DistRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# ---------------------------------------------------------------- publish
Step '1/5  publish'
$pubDir = Join-Path $DistRoot '_publish'
$pubArgs = @(
    'publish', 'src\App\HalconWorkflow.App.csproj',
    '-c', $Configuration, '--nologo', '-o', $pubDir
)
if (-not $FrameworkDependent) {
    $pubArgs += @('-r', $Runtime, '--self-contained', 'true')
}
$r = Invoke-Dotnet $pubArgs
if ($r.ExitCode -ne 0) {
    Bad ('publish failed, exit ' + $r.ExitCode)
    ($r.Output -split "`r?`n" | Where-Object { $_ -match 'error' } | Select-Object -First 8) | ForEach-Object { "         $_" }
    exit 1
}
$appFiles = @(Get-ChildItem -LiteralPath $pubDir -Recurse -File)
Ok ($appFiles.Count.ToString() + ' files, ' + [math]::Round((($appFiles | Measure-Object Length -Sum).Sum / 1mb), 1) + ' MB')

# ---------------------------------------------------------------- stage
Step '2/5  stage payload and license texts'
Get-ChildItem -LiteralPath $pubDir -Force | ForEach-Object {
    Move-Item -LiteralPath $_.FullName -Destination $stage -Force
}
Remove-Item -LiteralPath $pubDir -Recurse -Force -ErrorAction SilentlyContinue

foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item -LiteralPath (Join-Path $repoRoot $f) -Destination $stage -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'third-party') -Destination $stage -Recurse -Force

$docDir = Join-Path $stage 'docs'
New-Item -ItemType Directory -Path $docDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\DEPLOYMENT.md') -Destination $docDir -Force
Ok 'app payload + LICENSE + THIRD-PARTY-NOTICES.md + third-party/ + docs/DEPLOYMENT.md'

# ---------------------------------------------------------------- compliance
Step '3/5  compliance checks on produced files'
$staged = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
$problems = New-Object System.Collections.Generic.List[string]

# Option B: FFmpeg must be absent. This is the assertion that the earlier gap failed.
$ffmpeg = @($staged | Where-Object { $_.Name -like 'opencv_videoio_ffmpeg*' })
if ($ffmpeg.Count -ne 0) {
    $problems.Add(('FFmpeg native present: ' + (($ffmpeg | ForEach-Object { $_.Name }) -join ', ') + ' -- LGPL obligations are live'))
} else {
    Ok 'no opencv_videoio_ffmpeg*.dll anywhere in the package (LGPL option B holds)'
}

# Guard against the opposite failure: excluding too much and breaking OpenCV.
$openCvNative = @($staged | Where-Object { $_.Name -eq 'OpenCvSharpExtern.dll' })
if ($openCvNative.Count -eq 0) {
    $problems.Add('OpenCvSharpExtern.dll is missing -- the vision backend cannot load')
} else {
    Ok 'OpenCvSharpExtern.dll present (vision backend loadable)'
}

# Apache-2.0 4(a) requires the license to travel with the distribution. The NuGet packages
# ship none, so these files are the only thing satisfying it.
$requiredLicenses = @(
    'third-party\opencv\LICENSE',
    'third-party\opencv\COPYRIGHT',
    'third-party\opencv\LICENSE_CHANGE_NOTICE.txt',
    'third-party\opencvsharp\LICENSE',
    'third-party\ffmpeg\NOTICE.md',
    'THIRD-PARTY-NOTICES.md',
    'LICENSE'
)
$missingLic = @($requiredLicenses | Where-Object { -not (Test-Path -LiteralPath (Join-Path $stage $_)) })
if ($missingLic.Count -ne 0) {
    $problems.Add(('required license text missing: ' + ($missingLic -join ', ')))
} else {
    Ok ($requiredLicenses.Count.ToString() + ' required license/notice files present')
}

$entry = if ($staged | Where-Object { $_.Name -eq 'HalconWorkflow.App.exe' }) { 'HalconWorkflow.App.exe' } else { 'MISSING' }
if ($entry -eq 'MISSING') { $problems.Add('HalconWorkflow.App.exe entry point missing') } else { Ok "entry point $entry" }

if ($problems.Count -ne 0) {
    foreach ($p in $problems) { Bad $p }
    Write-Host ''
    Write-Host 'PACKAGING FAILED' -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- manifest
Step '4/5  manifest and hashes'
$finalFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
$manifest = New-Object System.Text.StringBuilder
[void]$manifest.AppendLine('Package : ' + $pkgName)
[void]$manifest.AppendLine('Mode    : ' + $(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))
[void]$manifest.AppendLine('Runtime : ' + $Runtime)
[void]$manifest.AppendLine('Commit  : ' + ((git rev-parse HEAD 2>$null | Out-String).Trim()))
[void]$manifest.AppendLine('Built   : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
[void]$manifest.AppendLine('Files   : ' + $finalFiles.Count)
[void]$manifest.AppendLine('')
[void]$manifest.AppendLine('== inventory ==')
foreach ($f in ($finalFiles | Sort-Object FullName)) {
    $rel = $f.FullName.Substring($stage.Length + 1)
    [void]$manifest.AppendLine(('{0,12:N0}  {1}' -f $f.Length, $rel))
}
[System.IO.File]::WriteAllText((Join-Path $stage 'MANIFEST.txt'), $manifest.ToString(), (New-Object System.Text.UTF8Encoding($false)))

$sums = New-Object System.Text.StringBuilder
[void]$sums.AppendLine('# SHA-256 per file. Verify with: Get-FileHash -Algorithm SHA256 <file>')
foreach ($f in ($finalFiles | Sort-Object FullName)) {
    $rel = $f.FullName.Substring($stage.Length + 1)
    $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    [void]$sums.AppendLine($h.ToLower() + '  ' + $rel)
}
[System.IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.txt'), $sums.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Ok ('MANIFEST.txt and SHA256SUMS.txt written for ' + $finalFiles.Count + ' files')

# ---------------------------------------------------------------- zip
Step '5/5  zip'
if ($SkipZip) {
    Ok 'skipped by -SkipZip'
} else {
    $zipFull = Join-Path $DistRoot $zipPath
    # Archive the folder itself, not its contents, so the zip carries a single top-level
    # directory. Archiving "$stage\*" would scatter ~200 files straight into whatever
    # directory the user extracts into.
    Compress-Archive -Path $stage -DestinationPath $zipFull -CompressionLevel Optimal
    $zh = (Get-FileHash -LiteralPath $zipFull -Algorithm SHA256).Hash.ToLower()
    $zsz = [math]::Round(((Get-Item $zipFull).Length / 1mb), 1)
    [System.IO.File]::WriteAllText((Join-Path $DistRoot ($zipPath + '.sha256')), ($zh + '  ' + $zipPath), (New-Object System.Text.UTF8Encoding($false)))

    # Verify the archive really carries the wrapper directory and the license texts, rather
    # than trusting that Compress-Archive did what was asked.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $za = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
    $entries = @($za.Entries | ForEach-Object { $_.FullName })
    $za.Dispose()
    $bad = New-Object System.Collections.Generic.List[string]
    if (-not ($entries | Where-Object { $_.StartsWith($pkgName + '\') -or $_.StartsWith($pkgName + '/') })) {
        $bad.Add('zip has no single top-level ' + $pkgName + ' directory')
    }
    foreach ($must in 'third-party\opencv\LICENSE', 'third-party\ffmpeg\NOTICE.md', 'docs\DEPLOYMENT.md', 'THIRD-PARTY-NOTICES.md') {
        $norm = $must -replace '/', '\'
        if (-not ($entries | Where-Object { $_.EndsWith($norm) })) { $bad.Add('zip missing ' + $must) }
    }
    if ($bad.Count -ne 0) {
        foreach ($b in $bad) { Bad $b }
        Write-Host ''
        Write-Host 'PACKAGING FAILED' -ForegroundColor Red
        exit 1
    }

    Ok ($zipPath + '  ' + $zsz + ' MB  (' + $entries.Count + ' entries, top-level ' + $pkgName + ')')
    Ok ('sha256 ' + $zh)
}

Write-Host ''
Write-Host 'PACKAGE OK' -ForegroundColor Green
Write-Host ('  folder : ' + $stage)
if (-not $SkipZip) { Write-Host ('  zip    : ' + (Join-Path $DistRoot $zipPath)) }
exit 0
