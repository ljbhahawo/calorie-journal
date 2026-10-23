param(
    [string]$OldVersion = '1.6.0',
    [string]$NewExecutable,
    [string]$CheckExecutable
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($OldVersion -notmatch '^\d+\.\d+\.\d+$') { throw '旧版必须是已验收稳定版本。' }
$oldRoot = Join-Path $projectRoot "artifacts/releases/$OldVersion"
$oldManifest = Get-Content -LiteralPath (Join-Path $oldRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($oldManifest.Stability -ne 'stable' -or $oldManifest.SchemaVersion -ne 1) { throw '此检查需要旧结构1稳定包。' }
$oldExe = Join-Path $oldRoot 'app/CalorieJournal.exe'
if (-not $NewExecutable) { $NewExecutable = Join-Path $projectRoot 'src/CalorieJournal.Desktop/bin/Debug/net10.0-windows/CalorieJournal.exe' }
if (-not $CheckExecutable) { $CheckExecutable = Join-Path $projectRoot 'tests/CalorieJournal.Desktop.Tests/bin/Debug/net10.0-windows/CalorieJournal.Desktop.Tests.exe' }
foreach ($exe in @($oldExe,$NewExecutable,$CheckExecutable)) { if (-not (Test-Path -LiteralPath $exe)) { throw "缺少已构建程序：$exe" } }
$testRoot = Join-Path $projectRoot ('workspace/migration_check_' + [Guid]::NewGuid().ToString('N'))
$legacyRoot = Join-Path $testRoot 'legacy'
New-Item -ItemType Directory -Path $legacyRoot | Out-Null
$oldSourceReport = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'artifacts/test-results') -Filter "source_$($OldVersion)_*.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $oldSourceReport) { throw '请先对旧稳定包执行Verify-Source，产生实际旧版测试库。' }
$oldSource = Get-Content -LiteralPath $oldSourceReport.FullName -Raw | ConvertFrom-Json
if (-not $oldSource.Success -or $oldSource.Version -ne $OldVersion) { throw '旧版源码验证未通过。' }
$oldFixture = Get-ChildItem -LiteralPath (Join-Path $projectRoot ($oldSource.TestRoot + '/tests')) -Directory -Filter 'wpf_lookup_*' | Select-Object -First 1
if (-not $oldFixture) { throw '缺少已验证1.6源码产生的明确WPF测试库。' }
Copy-Item -LiteralPath (Join-Path $oldFixture.FullName 'journal.db') -Destination (Join-Path $legacyRoot 'journal.db')
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$env:CALORIE_TEST_ROOT = $testRoot
function Invoke-App([string]$Executable, [string[]]$Arguments, [int]$Expected=0) {
    $argumentText = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Executable -ArgumentList $argumentText -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { throw '迁移检查进程超时。' }
    if ($process.ExitCode -ne $Expected) { throw "迁移检查进程退出码$($process.ExitCode)，预期$Expected；证据保留在$testRoot" }
}
$unmodifiedBackup = Join-Path $testRoot 'original-legacy.calorie-backup.json'
Invoke-App $oldExe @('--data-dir',$legacyRoot,'--export-backup',$unmodifiedBackup)
$explicit = Get-Content -LiteralPath $unmodifiedBackup -Raw | ConvertFrom-Json
$changedFood = $explicit.Foods | Where-Object Id -eq $explicit.Entries[0].FoodId
$changedFood.Name = '迁移验收修改 ' + $changedFood.Name
$changedFood.KcalPer100gHundredths += 100
$changedFood.UserModified = $true
$deletedFood = $explicit.Foods | Where-Object { $_.Id -notin $explicit.Entries.FoodId } | Select-Object -First 1
$deletedFood.IsDeleted = $true
$deletedFood.UserModified = $true
$explicitPath = Join-Path $testRoot 'explicit-food-state.calorie-backup.json'
$explicit | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $explicitPath -Encoding utf8
Invoke-App $oldExe @('--data-dir',$legacyRoot,'--restore-backup',$explicitPath,'--confirm-restore')
$legacyBackup = Join-Path $testRoot 'actual-legacy.calorie-backup.json'
Invoke-App $oldExe @('--data-dir',$legacyRoot,'--export-backup',$legacyBackup)
$oldSnapshot = Get-Content -LiteralPath $legacyBackup -Raw | ConvertFrom-Json
if ($oldSnapshot.SchemaVersion -ne 1 -or $oldSnapshot.Entries.Count -lt 2) { throw '旧程序测试数据无效。' }
Invoke-App $CheckExecutable @('--migration-check',(Join-Path $legacyRoot 'journal.db'))
$component = Get-Content -LiteralPath (Join-Path $testRoot 'migration-component.json') -Raw | ConvertFrom-Json
if (-not $component.Success) { throw '迁移组件检查未通过。' }
$healthyRoot = $component.HealthyDataDirectory
$databaseHash = (Get-FileHash -LiteralPath (Join-Path $healthyRoot 'journal.db')).Hash
Invoke-App $oldExe @('--data-dir',$healthyRoot,'--startup-check') 1
if ((Get-FileHash -LiteralPath (Join-Path $healthyRoot 'journal.db')).Hash -ne $databaseHash) { throw '旧版拒绝后数据库改变。' }
$recoveryOld = Join-Path $testRoot 'old-reads-backup'
New-Item -ItemType Directory -Path $recoveryOld | Out-Null
Copy-Item -LiteralPath $component.UpgradeDatabase -Destination (Join-Path $recoveryOld 'journal.db')
Invoke-App $oldExe @('--data-dir',$recoveryOld,'--export-backup',(Join-Path $testRoot 'old-reads-upgrade-db.calorie-backup.json'))
Invoke-App $oldExe @('--data-dir',(Join-Path $testRoot 'old-restores-upgrade-json'),'--restore-backup',$component.UpgradeBackup,'--confirm-restore')
$appRoot = Join-Path $testRoot 'app-upgrade'
New-Item -ItemType Directory -Path $appRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $legacyRoot 'journal.db') -Destination (Join-Path $appRoot 'journal.db')
Invoke-App $NewExecutable @('--data-dir',$appRoot,'--startup-check')
$afterPath = Join-Path $testRoot 'upgraded.calorie-backup.json'
Invoke-App $NewExecutable @('--data-dir',$appRoot,'--export-backup',$afterPath)
$after = Get-Content -LiteralPath $afterPath -Raw | ConvertFrom-Json
if ($after.SchemaVersion -ne 2 -or $after.FormatVersion -ne 2 -or $after.Portions.Count -ne 0) { throw '新程序迁移版本不符。' }
foreach ($collection in @('Foods','Meals')) {
    if (($oldSnapshot.$collection | ConvertTo-Json -Depth 12 -Compress) -cne ($after.$collection | ConvertTo-Json -Depth 12 -Compress)) { throw "迁移改变$collection" }
}
foreach ($entry in $oldSnapshot.Entries) {
    $actual = $after.Entries | Where-Object Id -eq $entry.Id
    foreach ($property in $entry.PSObject.Properties) { if ($property.Value -cne $actual.($property.Name)) { throw '迁移改变原摄入字段。' } }
    if ($null -ne $actual.WeightCalculation) { throw '旧条目被填入推测换算。' }
}
$result = @{Success=$true;TestRoot=$testRoot;OldVersion=$OldVersion;ActuallyTestedSchemas=@(1,2);ActualOldProgramExport=$true;ActualNewProgramMigration=$true;UserModifiedAndDeletedFoodsRetained=$true;OldProgramRefusesNewSchemaWithoutChangingBytes=$true;OldProgramReadsUpgradeDatabase=$true;OldProgramRestoresUpgradeBackup=$true;OriginalFoodMealEntryFieldsRetained=$true;BackupFailurePreservesBytes=$true;RealSqliteFailureRollsBackSchemaAndData=$true}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding utf8
$result | ConvertTo-Json -Depth 5
