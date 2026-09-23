$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:TEMP "halcon-result-xml"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory -Path $dir | Out-Null

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runLog = Join-Path $dir "runner-out.txt"
$errLog = Join-Path $dir "runner-err.txt"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.WorkingDirectory = $scriptDir
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

# Arguments built as a string array; index  ⁄ /  §  all ASCII here.
$trxName = "suite-results.trx"
$args = @(
  "test",
  "HalconWorkflow.sln",
  "--nologo",
  "--no-restore",
  "--logger", ("trx;LogFileName=" + $trxName),
  "--results-directory", $dir
)
foreach ($a in $args) { $null = $psi.ArgumentList.Add($a) }

$proc = [System.Diagnostics.Process]::Start($psi)
$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()
$code = $proc.ExitCode
[System.IO.File]::WriteAllText($runLog, $stdout, (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText($errLog, $stderr, (New-Object System.Text.UTF8Encoding($false)))

$trx = Join-Path $dir $trxName
if (-not (Test-Path $trx)) {
    "TRX-MISSING exit=$code"
    "== stderr tail (first 12 lines):"
    (Get-Content $errLog -TotalCount 12) | ForEach-Object { "  " + $_ }
    exit 1
}

$xml = New-Object System.Xml.XmlDocument
$xml.Load($trx)
$ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")

$rows = $xml.SelectNodes("//t:UnitTestResult", $ns)
$map = [ordered]@{}
$grandPass = 0
$grandTotal = 0
foreach ($r in $rows) {
    $asm = $r.GetAttribute("assemblyName")
    if (-not $map.Contains($asm)) { $map[$asm] = @{ pass = 0; total = 0 } }
    $e = $map[$asm]
    $e.total++
    if ($r.GetAttribute("outcome") -eq "Passed") { $e.pass++ ; $grandPass++ }
    $grandTotal++
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("== per-assembly totals (authoritative, from single TRX, clean UTF-8):")
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
