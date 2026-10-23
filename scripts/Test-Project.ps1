param([string]$ArtifactsPath, [string]$Check = 'all')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $projectRoot "workspace/test-runs/$runId"
New-Item -ItemType Directory -Path $runRoot -ErrorAction Stop | Out-Null
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $runRoot 'build' }
$artifactArguments = @()
if ($ArtifactsPath) {
    $ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
    if (-not $ArtifactsPath.StartsWith((Join-Path $projectRoot 'workspace') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '隔离构建目录须位于本项目workspace。' }
    $artifactArguments = @('--artifacts-path',$ArtifactsPath)
}
$env:CALORIE_TEST_ROOT = Join-Path $runRoot 'tests'
New-Item -ItemType Directory -Path (Join-Path $projectRoot 'artifacts/test-results') -Force | Out-Null
$testLogName = 'integration_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.trx'
$report = [ordered]@{ RunId=$runId; Success=$false; Stage='锁定还原'; TestRoot=[IO.Path]::GetRelativePath($projectRoot,$env:CALORIE_TEST_ROOT); SourceFiles=@{}; Assemblies=@{}; Steps=@() }
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src'),(Join-Path $projectRoot 'tests'),(Join-Path $projectRoot 'scripts'),(Join-Path $projectRoot 'data') -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $report.SourceFiles[[IO.Path]::GetRelativePath($projectRoot,$_.FullName)] = (Get-FileHash -LiteralPath $_.FullName).Hash }
foreach ($configuration in @('Directory.Build.props','global.json','NuGet.Config','CalorieJournal.slnx')) { $report.SourceFiles[$configuration]=(Get-FileHash -LiteralPath (Join-Path $projectRoot $configuration)).Hash }
try {
& (Join-Path $PSScriptRoot 'dotnet.ps1') restore (Join-Path $projectRoot 'CalorieJournal.slnx') --locked-mode '-m:1' @artifactArguments
if ($LASTEXITCODE -ne 0) { throw '锁定依赖还原失败，不继续测试。' }
$report.Steps += '锁定还原'; $report.Stage='业务与SQLite'
& (Join-Path $PSScriptRoot 'dotnet.ps1') test (Join-Path $projectRoot 'CalorieJournal.slnx') --no-restore '-m:1' '-p:UseSharedCompilation=false' @artifactArguments --blame-hang-timeout 120s --blame-hang-dump-type none --logger "trx;LogFilePrefix=$testLogName" --results-directory (Join-Path $projectRoot 'artifacts/test-results')
if ($LASTEXITCODE -ne 0) { throw '测试失败，请查看实际输出和TRX报告。' }
$report.Steps += '业务与SQLite'; $report.Stage='WPF构建'
& (Join-Path $PSScriptRoot 'dotnet.ps1') build (Join-Path $projectRoot 'tests/CalorieJournal.Desktop.Tests/CalorieJournal.Desktop.Tests.csproj') --no-restore '-m:1' '-p:UseSharedCompilation=false' @artifactArguments
if ($LASTEXITCODE -ne 0) { throw 'WPF独立检查程序构建失败。' }
$report.Steps += 'WPF构建'; $report.Stage='WPF实际检查'
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$bindingExe = Join-Path $projectRoot 'tests/CalorieJournal.Desktop.Tests/bin/Debug/net10.0-windows/CalorieJournal.Desktop.Tests.exe'
if ($ArtifactsPath) {
    $bindingExecutables = @(Get-ChildItem -LiteralPath (Join-Path $ArtifactsPath 'bin/CalorieJournal.Desktop.Tests') -Recurse -File -Filter CalorieJournal.Desktop.Tests.exe)
    if ($bindingExecutables.Count -ne 1) { throw '隔离WPF检查程序路径不唯一。' }
    $bindingExe = $bindingExecutables[0].FullName
}
Get-ChildItem -LiteralPath (Split-Path $bindingExe -Parent) -File -Filter '*.dll' | Where-Object Name -Like 'CalorieJournal*' | ForEach-Object { $report.Assemblies[$_.Name]=(Get-FileHash -LiteralPath $_.FullName).Hash }
$bindingProcess = Start-Process -FilePath $bindingExe -ArgumentList @('--check',$Check) -WindowStyle Hidden -PassThru
if (-not $bindingProcess.WaitForExit(120000)) { Stop-Process -Id $bindingProcess.Id; throw '独立WPF检查超过120秒，未通过。' }
Get-Content -LiteralPath (Join-Path $env:CALORIE_TEST_ROOT 'binding-check-result.txt')
if ($bindingProcess.ExitCode -ne 0) { throw '独立WPF绑定检查失败。' }
$report.Steps += 'WPF实际检查'; $report.Success=$true; $report.Stage='完成'
foreach ($relative in $report.SourceFiles.Keys) { if ((Get-FileHash -LiteralPath (Join-Path $projectRoot $relative)).Hash -ne $report.SourceFiles[$relative]) { $report.Success=$false; throw "验证期间源文件改变：$relative" } }
}
catch { $report.Error=$_.Exception.Message; throw }
finally { $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot "artifacts/test-results/run_$runId.json") -Encoding utf8; Write-Output "本次验证目录：$runRoot" }
