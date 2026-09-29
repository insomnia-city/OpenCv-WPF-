<#
  ci_gate.ps1 - the delivery gate, single source of truth (delivery agreement 7, item 1).

  The hosted CI workflow (.github/workflows/ci.yml) does nothing but call this script, so
  "green" means exactly the same thing locally and on a runner. Do not re-implement these
  checks in the workflow; extend this file instead.

  Steps
    1  sdk      resolved SDK satisfies global.json
    2  restore  NuGet restore
    3  build    solution builds with zero errors
    4  test     full solution test run, TRX captured
    5  trx      TRX verdict: nothing failed, nothing skipped where it matters, and the
                test count did not silently shrink
    6  publish  App publish, used by step 7
    7  ffmpeg   the option B FFmpeg exclusion actually holds in published output, and we
                did not over-delete the OpenCV native we still depend on
    8  package  the distributable itself: the license texts must travel inside it,
                and option B must still hold for the self-contained layout, which
                is where it once leaked 27 MB of FFmpeg into the shipped zip

  Output is deliberately ASCII-only: the file is stored as UTF-8 without BOM, and Windows
  PowerShell 5.1 mis-decodes non-ASCII in such a script. Runner shells here may be either
  5.1 or pwsh, so the constraint has to hold for both.

  Exit code 0 = every step passed. Any other value = at least one step failed.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',

    # Floor for the test count. Bump it when tests are added. Its purpose is to catch
    # tests disappearing, which a plain "0 failed" verdict would not notice.
    [int]    $BaselineTests = 421,

    [string] $WorkDirectory = (Join-Path $env:TEMP 'halcon-ci-gate')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
Set-Location -LiteralPath $repoRoot

$results = New-Object System.Collections.Generic.List[object]

function Add-Step {
    param([string] $Name, [bool] $Ok, [string] $Detail)
    $results.Add([pscustomobject] @{ Step = $Name; Ok = $Ok; Detail = $Detail })
    $mark = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("      {0}  {1}" -f $mark, $Detail) -ForegroundColor $(if ($Ok) { 'Green' } else { 'Red' })
}

function Start-Step {
    param([string] $Title)
    Write-Host ''
    Write-Host ("== " + $Title) -ForegroundColor Cyan
}

