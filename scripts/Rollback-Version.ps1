[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$TargetReleaseDirectory,
    [string]$CurrentReleaseDirectory = $PSScriptRoot,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'CalorieJournal'),
    [string]$SnapshotPath,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'

function Read-Release([string]$ReleaseDirectory) {
    $releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
    $manifest = Get-Content -LiteralPath (Join-Path $releaseRoot 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.Application -ne 'CalorieJournal') { throw '不是本软件的版本清单。' }
    foreach ($file in $manifest.Files) {
        $filePath = [IO.Path]::GetFullPath((Join-Path $releaseRoot $file.Path))
        if (-not $filePath.StartsWith($releaseRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '版本清单包含越界路径。' }
        if (-not (Test-Path -LiteralPath $filePath) -or (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $file.Sha256) { throw "版本文件校验失败：$($file.Path)" }
    }
    return [pscustomobject]@{Root=$releaseRoot; Manifest=$manifest; Exe=(Join-Path $releaseRoot 'app/CalorieJournal.exe')}
}

function Invoke-Maintenance([string]$Executable, [string[]]$Arguments) {
    # Start-Process的字符串参数要求对含空格的路径逐项加引号。
    foreach ($argument in $Arguments) { if ($argument.Contains('"')) { throw '参数路径不能包含双引号。' } }
    $argumentText = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $operation = Start-Process -FilePath $Executable -ArgumentList $argumentText -WindowStyle Hidden -PassThru
    if (-not $operation.WaitForExit(60000)) { throw '维护操作60秒未结束，请检查进程与诊断文件。' }
    if ($operation.ExitCode -ne 0) { throw '维护操作失败，保留现有数据。请查看数据目录中的startup-error.json。' }
}

$target = Read-Release $TargetReleaseDirectory
$current = Read-Release $CurrentReleaseDirectory
$dataRoot = [IO.Path]::GetFullPath($DataDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'journal.db'))) { throw '指定目录没有已存在的热量记录数据库。' }
$pointer = Join-Path $dataRoot 'active-version.json'
if (Test-Path -LiteralPath $pointer) {
    $existing = Get-Content -LiteralPath $pointer -Raw | ConvertFrom-Json
    if ($existing.Application -ne 'CalorieJournal') { throw '现有版本指针归属不明，已停止写入。' }
}
if ($current.Manifest.SchemaVersion -ne $target.Manifest.SchemaVersion -and -not $SnapshotPath) { throw '数据格式不兼容，必须明确选择目标旧版可读取的完整备份。' }
if ($SnapshotPath) {
    $SnapshotPath = (Resolve-Path -LiteralPath $SnapshotPath).Path
    if ((Get-Item -LiteralPath $SnapshotPath).Length -gt 52428800) { throw '备份超过50MB限制。' }
    $snapshotInfo = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json
    if ($snapshotInfo.SchemaVersion -ne $target.Manifest.SchemaVersion -or $snapshotInfo.FormatVersion -ne $target.Manifest.BackupFormatVersion) { throw '所选备份与目标版本不兼容。' }
    Write-Host "恢复点：$($snapshotInfo.ExportedAtUtc)；当前工作视图将恢复到该备份，之后的记录保留在回滚前归档中。"
    if ($current.Manifest.SchemaVersion -ge 6 -and $target.Manifest.SchemaVersion -lt 6) {
        Write-Host '目标旧版不能显示摄入目标、锻炼及恢复点后的新增资料；全部当前资料将先完整归档。返回新版可显式恢复该归档，旧版期间新增资料不自动合并。'
    }
    if ($current.Manifest.SchemaVersion -ge 7 -and $target.Manifest.SchemaVersion -lt 7) {
        Write-Host '目标旧版不能显示原始千焦和蛋白质字段，以及恢复点后新增资料；全部当前快照会先完整归档，返回新版可恢复。'
    }
}
if (-not $PSCmdlet.ShouldProcess($dataRoot, "归档当前数据并切换到版本 $($target.Manifest.Version)")) { return }

$recoveryRoot = Join-Path $dataRoot 'recovery'
New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null
$operationId = (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N')
$currentBackup = Join-Path $recoveryRoot ("回滚前_" + $operationId + '.calorie-backup.json')
Invoke-Maintenance $current.Exe @('--data-dir', $dataRoot, '--export-backup', $currentBackup)
$lock = [IO.File]::Open((Join-Path $dataRoot 'instance.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    if ($SnapshotPath) {
        $stagingRoot = Join-Path $recoveryRoot ('restore_' + $operationId)
        Invoke-Maintenance $target.Exe @('--data-dir', $stagingRoot, '--restore-backup', $SnapshotPath, '--confirm-restore')
        if ((Test-Path -LiteralPath (Join-Path $dataRoot 'journal.db-wal')) -or (Test-Path -LiteralPath (Join-Path $dataRoot 'journal.db-shm'))) { throw '发现数据库日志文件，停止直接替换。请先用匹配版本正常关闭数据连接。' }
        $oldDatabase = Join-Path $recoveryRoot ('回滚前_' + $operationId + '.db')
        [IO.File]::Replace((Join-Path $stagingRoot 'journal.db'), (Join-Path $dataRoot 'journal.db'), $oldDatabase)
    }
    $pointerStaging = Join-Path $dataRoot ('active_' + $operationId + '.writing')
    @{Application='CalorieJournal'; Version=$target.Manifest.Version; ReleaseDirectory=$target.Root; RecoveryBackup=$currentBackup} | ConvertTo-Json | Set-Content -LiteralPath $pointerStaging -Encoding utf8
    if (Test-Path -LiteralPath $pointer) { [IO.File]::Replace($pointerStaging, $pointer, (Join-Path $recoveryRoot ('active_' + $operationId + '.json'))) }
    else { [IO.File]::Move($pointerStaging, $pointer) }
}
finally { $lock.Dispose() }
Write-Host "已切换到版本 $($target.Manifest.Version)，回滚前数据归档：$currentBackup"
if (-not $NoLaunch) { Start-Process -FilePath $target.Exe -ArgumentList ('--data-dir "' + $dataRoot + '"') -WindowStyle Hidden }
