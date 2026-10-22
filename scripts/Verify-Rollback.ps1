param(
    [string]$OldVersion = '1.0.0-rc.1',
    [string]$NewVersion = '1.0.0-rc.2'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($OldVersion -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$' -or $NewVersion -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$') { throw '请提供有效的旧版本和新版本。' }
$oldRoot = Join-Path $projectRoot ('artifacts/releases/' + $oldVersion)
$newRoot = Join-Path $projectRoot ('artifacts/releases/' + $newVersion)
$testRoot = Join-Path $projectRoot ('workspace/rollback_check_' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $testRoot '数据'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
$fixture = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'workspace/tests') -Directory -Filter 'OnlineDatabaseBackupIsAnIndependentReadableDatabase_*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $fixture) { throw '请先运行集成测试。' }
Copy-Item -LiteralPath (Join-Path $fixture.FullName 'journal.db') -Destination (Join-Path $dataRoot 'journal.db')
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
$extra.SourceSnapshot = '软件回滚验证的明确输入，不是真实用户摄入'
$original.Entries = @($original.Entries) + @($extra)
$threePath = Join-Path $testRoot '三条明确测试输入.calorie-backup.json'
$original | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $threePath -Encoding utf8
Invoke-Version $newRoot @('--data-dir',$dataRoot,'--restore-backup',$threePath,'--confirm-restore')

$rollback = Join-Path $newRoot '回滚版本.ps1'
& $rollback -TargetReleaseDirectory $oldRoot -CurrentReleaseDirectory $newRoot -DataDirectory $dataRoot -NoLaunch -Confirm:$false
Invoke-Version $oldRoot @('--data-dir',$dataRoot,'--startup-check')
$oldView = Export-Snapshot $oldRoot '退回旧候选后的记录'
if ($oldView.Entries.Count -ne 3 -or $oldView.Entries.Id -notcontains $extra.Id) { throw '回滚后新增记录未保留。' }
& $rollback -TargetReleaseDirectory $newRoot -CurrentReleaseDirectory $oldRoot -DataDirectory $dataRoot -NoLaunch -Confirm:$false
Invoke-Version $newRoot @('--data-dir',$dataRoot,'--startup-check')
$newView = Export-Snapshot $newRoot '重新启用新候选后的记录'
if ($newView.Entries.Count -ne 3 -or $newView.Entries.Id -notcontains $extra.Id) { throw '重新启用新版后记录不一致。' }

$empty = Get-Content -LiteralPath $threePath -Raw | ConvertFrom-Json
$empty.Entries = @()
$emptyPath = Join-Path $testRoot '指定旧恢复点的明确测试输入.calorie-backup.json'
$empty | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $emptyPath -Encoding utf8
& $rollback -TargetReleaseDirectory $oldRoot -CurrentReleaseDirectory $newRoot -DataDirectory $dataRoot -SnapshotPath $emptyPath -NoLaunch -Confirm:$false
$restoredOld = Export-Snapshot $oldRoot '显式恢复旧快照后的记录'
if ($restoredOld.Entries.Count -ne 0) { throw '显式旧快照恢复失败。' }
$pointer = Get-Content -LiteralPath (Join-Path $dataRoot 'active-version.json') -Raw | ConvertFrom-Json
$archived = Get-Content -LiteralPath $pointer.RecoveryBackup -Raw | ConvertFrom-Json
if ($archived.Entries.Count -ne 3 -or $archived.Entries.Id -notcontains $extra.Id) { throw '恢复旧快照前没有完整归档新记录。' }
& $rollback -TargetReleaseDirectory $newRoot -CurrentReleaseDirectory $oldRoot -DataDirectory $dataRoot -SnapshotPath $pointer.RecoveryBackup -NoLaunch -Confirm:$false
if ((Export-Snapshot $newRoot '归档重新恢复后的记录').Entries.Count -ne 3) { throw '新版归档恢复失败。' }

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
if ((Export-Snapshot $newRoot '错误拒绝后的记录').Entries.Count -ne 3) { throw '预检查失败影响了数据。' }

$result = @{From=$newVersion; To=$oldVersion; Return=$newVersion; CurrentRecordsRetained=3; CompatibleProgramRollback=$true; ExplicitSnapshotRestore=$true; NewDataArchivedAndRestored=$true; UnknownPointerRejectedBeforeChanges=$true; SchemaVersionsActuallyTested=@(1); PreviousVersionStability=(Get-Content -LiteralPath (Join-Path $oldRoot 'manifest.json') -Raw | ConvertFrom-Json).Stability; TestRoot=$testRoot}
$reportName = if ($oldVersion -eq '1.0.0-rc.1' -and $newVersion -eq '1.0.0-rc.2') { 'cross-candidate-rollback.json' } else { "cross-rollback_$($newVersion)_to_$($oldVersion).json" }
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot ('artifacts/test-results/' + $reportName)) -Encoding utf8
$result | ConvertTo-Json -Depth 5
