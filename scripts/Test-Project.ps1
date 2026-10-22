$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$env:CALORIE_TEST_ROOT = Join-Path $projectRoot 'workspace/tests'
New-Item -ItemType Directory -Path (Join-Path $projectRoot 'artifacts/test-results') -Force | Out-Null
$testLogName = 'integration_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.trx'
& (Join-Path $PSScriptRoot 'dotnet.ps1') test (Join-Path $projectRoot 'CalorieJournal.slnx') --no-restore '-m:1' '-p:UseSharedCompilation=false' --blame-hang-timeout 30s --blame-hang-dump-type none --logger "trx;LogFilePrefix=$testLogName" --results-directory (Join-Path $projectRoot 'artifacts/test-results')
if ($LASTEXITCODE -ne 0) { throw '测试失败，请查看实际输出和TRX报告。' }
& (Join-Path $PSScriptRoot 'dotnet.ps1') build (Join-Path $projectRoot 'tests/CalorieJournal.Desktop.Tests/CalorieJournal.Desktop.Tests.csproj') --no-restore '-m:1' '-p:UseSharedCompilation=false'
if ($LASTEXITCODE -ne 0) { throw 'WPF独立检查程序构建失败。' }
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$bindingExe = Join-Path $projectRoot 'tests/CalorieJournal.Desktop.Tests/bin/Debug/net10.0-windows/CalorieJournal.Desktop.Tests.exe'
$bindingProcess = Start-Process -FilePath $bindingExe -WindowStyle Hidden -PassThru
if (-not $bindingProcess.WaitForExit(30000)) { Stop-Process -Id $bindingProcess.Id; throw '独立WPF检查超过30秒，未通过。' }
Get-Content -LiteralPath (Join-Path $env:CALORIE_TEST_ROOT 'binding-check-result.txt')
if ($bindingProcess.ExitCode -ne 0) { throw '独立WPF绑定检查失败。' }
