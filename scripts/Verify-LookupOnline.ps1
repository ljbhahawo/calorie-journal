$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$env:CALORIE_TEST_ROOT = Join-Path $projectRoot ('workspace/tests/online_' + [Guid]::NewGuid().ToString('N'))
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$exe = Join-Path $projectRoot 'tests/CalorieJournal.Desktop.Tests/bin/Debug/net10.0-windows/CalorieJournal.Desktop.Tests.exe'
$process = Start-Process -FilePath $exe -ArgumentList '--lookup-online' -WindowStyle Hidden -PassThru
$watch = [Diagnostics.Stopwatch]::StartNew()
while (-not $process.WaitForExit(1000)) {
    if ($watch.Elapsed.TotalSeconds -gt 150) { Stop-Process -Id $process.Id; throw '实际联网验证超时，未通过。' }
}
Get-Content -LiteralPath (Join-Path $env:CALORIE_TEST_ROOT 'binding-check-result.txt')
if ($process.ExitCode -ne 0) { throw '实际联网检查失败。' }
Get-ChildItem -LiteralPath $env:CALORIE_TEST_ROOT -Recurse -File -Filter online-checks.json | ForEach-Object {
    Get-Content -LiteralPath $_.FullName
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $projectRoot ('artifacts/test-results/lookup-online_' + [Guid]::NewGuid().ToString('N') + '.json'))
}