function Invoke-Dotnet {
    param([string[]] $Arguments)
    # Native stderr is folded into the output stream below. Under ErrorActionPreference=Stop
    # Windows PowerShell 5.1 turns that into a terminating NativeCommandError, and a normal
    # diagnostic such as the NU1900 audit warning would abort the gate. Relax it for the
    # duration of the call, then restore.
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

# Report the tail of a failed command, so the log is diagnosable without rerunning.
function Get-Tail {
    param([string] $Text, [int] $Lines = 15)
    $all = @([regex]::Split($Text, '\r?\n') | Where-Object { $_.Trim() -ne '' })
    if ($all.Count -le $Lines) { return ($all -join "`n") }
    return (($all | Select-Object -Last $Lines) -join "`n")
}

Write-Host ("Delivery gate  config=" + $Configuration + "  baseline=" + $BaselineTests + "  root=" + $repoRoot) -ForegroundColor White

# Layout under WorkDirectory:
#   results/         TRX files from the test run
#   publish/         published App output, used by the compliance guard
#   gate-output.txt  full transcript, so CI can attach or summarise the run
# results and publish are wiped at their own steps rather than the whole directory being
# wiped up front, which would delete the transcript out from under us.
$resultsDir = Join-Path $WorkDirectory 'results'
$publishDir = Join-Path $WorkDirectory 'publish'
$logPath = Join-Path $WorkDirectory 'gate-output.txt'
Remove-Item -LiteralPath $WorkDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
Start-Transcript -LiteralPath $logPath -Force | Out-Null

# ---------------------------------------------------------------- 1  sdk
Start-Step '1/8  SDK pinned by global.json'
$expectedSdk = [string](Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
$saved = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$actualSdk = (& dotnet --version 2>&1 | Out-String).Trim()
$ErrorActionPreference = $saved
# rollForward=latestFeature means the resolved SDK may be a higher patch on the same
# feature band than the pinned version. The gate's job is to prove resolution honoured
# global.json, which is: same major, and at least the pinned version. A prefix match was
# wrong - it failed a legitimate forward roll from 9.0.317 to 9.0.318, which is exactly
# what rollForward allows. Compare as versions instead, while refusing a different major
# (a 10.x would roll past the pin's band entirely, which latestFeature does not allow).
$expectedOk = $true
try { $ev = [System.Version]::Parse($expectedSdk); $av = [System.Version]::Parse($actualSdk) } catch { $expectedOk = $false }
if ($expectedOk -and $av.Major -eq $ev.Major -and $av -ge $ev) {
    Add-Step 'sdk' $true ("expected " + $expectedSdk + " (rollForward latestFeature), resolved " + $actualSdk)
} else {
    Add-Step 'sdk' $false ("expected " + $expectedSdk + ", resolved " + $actualSdk)
}

# ---------------------------------------------------------------- 2  restore
Start-Step '2/8  restore'
$r = Invoke-Dotnet @('restore', 'HalconWorkflow.sln', '--nologo')
if ($r.ExitCode -eq 0) {
    Add-Step 'restore' $true 'packages restored'
} else {
    Add-Step 'restore' $false ("exit " + $r.ExitCode + "`n" + (Get-Tail $r.Output))
}

# ---------------------------------------------------------------- 3  build
Start-Step ('3/8  build (' + $Configuration + ')')
$r = Invoke-Dotnet @('build', 'HalconWorkflow.sln', '-c', $Configuration, '--no-restore', '--nologo')
$buildOut = $r.Output
$errorCount = ([regex]::Matches($buildOut, ': error ')).Count
$warnCount = ([regex]::Matches($buildOut, ': warning ')).Count
$summaryLine = (@([regex]::Split($buildOut, '\r?\n') | Where-Object { $_ -match '\d+\s+(Warning|Error)' }) | Select-Object -Last 1)
if ($r.ExitCode -eq 0 -and $errorCount -eq 0) {
    Add-Step 'build' $true ("0 errors, " + $warnCount + " warnings" + $(if ($summaryLine) { "  [" + $summaryLine.Trim() + "]" } else { "" }))
} else {
    Add-Step 'build' $false ("exit " + $r.ExitCode + ", errors=" + $errorCount + "`n" + (Get-Tail $buildOut))
}

# ---------------------------------------------------------------- 4  test
Start-Step '4/8  test'
Remove-Item -LiteralPath $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
# No LogFileName here on purpose. A fixed name makes every test project write to the same
# path, so they clobber each other and only the last one to finish survives. Letting VSTest
# pick per-project names is what makes the full-solution result complete.
$r = Invoke-Dotnet @(
    'test', 'HalconWorkflow.sln', '-c', $Configuration, '--no-build', '--nologo',
    '--logger', 'trx',
    '--results-directory', $resultsDir
)
$trxFiles = @(Get-ChildItem -LiteralPath $resultsDir -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue)
if ($trxFiles.Count -eq 0) {
    Add-Step 'test' $false ("no TRX produced, exit " + $r.ExitCode + "`n" + (Get-Tail $r.Output))
} else {
    Add-Step 'test' $true ($trxFiles.Count.ToString() + ' TRX file(s) captured under ' + $resultsDir)
}

# ---------------------------------------------------------------- 5  trx verdict
Start-Step '5/8  TRX verdict'
if ($trxFiles.Count -eq 0) {
    Add-Step 'trx' $false 'no TRX to read'
    Add-Step 'vision-native' $false 'no TRX to read'
} else {
    $perAsm = @{}
    $passed = 0; $failed = 0; $skipped = 0; $other = 0

    foreach ($file in $trxFiles) {
        $xml = New-Object System.Xml.XmlDocument
        $xml.Load($file.FullName)
        $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
        $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

        # UnitTestResult carries no assembly attribute, so map testId to assembly through
        # TestDefinitions/UnitTest/TestMethod/@codeBase, which holds the built test dll.
        $idToAsm = @{}
        foreach ($def in $xml.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
            $id = $def.GetAttribute('id')
            if ([string]::IsNullOrEmpty($id)) { continue }
            $asm = '<unknown>'
            $method = $def.SelectSingleNode('t:TestMethod', $ns)
            if ($null -ne $method) {
                $codeBase = $method.GetAttribute('codeBase')
                if (-not [string]::IsNullOrEmpty($codeBase)) {
                    $asm = [System.IO.Path]::GetFileNameWithoutExtension($codeBase)
                }
            }
            $idToAsm[$id] = $asm
        }

        foreach ($n in $xml.SelectNodes('//t:UnitTestResult', $ns)) {
            $testId = $n.GetAttribute('testId')
            $asm = if ($idToAsm.ContainsKey($testId)) { $idToAsm[$testId] } else { '<unknown>' }
            if (-not $perAsm.ContainsKey($asm)) { $perAsm[$asm] = @{ Passed = 0; Failed = 0; Skipped = 0; Other = 0 } }
            switch ($n.GetAttribute('outcome')) {
                'Passed'      { $perAsm[$asm].Passed++; $passed++ }
                'Failed'      { $perAsm[$asm].Failed++; $failed++ }
                'NotExecuted' { $perAsm[$asm].Skipped++; $skipped++ }
                default       { $perAsm[$asm].Other++; $other++ }
            }
        }
    }

    Write-Host ''
    Write-Host ('      {0,-46} {1,7} {2,7} {3,8}' -f 'assembly', 'passed', 'failed', 'skipped')
    foreach ($k in ($perAsm.Keys | Sort-Object)) {
        $e = $perAsm[$k]
        Write-Host ('      {0,-46} {1,7} {2,7} {3,8}' -f $k, $e.Passed, $e.Failed, $e.Skipped)
    }
    Write-Host ('      {0,-46} {1,7} {2,7} {3,8}' -f 'TOTAL', $passed, $failed, $skipped)

    if ($failed -ne 0) {
        # A failed run must name its failures in the gate output itself, otherwise the
        # failing tests are unreachable from the step summary and only survive in the TRX,
        # which takes an authenticated download to inspect.
        # · 失败必须点名:否则失败测试只存在于 TRX 里,而 TRX 需登录下载才能查看。
        Write-Host ''
        foreach ($file in $trxFiles) {
            $xml = New-Object System.Xml.XmlDocument
            $xml.Load($file.FullName)
            $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
            $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
            foreach ($n in $xml.SelectNodes('//t:UnitTestResult', $ns)) {
                if ($n.GetAttribute('outcome') -ne 'Failed') { continue }
                $name = $n.GetAttribute('testName')
                $err = ''
                $mt = $n.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
                if ($null -ne $mt) { $err = ($mt.InnerText -replace '\s+', ' ').Trim() }
                if ($err.Length -gt 200) { $err = $err.Substring(0, 200) + '...' }
                Write-Host ('      FAILED  ' + $name)
                if ($err.Length -gt 0) { Write-Host ('         ' + $err) }
            }
        }
        Write-Host ''
    }

    $total = $passed + $failed + $skipped + $other
    $unattributed = 0
    if ($perAsm.ContainsKey('<unknown>')) { $unattributed = $perAsm['<unknown>'].Passed + $perAsm['<unknown>'].Failed + $perAsm['<unknown>'].Skipped + $perAsm['<unknown>'].Other }

    if ($failed -ne 0) {
        Add-Step 'trx' $false ($failed.ToString() + ' test(s) failed')
    } elseif ($other -ne 0) {
        Add-Step 'trx' $false ($other.ToString() + ' test(s) ended in an unexpected state')
    } elseif ($unattributed -ne 0) {
        # A non-zero count here means testId -> assembly attribution is broken, so the
        # per-assembly table above is not trustworthy even though the totals look fine.
        Add-Step 'trx' $false ($unattributed.ToString() + ' test(s) could not be attributed to an assembly')
    } elseif ($total -lt $BaselineTests) {
        Add-Step 'trx' $false ('test count regressed: ' + $total + ' < baseline ' + $BaselineTests + '; raise -BaselineTests only when tests are actually added')
    } else {
        Add-Step 'trx' $true ('no failures, ' + $total + ' across ' + $perAsm.Count + ' assemblies, >= baseline ' + $BaselineTests)
    }

    # The OpenCV vision tests call the real x64 native runtime and skip nothing. If they
    # are absent or skipped, the suite went green without ever loading OpenCV, which is
    # exactly the regression the native probe is meant to catch.
    $visionKey = ($perAsm.Keys | Where-Object { $_ -like '*Vision.Tests*' } | Select-Object -First 1)
    if (-not $visionKey) {
        Add-Step 'vision-native' $false 'no Nodes.Vision.Tests results in the TRX'
    } elseif ($perAsm[$visionKey].Skipped -ne 0) {
        Add-Step 'vision-native' $false ($perAsm[$visionKey].Skipped.ToString() + ' vision test(s) skipped; native OpenCV coverage was not exercised')
    } elseif ($perAsm[$visionKey].Passed -eq 0) {
        Add-Step 'vision-native' $false 'vision tests reported no passes'
    } else {
        Add-Step 'vision-native' $true ($perAsm[$visionKey].Passed.ToString() + ' vision test(s) passed against the real native runtime')
    }
}

# ---------------------------------------------------------------- 6/8  publish + ffmpeg guard
Start-Step '6/8  publish App (license compliance guard)'
Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
$ffmpegOk = $false; $ffmpegDetail = 'not run'
if ($results | Where-Object { $_.Step -eq 'build' -and -not $_.Ok }) {
    Add-Step 'publish' $false 'skipped: build failed'
    Add-Step 'ffmpeg' $false 'skipped: publish not produced'
} else {
    $r = Invoke-Dotnet @('publish', 'src\App\HalconWorkflow.App.csproj', '-c', $Configuration, '--no-restore', '--nologo', '-o', $publishDir)
    if ($r.ExitCode -ne 0) {
        Add-Step 'publish' $false ("exit " + $r.ExitCode + "`n" + (Get-Tail $r.Output))
        Add-Step 'ffmpeg' $false 'skipped: publish not produced'
    } else {
        $files = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File -ErrorAction SilentlyContinue)
        Add-Step 'publish' $true ($files.Count.ToString() + ' files in ' + $publishDir)
    }
}

Start-Step '7/8  FFmpeg option B holds in published output'
$publishOk = [bool]($results | Where-Object { $_.Step -eq 'publish' -and $_.Ok })
if (-not $publishOk) {
    $ffmpegOk = $false
    $ffmpegDetail = 'skipped: no publish output'
    Add-Step 'ffmpeg' $false $ffmpegDetail
} else {
    $files = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
    $ffmpeg = @($files | Where-Object { $_.Name -like 'opencv_videoio_ffmpeg*' })
    $extern = @($files | Where-Object { $_.Name -eq 'OpenCvSharpExtern.dll' })

    if ($ffmpeg.Count -ne 0) {
        $names = ($ffmpeg | ForEach-Object { $_.Name }) -join ', '
        Add-Step 'ffmpeg' $false ('FFmpeg native present (' + $names + '); Directory.Build.targets option B exclusion did not hold')
    } elseif ($extern.Count -eq 0) {
        # Deleting the FFmpeg DLL is intended; deleting the OpenCV native too is not.
        Add-Step 'ffmpeg' $false 'FFmpeg excluded as intended, but OpenCvSharpExtern.dll is also missing; the exclusion deleted more than it should'
    } else {
        Add-Step 'ffmpeg' $true ('FFmpeg excluded (' + $extern.Count + ' OpenCvSharpExtern.dll retained)')
    }
}

# ---------------------------------------------------------------- 8/8  distribution package
Start-Step '8/8  distributable package (Apache-2.0 4(a) + option B)'
# The gate above only proves the framework-dependent publish is clean. A distributable is
# built with a RID and a self-contained payload, which lays natives out differently, and
# that is exactly where option B previously leaked. The license texts must also travel
# inside the package, since no NuGet package carries any.
$pkgScript = 'tools\pack\package_release.ps1'
if (-not (Test-Path -LiteralPath $pkgScript)) {
    Add-Step 'package' $false ('packaging script not found: ' + $pkgScript)
} else {
    $pkgWork = Join-Path $WorkDirectory 'dist'
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $pkgText = (& powershell -NoProfile -ExecutionPolicy Bypass -File $pkgScript -SkipZip -DistRoot $pkgWork 2>&1 | Out-String)
    $pkgCode = $LASTEXITCODE
    $ErrorActionPreference = $saved

    if ($pkgCode -ne 0) {
        Add-Step 'package' $false ('packaging failed, exit ' + $pkgCode + "`n" + (Get-Tail $pkgText))
    } else {
        $pkgDir = @(Get-ChildItem -LiteralPath $pkgWork -Directory -Filter 'HalconWorkflow-*' -ErrorAction SilentlyContinue)
        if ($pkgDir.Count -eq 0) {
            Add-Step 'package' $false 'packaging reported success but produced no package folder'
        } else {
            $pkgFiles = @(Get-ChildItem -LiteralPath $pkgDir[0].FullName -Recurse -File)
            $pkgFfmpeg = @($pkgFiles | Where-Object { $_.Name -like 'opencv_videoio_ffmpeg*' })
            $need = @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'third-party\opencv\LICENSE',
                      'third-party\opencv\COPYRIGHT', 'third-party\opencvsharp\LICENSE',
                      'third-party\ffmpeg\NOTICE.md',
                      'third-party\system.drawing.common\LICENSE.txt',
                      'docs\DEPLOYMENT.md',
                      'MANIFEST.txt', 'SHA256SUMS.txt')
            $absent = @($need | Where-Object { -not (Test-Path -LiteralPath (Join-Path $pkgDir[0].FullName $_)) })

            if ($pkgFfmpeg.Count -ne 0) {
                Add-Step 'package' $false ('FFmpeg native inside the distributable (' + (($pkgFfmpeg | ForEach-Object { $_.Name }) -join ', ') + '); LGPL obligations would be live')
            } elseif ($absent.Count -ne 0) {
                Add-Step 'package' $false ('package is missing ' + ($absent -join ', '))
            } else {
                Add-Step 'package' $true ($pkgFiles.Count.ToString() + ' files, FFmpeg absent, all ' + $need.Count + ' license/manifest files present')
            }
        }
    }
}

# ---------------------------------------------------------------- summary
Write-Host ''
Write-Host '== Gate summary' -ForegroundColor Cyan
foreach ($x in $results) {
    $mark = if ($x.Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ('   {0,-16} {1}' -f $x.Step, $mark) -ForegroundColor $(if ($x.Ok) { 'Green' } else { 'Red' })
}

$bad = @($results | Where-Object { -not $_.Ok })
Write-Host ''
if ($bad.Count -eq 0) {
    Write-Host ('GATE PASSED  (' + $results.Count + ' checks)') -ForegroundColor Green
    $gateExit = 0
} else {
    Write-Host ('GATE FAILED  ' + $bad.Count + ' of ' + $results.Count + ' checks failed: ' + (($bad | ForEach-Object { $_.Step }) -join ', ')) -ForegroundColor Red
    $gateExit = 1
}

# Flush the transcript before exiting, otherwise CI would read a truncated log.
try { Stop-Transcript | Out-Null } catch { }
Write-Host ("transcript: " + $logPath)
exit $gateExit
