param(
    [string]$OldVersion = '1.11.0',
    [string]$NewExecutable,
    [string]$CheckExecutable
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($OldVersion -notmatch '^\d+\.\d+\.\d+$') { throw '旧版必须是已验收稳定版本。' }
$oldRoot = Join-Path $projectRoot "artifacts/releases/$OldVersion"
$oldManifest = Get-Content -LiteralPath (Join-Path $oldRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($oldManifest.Stability -ne 'stable' -or $oldManifest.SchemaVersion -notin @(1,2,3,4,5,6)) { throw '此检查需要旧结构1至6稳定包。' }
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
$fixtureFilter = if ($oldManifest.SchemaVersion -ge 6) { 'wpf_personal_*' } elseif ($oldManifest.SchemaVersion -ge 5) { 'wpf_statistics_*' } elseif ($oldManifest.SchemaVersion -ge 3) { 'wpf_template_*' } elseif ($oldManifest.SchemaVersion -eq 2) { 'wpf_weight_*' } else { 'wpf_lookup_*' }
$oldFixture = Get-ChildItem -LiteralPath (Join-Path $projectRoot ($oldSource.TestRoot + '/tests')) -Directory -Filter $fixtureFilter | Select-Object -First 1
if (-not $oldFixture) { throw '缺少已验证旧版源码产生的明确WPF测试库。' }
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
$referencedEntry = $explicit.Entries | Where-Object { $_.FoodId } | Select-Object -First 1
if (-not $referencedEntry) { throw '旧版测试库须包含真实引用食物的摄入。' }
$changedFood = $explicit.Foods | Where-Object Id -eq $referencedEntry.FoodId
if (-not $changedFood) { throw '旧版摄入引用食物缺失。' }
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
if ($oldSnapshot.SchemaVersion -ne $oldManifest.SchemaVersion -or $oldSnapshot.Entries.Count -lt 2) { throw '旧程序测试数据无效。' }
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
if ($after.SchemaVersion -ne 7 -or $after.FormatVersion -ne 7) { throw '新程序迁移版本不符。' }
if ($oldManifest.SchemaVersion -lt 6 -and ($after.IntakeGoals.Count -ne 0 -or $after.Exercises.Count -ne 0)) { throw '旧结构目标与锻炼应为空。' }
if ($oldManifest.SchemaVersion -ge 6) { foreach ($collection in @('IntakeGoals','Exercises')) { if (($oldSnapshot.$collection | ConvertTo-Json -Depth 20 -Compress) -cne ($after.$collection | ConvertTo-Json -Depth 20 -Compress)) { throw "迁移改变$collection" } } }
if ((@($oldSnapshot.BodyMeasurements) | ConvertTo-Json -Depth 16 -Compress) -cne (@($after.BodyMeasurements) | ConvertTo-Json -Depth 16 -Compress) -and $oldManifest.SchemaVersion -ge 5) { throw '迁移改变已有身体记录。' }
if ($oldManifest.SchemaVersion -lt 5 -and $after.BodyMeasurements.Count -ne 0) { throw '旧结构迁移后身体记录应为空。' }
if ($oldManifest.SchemaVersion -ge 4) { if (($oldSnapshot.Settings | ConvertTo-Json -Compress) -cne ($after.Settings | ConvertTo-Json -Compress)) { throw '迁移改变已有设置。' } }
elseif ($after.Settings.NewFoodMode -ne 'ask' -or -not $after.Settings.DefaultWeightMode) { throw '旧格式迁移默认设置不符。' }
$proteinData = (Get-Content -LiteralPath (Join-Path $projectRoot 'data/lookup/protein.json') -Raw | ConvertFrom-Json).Catalogs | Where-Object Provider -eq 'USDA SR Legacy'
foreach ($food in $oldSnapshot.Foods) {
    $actualFood = $after.Foods | Where-Object Id -eq $food.Id
    $protein = $proteinData.Foods | Where-Object SourceId -eq $food.SourceId
    if ($food.UserModified -or $food.IsDeleted) { if ($null -ne $actualFood.ProteinPer100gHundredths) { throw '迁移覆盖已修改或已删除食物的蛋白质' } }
    elseif ($food.BuiltinKey -and $protein) { if ($actualFood.ProteinPer100gHundredths -ne $protein.ProteinPer100gHundredths) { throw '未修改内置项蛋白质补充不符' } }
    $legacyFood = $actualFood | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $legacyFood.PSObject.Properties.Remove('ProteinPer100gHundredths'); $legacyFood.PSObject.Properties.Remove('KilojoulesPer100gHundredths')
    if (($food | ConvertTo-Json -Depth 12 -Compress) -cne ($legacyFood | ConvertTo-Json -Depth 12 -Compress)) { throw '迁移改变食物原有字段' }
}
if (($oldSnapshot.Meals | ConvertTo-Json -Depth 12 -Compress) -cne ($after.Meals | ConvertTo-Json -Depth 12 -Compress)) { throw '迁移改变餐次' }
foreach ($entry in $oldSnapshot.Entries) {
    $actual = $after.Entries | Where-Object Id -eq $entry.Id
    if (($entry | ConvertTo-Json -Depth 16 -Compress) -cne ($actual | ConvertTo-Json -Depth 16 -Compress)) { throw '迁移改变原摄入字段或换算依据。' }
}
if ((@($oldSnapshot.Portions) | ConvertTo-Json -Depth 16 -Compress) -cne (@($after.Portions) | ConvertTo-Json -Depth 16 -Compress) -and $oldManifest.SchemaVersion -ge 2) { throw '迁移改变旧份量。' }
if($oldManifest.SchemaVersion -ge 3){foreach($collection in @('Templates','TemplateBatches')){if(($oldSnapshot.$collection|ConvertTo-Json -Depth 20 -Compress) -cne ($after.$collection|ConvertTo-Json -Depth 20 -Compress)){throw "迁移改变$collection"}}}
$result = @{Success=$true;TestRoot=$testRoot;OldVersion=$OldVersion;ActuallyTestedSchemas=@($oldManifest.SchemaVersion,7);ActualOldProgramExport=$true;ActualNewProgramMigration=$true;UserModifiedAndDeletedFoodsRetained=$true;OldProgramRefusesNewSchemaWithoutChangingBytes=$true;OldProgramReadsUpgradeDatabase=$true;OldProgramRestoresUpgradeBackup=$true;OriginalFoodMealEntryFieldsRetained=$true;ExistingBodyGoalsAndExercisesRetained=$true;HistoricalNutritionRemainsUnknown=$true;OnlyUnmodifiedBuiltinProteinEnriched=$true;BackupFailurePreservesBytes=$true;RealSqliteFailureRollsBackSchemaAndData=$true}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding utf8
$result | ConvertTo-Json -Depth 5
