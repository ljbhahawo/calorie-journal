param([string]$ReleaseVersion = '1.0.0-rc.1', [string]$FixtureDatabase)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $projectRoot ('workspace/release_check_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$package = Join-Path $projectRoot ("artifacts/releases/每日热量记录_" + $ReleaseVersion + '_win-x64.zip')
$unpackedRoot = Join-Path $testRoot '中文 路径中的程序'
Expand-Archive -LiteralPath $package -DestinationPath $unpackedRoot
$manifest = Get-Content -LiteralPath (Join-Path $unpackedRoot 'manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.Files) {
    if ((Get-FileHash -LiteralPath (Join-Path $unpackedRoot $file.Path) -Algorithm SHA256).Hash -ne $file.Sha256) { throw '压缩包文件校验失败。' }
}
$executable = Join-Path $unpackedRoot 'app/CalorieJournal.exe'
$env:DOTNET_ROOT = Join-Path $testRoot '不存在的外部运行时'
function Invoke-App([string[]]$Arguments, [int]$ExpectedExitCode = 0) {
    $argumentText = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $executable -ArgumentList $argumentText -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id; throw '候选程序15秒后仍未退出。' }
    if ($process.ExitCode -ne $ExpectedExitCode) { throw "退出码$($process.ExitCode)，预期$ExpectedExitCode。检查隔离目录：$testRoot" }
}
$versionInfo = Join-Path $testRoot 'program-capabilities.json'
Invoke-App @('--version-info',$versionInfo)
$capabilities = Get-Content -LiteralPath $versionInfo -Raw | ConvertFrom-Json
if ($capabilities.Application -ne $manifest.Application -or $capabilities.Version -ne $manifest.Version -or $capabilities.SchemaVersion -ne $manifest.SchemaVersion -or $capabilities.BackupFormatVersion -ne $manifest.BackupFormatVersion -or $capabilities.MaximumBackupBytes -ne $manifest.MaximumBackupBytes) { throw '程序与清单的数据版本或备份能力不一致。' }
$startupRoot = Join-Path $testRoot '启动数据'
$watch = [Diagnostics.Stopwatch]::StartNew()
Invoke-App @('--data-dir',$startupRoot,'--startup-check')
$startupMs = $watch.Elapsed.TotalMilliseconds
$startup = Get-Content -LiteralPath (Join-Path $startupRoot 'startup-check.json') -Raw | ConvertFrom-Json
if (-not $startup.IsVisible -or $startup.FoodCount -lt 50 -or $startup.EntryCount -ne 0) { throw '发布包实际启动状态错误。' }

if (-not $FixtureDatabase) { throw '请显式指定本次真实测试库-FixtureDatabase，不从历史目录猜测输入。' }
$dataRoot = Join-Path $testRoot '恢复与回滚数据'
New-Item -ItemType Directory -Path $dataRoot | Out-Null
Copy-Item -LiteralPath $FixtureDatabase -Destination (Join-Path $dataRoot 'journal.db')
$backupPath = Join-Path $testRoot '原记录.calorie-backup.json'
Invoke-App @('--data-dir',$dataRoot,'--export-backup',$backupPath)
$snapshot = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
if ($snapshot.SchemaVersion -ne $manifest.SchemaVersion -or $snapshot.FormatVersion -ne $manifest.BackupFormatVersion) { throw '导出数据版本与程序清单不一致。' }
if ($snapshot.Entries.Count -ne 2) { throw '发布程序未正确读取真实测试数据库。' }
$emptyPath = Join-Path $testRoot '清空视图的明确测试输入.calorie-backup.json'
$snapshot.Entries = @()
$snapshot | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $emptyPath -Encoding utf8
Invoke-App @('--data-dir',$dataRoot,'--restore-backup',$emptyPath,'--confirm-restore')
$emptyCheck = Join-Path $testRoot '恢复后.calorie-backup.json'
Invoke-App @('--data-dir',$dataRoot,'--export-backup',$emptyCheck)
if ((Get-Content -LiteralPath $emptyCheck -Raw | ConvertFrom-Json).Entries.Count -ne 0) { throw '发布程序恢复结果错误。' }
$snapshot.FormatVersion = 99
$invalidPath = Join-Path $testRoot '不兼容的明确测试输入.json'
$snapshot | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $invalidPath -Encoding utf8
Invoke-App @('--data-dir',$dataRoot,'--restore-backup',$invalidPath,'--confirm-restore') 1
Invoke-App @('--data-dir',$dataRoot,'--restore-backup',$backupPath,'--confirm-restore')
& (Join-Path $unpackedRoot '回滚版本.ps1') -TargetReleaseDirectory $unpackedRoot -CurrentReleaseDirectory $unpackedRoot -DataDirectory $dataRoot -NoLaunch -Confirm:$false
$rollbackCheck = Join-Path $testRoot '版本入口验证后.calorie-backup.json'
Invoke-App @('--data-dir',$dataRoot,'--export-backup',$rollbackCheck)
if ((Get-Content -LiteralPath $rollbackCheck -Raw | ConvertFrom-Json).Entries.Count -ne 2) { throw '回滚入口不应改变兼容数据。' }
$archive = Join-Path $testRoot 'consistent-archive.db'
Invoke-App @('--data-dir',$dataRoot,'--export-database',$archive)
if (-not (Test-Path -LiteralPath $archive)) { throw '发布程序没有生成SQLite一致性归档。' }
$result = @{Version=$ReleaseVersion; StartupMilliseconds=$startupMs; StartupFoodCount=$startup.FoodCount; PackageSha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash; SelfContainedWithInvalidExternalRuntime=$true; CliBackupAndRestore=$true; InvalidBackupRejected=$true; SameVersionRollbackEntry=$true; VersionCapabilitiesMatch=$true; CliDatabaseArchive=$true; CrossVersionRollback='尚未执行'; TestRoot=$testRoot}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectRoot ("artifacts/test-results/release_" + $ReleaseVersion + '.json')) -Encoding utf8
$result | ConvertTo-Json
