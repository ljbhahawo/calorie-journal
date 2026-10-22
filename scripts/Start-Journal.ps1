param([string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'CalorieJournal'))
$ErrorActionPreference = 'Stop'
$releaseRoot = $PSScriptRoot
$pointerPath = Join-Path $DataDirectory 'active-version.json'
if (Test-Path -LiteralPath $pointerPath) {
    $pointer = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
    if ($pointer.Application -ne 'CalorieJournal') { throw '版本指针归属不明。' }
    $releaseRoot = $pointer.ReleaseDirectory
}
$executable = Join-Path $releaseRoot 'app/CalorieJournal.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw '程序文件不存在，请重新解压完整程序包。' }
if ($DataDirectory.Contains('"')) { throw '数据目录不能包含双引号。' }
Start-Process -FilePath $executable -ArgumentList ('--data-dir "' + $DataDirectory + '"') -WindowStyle Hidden
