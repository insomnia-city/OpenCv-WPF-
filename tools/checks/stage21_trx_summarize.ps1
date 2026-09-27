$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:TEMP "halcon-result-xml"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory -Path $dir | Out-Null

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runLog = Join-Path $dir "runner-out.txt"

# Do NOT pass LogFileName. With a fixed name every test project writes to the same path, so
# they overwrite each other and only the last project to finish is left. Letting VSTest pick
# per-project names is what makes a solution-level run complete.
$dotnetArgs = @(
  "test",
  "HalconWorkflow.sln",
  "--nologo",
  "--no-restore",
  "--logger", "trx",
  "--results-directory", $dir
)

# Invoked directly rather than through ProcessStartInfo: ArgumentList does not exist on
# Windows PowerShell 5.1 (.NET Framework), so the previous ProcessStartInfo form silently
# only ever worked under pwsh 7. This also avoids shadowing the automatic $args variable.
# stderr is folded into the captured text, so ErrorActionPreference is relaxed for the call.
$savedPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$stdout = (& dotnet @dotnetArgs 2>&1 | Out-String)
$code = $LASTEXITCODE
$ErrorActionPreference = $savedPreference
[System.IO.File]::WriteAllText($runLog, $stdout, (New-Object System.Text.UTF8Encoding($false)))


$trxFiles = @(Get-ChildItem -LiteralPath $dir -Filter "*.trx" -Recurse -File)
if ($trxFiles.Count -eq 0) {
    "TRX-MISSING exit=$code"
    "== runner log tail (first 12 lines):"
    (Get-Content $runLog -TotalCount 12) | ForEach-Object { "  " + $_ }
    exit 1
}

$map = [ordered]@{}
$grandPass = 0
$grandTotal = 0
foreach ($trxFile in $trxFiles) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($trxFile.FullName)
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")

    # UnitTestResult carries no assembly attribute, so attribute each result by mapping
    # testId through TestDefinitions/UnitTest/TestMethod/@codeBase, which holds the built
    # test dll. Reading a non-existent "assemblyName" attribute silently yields "", which
    # collapses every test into one unnamed bucket.
    $idToAsm = @{}
    foreach ($def in $xml.SelectNodes("//t:TestDefinitions/t:UnitTest", $ns)) {
        $id = $def.GetAttribute("id")
        if ([string]::IsNullOrEmpty($id)) { continue }
        $asm = "<unknown>"
        $method = $def.SelectSingleNode("t:TestMethod", $ns)
        if ($null -ne $method) {
            $codeBase = $method.GetAttribute("codeBase")
            if (-not [string]::IsNullOrEmpty($codeBase)) {
                $asm = [System.IO.Path]::GetFileNameWithoutExtension($codeBase)
            }
        }
        $idToAsm[$id] = $asm
    }

    foreach ($r in $xml.SelectNodes("//t:UnitTestResult", $ns)) {
        $testId = $r.GetAttribute("testId")
        $asm = if ($idToAsm.ContainsKey($testId)) { $idToAsm[$testId] } else { "<unknown>" }
        if (-not $map.Contains($asm)) { $map[$asm] = @{ pass = 0; total = 0 } }
        $e = $map[$asm]
        $e.total++
        if ($r.GetAttribute("outcome") -eq "Passed") { $e.pass++ ; $grandPass++ }
        $grandTotal++
    }
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("== per-assembly totals (authoritative, aggregated across $($trxFiles.Count) TRX file(s), clean UTF-8):")
foreach ($k in $map.Keys) {
    $e = $map[$k]
    [void]$sb.AppendLine(("  {0}  pass={1}  total={2}" -f $k, $e.pass, $e.total))
}
[void]$sb.AppendLine(("== GRAND TOTAL  pass={0}  total={1}  (violations: {2})  ExitCode={3}" -f $grandPass, $grandTotal, ($grandTotal - $grandPass), $code))

$summaryFile = Join-Path $dir "summary.txt"
[System.IO.File]::WriteAllText($summaryFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
"summary written to: " + $summaryFile
Write-Output $sb.ToString()
exit $code
