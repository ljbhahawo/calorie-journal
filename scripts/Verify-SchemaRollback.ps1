param(
    [Parameter(Mandatory)][string]$NewVersion,
    [string]$OldVersion = '1.8.0',
    [Parameter(Mandatory)][string]$FixtureDatabase
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($version in @($NewVersion,$OldVersion)) { if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$') { throw '版本号无效。' } }
$newRoot = Join-Path $projectRoot "artifacts/releases/$NewVersion"
$oldRoot = Join-Path $projectRoot "artifacts/releases/$OldVersion"
$newManifest = Get-Content -LiteralPath (Join-Path $newRoot 'manifest.json') -Raw | ConvertFrom-Json
$oldManifest = Get-Content -LiteralPath (Join-Path $oldRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($newManifest.SchemaVersion -ne 4 -or $oldManifest.SchemaVersion -ne 3 -or $oldManifest.Stability -ne 'stable') { throw '此演练需要结构4新版和结构3旧稳定包。' }
$fixture = [IO.Path]::GetFullPath($FixtureDatabase)
if (-not $fixture.StartsWith((Join-Path $projectRoot 'workspace/tests') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '只允许workspace/tests中的明确测试库。' }
$oldReportFile = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'artifacts/test-results') -Filter "source_$($OldVersion)_*.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$oldReport = Get-Content -LiteralPath $oldReportFile.FullName -Raw | ConvertFrom-Json
if (-not $oldReport.Success) { throw '旧版源码验证未通过。' }
$oldFixture = Get-ChildItem -LiteralPath (Join-Path $projectRoot ($oldReport.TestRoot + '/tests')) -Directory -Filter 'wpf_template_*' | Select-Object -First 1
if (-not $oldFixture) { throw '缺少旧版实际测试库。' }
$testRoot = Join-Path $projectRoot ('workspace/schema_rollback_' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $testRoot 'data'
$fixtureRoot = Join-Path $testRoot 'new-fixture'
New-Item -ItemType Directory -Path $dataRoot,$fixtureRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $oldFixture.FullName 'journal.db') -Destination (Join-Path $dataRoot 'journal.db')
Copy-Item -LiteralPath $fixture -Destination (Join-Path $fixtureRoot 'journal.db')
function Invoke-App([string]$ReleaseRoot, [string[]]$Arguments) {
    $argumentText = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath (Join-Path $ReleaseRoot 'app/CalorieJournal.exe') -ArgumentList $argumentText -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000) -or $process.ExitCode -ne 0) { throw "版本操作失败：$ReleaseRoot；证据$testRoot" }
}
function Export([string]$ReleaseRoot, [string]$Directory, [string]$Name) {
    $path = Join-Path $testRoot ($Name + '.calorie-backup.json')
    Invoke-App $ReleaseRoot @('--data-dir',$Directory,'--export-backup',$path)
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
function Same-Collections($Expected,$Actual,[string[]]$Names) {
    foreach ($name in $Names) {
        if (($Expected.$name | Sort-Object Id | ConvertTo-Json -Depth 16 -Compress) -cne ($Actual.$name | Sort-Object Id | ConvertTo-Json -Depth 16 -Compress)) { throw "回滚改变了$name" }
    }
}
$old = Export $oldRoot $dataRoot 'old-restore-point'
$oldSnapshotPath = Join-Path $testRoot 'old-restore-point.calorie-backup.json'
Invoke-App $newRoot @('--data-dir',$dataRoot,'--startup-check')
$upgraded = Export $newRoot $dataRoot 'upgraded'
$newData = Export $newRoot $fixtureRoot 'new-explicit-test-inputs'
if ($newData.Settings -eq $null -or $newData.Entries.Count -lt 2) { throw '测试库缺少录入设置和摄入。' }
# 合并两组已经真实写入的明确测试输入，仅用于独立演练目录。
$merged = $upgraded
$merged.Foods = @(@($upgraded.Foods) + @($newData.Foods) | Group-Object Id | ForEach-Object { $_.Group[-1] })
$merged.Entries = @(@($upgraded.Entries) + @($newData.Entries))
$merged.Portions = @(@($upgraded.Portions) + @($newData.Portions) | Group-Object Id | ForEach-Object { $_.Group[-1] })
$merged.Templates = @(@($upgraded.Templates) + @($newData.Templates) | Group-Object Id | ForEach-Object { $_.Group[-1] })
$merged.TemplateBatches = @(@($upgraded.TemplateBatches) + @($newData.TemplateBatches) | Group-Object Id | ForEach-Object { $_.Group[-1] })
$merged.Settings = $newData.Settings
$mergedPath = Join-Path $testRoot 'merged-explicit-inputs.calorie-backup.json'
$merged | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $mergedPath -Encoding utf8
Invoke-App $newRoot @('--data-dir',$dataRoot,'--restore-backup',$mergedPath,'--confirm-restore')
$expected = Export $newRoot $dataRoot 'expected-new'
$rollback = Join-Path $newRoot '回滚版本.ps1'
$dbHash = (Get-FileHash -LiteralPath (Join-Path $dataRoot 'journal.db')).Hash
$recoveryCount = @(Get-ChildItem -LiteralPath (Join-Path $dataRoot 'recovery') -File).Count
try {
    & $rollback -TargetReleaseDirectory $oldRoot -CurrentReleaseDirectory $newRoot -DataDirectory $dataRoot -NoLaunch -Confirm:$false
    throw '应当拒绝无旧恢复点的跨结构回退。'
} catch { if ($_.Exception.Message -notlike '*必须明确选择*') { throw } }
if ((Get-FileHash -LiteralPath (Join-Path $dataRoot 'journal.db')).Hash -ne $dbHash -or @(Get-ChildItem -LiteralPath (Join-Path $dataRoot 'recovery') -File).Count -ne $recoveryCount) { throw '预检查拒绝后发生数据操作。' }

$badOld = Get-Content -LiteralPath $oldSnapshotPath -Raw | ConvertFrom-Json
$badOld.Entries = @($badOld.Entries) + @($badOld.Entries[0])
$badPath = Join-Path $testRoot 'invalid-old-snapshot.json'
$badOld | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $badPath -Encoding utf8
try {
    & $rollback -TargetReleaseDirectory $oldRoot -CurrentReleaseDirectory $newRoot -DataDirectory $dataRoot -SnapshotPath $badPath -NoLaunch -Confirm:$false
    throw '应当拒绝无效旧恢复点。'
} catch { if ($_.Exception.Message -notlike '*维护操作失败*') { throw } }
if ((Get-FileHash -LiteralPath (Join-Path $dataRoot 'journal.db')).Hash -ne $dbHash -or (Test-Path -LiteralPath (Join-Path $dataRoot 'active-version.json'))) { throw '暂存恢复失败改变了工作数据库或指针。' }

& $rollback -TargetReleaseDirectory $oldRoot -CurrentReleaseDirectory $newRoot -DataDirectory $dataRoot -SnapshotPath $oldSnapshotPath -NoLaunch -Confirm:$false
$oldView = Export $oldRoot $dataRoot 'old-view'
Same-Collections $old $oldView @('Foods','Meals','Entries','Portions','Templates','TemplateBatches')
$pointer = Get-Content -LiteralPath (Join-Path $dataRoot 'active-version.json') -Raw | ConvertFrom-Json
$newArchive = Get-Content -LiteralPath $pointer.RecoveryBackup -Raw | ConvertFrom-Json
Same-Collections $expected $newArchive @('Foods','Meals','Entries','Portions','Templates','TemplateBatches')
if(($expected.Settings|ConvertTo-Json -Compress) -cne ($newArchive.Settings|ConvertTo-Json -Compress)){throw '回滚前归档缺少当前设置'}
& $rollback -TargetReleaseDirectory $newRoot -CurrentReleaseDirectory $oldRoot -DataDirectory $dataRoot -SnapshotPath $pointer.RecoveryBackup -NoLaunch -Confirm:$false
$returned = Export $newRoot $dataRoot 'returned-new'
Same-Collections $expected $returned @('Foods','Meals','Entries','Portions','Templates','TemplateBatches')
if(($expected.Settings|ConvertTo-Json -Compress) -cne ($returned.Settings|ConvertTo-Json -Compress)){throw '返回新版后设置不一致'}
$result = @{Success=$true;From=$NewVersion;To=$OldVersion;Return=$NewVersion;ActuallyTestedSchemas=@(3,4);RequiresExplicitOldRestorePoint=$true;InvalidOldSnapshotDoesNotReplaceWorkingData=$true;OldViewMatchesChosenRestorePoint=$true;AllNewDataArchivedBeforeRollback=$true;AllNewDataRestoredOnReturn=$true;AllSixCollectionsIdentical=$true;SettingsIdentical=$true;RetainedSettings=$returned.Settings;RetainedCurrentEntries=$returned.Entries.Count;RetainedPortions=$returned.Portions.Count;RetainedTemplates=$returned.Templates.Count;RetainedTemplateBatches=$returned.TemplateBatches.Count;OldVisibleEntries=$oldView.Entries.Count;OldRestorePoint=$old.ExportedAtUtc;TestRoot=$testRoot}
$reportPath = Join-Path $projectRoot "artifacts/test-results/schema-rollback_$($NewVersion)_to_$($OldVersion).json"
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
$result | ConvertTo-Json -Depth 6
