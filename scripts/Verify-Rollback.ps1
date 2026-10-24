param(
    [string]$OldVersion = '1.9.0',
    [string]$NewVersion = '1.10.0',
    [string]$FixtureDatabase
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($OldVersion -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$' -or $NewVersion -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$') { throw '请提供有效的旧版本和新版本。' }
$oldRoot = Join-Path $projectRoot ('artifacts/releases/' + $oldVersion)
$newRoot = Join-Path $projectRoot ('artifacts/releases/' + $newVersion)
$testRoot = Join-Path $projectRoot ('workspace/rollback_check_' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $testRoot '数据'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
if ($FixtureDatabase) {
    $fixturePath = [IO.Path]::GetFullPath($FixtureDatabase)
    $allowedFixtureRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'workspace/tests')) + [IO.Path]::DirectorySeparatorChar
    if (-not $fixturePath.StartsWith($allowedFixtureRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '只允许使用workspace/tests中的明确测试数据库。' }
} else {
    $fixture = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'workspace/tests') -Directory -Filter 'OnlineDatabaseBackupIsAnIndependentReadableDatabase_*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $fixture) { throw '请先运行集成测试。' }
    $fixturePath = Join-Path $fixture.FullName 'journal.db'
}
Copy-Item -LiteralPath $fixturePath -Destination (Join-Path $dataRoot 'journal.db')
function Invoke-Version([string]$ReleaseRoot, [string[]]$Arguments) {
    $argumentText = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath (Join-Path $ReleaseRoot 'app/CalorieJournal.exe') -ArgumentList $argumentText -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id; throw '版本验证进程未按期退出。' }
    if ($process.ExitCode -ne 0) { throw "版本验证失败，退出码$($process.ExitCode)" }
}
function Export-Snapshot([string]$ReleaseRoot, [string]$Name) {
    $path = Join-Path $testRoot ($Name + '.calorie-backup.json')
    Invoke-Version $ReleaseRoot @('--data-dir',$dataRoot,'--export-backup',$path)
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
$original = Export-Snapshot $newRoot '新候选初始记录'
$extra = $original.Entries[0].PSObject.Copy()
$extra.Id = [Guid]::NewGuid().ToString()
$extra.FoodId = $null; $extra.FoodNameSnapshot = '跨候选版回滚的明确测试输入'; $extra.InputMode = 'direct'
$extra.WeightGramsTenths = $null; $extra.KcalPer100gHundredthsSnapshot = $null; $extra.EnergyKcalTenths = 1234
$extra.PSObject.Properties.Remove('WeightCalculation')
$extra.PSObject.Properties.Remove('KilojoulesHundredthsSnapshot')
$extra.PSObject.Properties.Remove('ProteinPer100gHundredthsSnapshot')
$extra.PSObject.Properties.Remove('ProteinGramsHundredths')
$extra.SourceSnapshot = '软件回滚验证的明确输入，不是真实用户摄入'
$original.Entries = @($original.Entries) + @($extra)
$expectedEntries = @($original.Entries)
$expectedCount = $expectedEntries.Count
function Assert-Entries($Snapshot) {
    foreach($name in @('Foods','Meals','Portions','Templates','TemplateBatches','BodyMeasurements','IntakeGoals','Exercises')){
        if(($Snapshot.$name | Sort-Object Id | ConvertTo-Json -Depth 20 -Compress) -cne ($original.$name | Sort-Object Id | ConvertTo-Json -Depth 20 -Compress)){throw "回滚改变了$name"}
    }
    if(($Snapshot.Settings | ConvertTo-Json -Compress) -cne ($original.Settings | ConvertTo-Json -Compress)){throw '回滚改变了录入设置。'}
    if ($Snapshot.Entries.Count -ne $expectedCount) { throw '回滚前后记录数量不同。' }
    foreach ($expectedEntry in $expectedEntries) {
        $actualEntry = $Snapshot.Entries | Where-Object { $_.Id -eq $expectedEntry.Id }
        if (-not $actualEntry -or ($actualEntry | ConvertTo-Json -Depth 6 -Compress) -cne ($expectedEntry | ConvertTo-Json -Depth 6 -Compress)) { throw '回滚前后某条记录或保存时的快照不同。' }
    }
}
$threePath = Join-Path $testRoot '三条明确测试输入.calorie-backup.json'
$original | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $threePath -Encoding utf8
Invoke-Version $newRoot @('--data-dir',$dataRoot,'--restore-backup',$threePath,'--confirm-restore')

$rollback = Join-Path $newRoot '回滚版本.ps1'
& $rollback -TargetReleaseDirectory $oldRoot -CurrentReleaseDirectory $newRoot -DataDirectory $dataRoot -NoLaunch -Confirm:$false
Invoke-Version $oldRoot @('--data-dir',$dataRoot,'--startup-check')
$oldView = Export-Snapshot $oldRoot '退回旧候选后的记录'
Assert-Entries $oldView
& $rollback -TargetReleaseDirectory $newRoot -CurrentReleaseDirectory $oldRoot -DataDirectory $dataRoot -NoLaunch -Confirm:$false
Invoke-Version $newRoot @('--data-dir',$dataRoot,'--startup-check')
$newView = Export-Snapshot $newRoot '重新启用新候选后的记录'
Assert-Entries $newView

$empty = Get-Content -LiteralPath $threePath -Raw | ConvertFrom-Json
$empty.Entries = @()
$emptyPath = Join-Path $testRoot '指定旧恢复点的明确测试输入.calorie-backup.json'
$empty | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $emptyPath -Encoding utf8
& $rollback -TargetReleaseDirectory $oldRoot -CurrentReleaseDirectory $newRoot -DataDirectory $dataRoot -SnapshotPath $emptyPath -NoLaunch -Confirm:$false
$restoredOld = Export-Snapshot $oldRoot '显式恢复旧快照后的记录'
if ($restoredOld.Entries.Count -ne 0) { throw '显式旧快照恢复失败。' }
$pointer = Get-Content -LiteralPath (Join-Path $dataRoot 'active-version.json') -Raw | ConvertFrom-Json
$archived = Get-Content -LiteralPath $pointer.RecoveryBackup -Raw | ConvertFrom-Json
Assert-Entries $archived
& $rollback -TargetReleaseDirectory $newRoot -CurrentReleaseDirectory $oldRoot -DataDirectory $dataRoot -SnapshotPath $pointer.RecoveryBackup -NoLaunch -Confirm:$false
Assert-Entries (Export-Snapshot $newRoot '归档重新恢复后的记录')

$pointerPath = Join-Path $dataRoot 'active-version.json'
$safePointer = Get-Content -LiteralPath $pointerPath -Raw
$beforeCount = @(Get-ChildItem -LiteralPath (Join-Path $dataRoot 'recovery') -File).Count
@{Application='UnknownApplication'} | ConvertTo-Json | Set-Content -LiteralPath $pointerPath -Encoding utf8
try {
    & $rollback -TargetReleaseDirectory $oldRoot -CurrentReleaseDirectory $newRoot -DataDirectory $dataRoot -SnapshotPath $emptyPath -NoLaunch -Confirm:$false
    throw '应当拒绝归属不明的版本指针。'
}
catch { if ($_.Exception.Message -notlike '*归属不明*') { throw } }
if (@(Get-ChildItem -LiteralPath (Join-Path $dataRoot 'recovery') -File).Count -ne $beforeCount) { throw '预检查失败后不应开始数据操作。' }
$safePointer | Set-Content -LiteralPath $pointerPath -Encoding utf8
Assert-Entries (Export-Snapshot $newRoot '错误拒绝后的记录')

$result = @{From=$newVersion; To=$oldVersion; Return=$newVersion; CurrentRecordsRetained=$expectedCount; EntrySnapshotsIdentical=$true; AllSevenCollectionsIdentical=$true; SettingsIdentical=$true; BodyMeasurementsRetained=$original.BodyMeasurements.Count; PortionsRetained=$original.Portions.Count; TemplatesRetained=$original.Templates.Count; BatchesRetained=$original.TemplateBatches.Count; CompatibleProgramRollback=$true; ExplicitSnapshotRestore=$true; NewDataArchivedAndRestored=$true; UnknownPointerRejectedBeforeChanges=$true; SchemaVersionsActuallyTested=@($original.SchemaVersion); PreviousVersionStability=(Get-Content -LiteralPath (Join-Path $oldRoot 'manifest.json') -Raw | ConvertFrom-Json).Stability; TestRoot=$testRoot}
$result.AllNineCollectionsIdentical = $true
$result.IntakeGoalsRetained = @($original.IntakeGoals).Count
$result.ExercisesRetained = @($original.Exercises).Count
$reportName = if ($oldVersion -eq '1.0.0-rc.1' -and $newVersion -eq '1.0.0-rc.2') { 'cross-candidate-rollback.json' } else { "cross-rollback_$($newVersion)_to_$($oldVersion).json" }
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot ('artifacts/test-results/' + $reportName)) -Encoding utf8
$result | ConvertTo-Json -Depth 5
