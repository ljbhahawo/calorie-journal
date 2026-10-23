param([Parameter(Mandatory)][string]$ReleaseVersion)
$ErrorActionPreference = 'Stop'
if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$') { throw '版本号格式无效。' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$runId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $projectRoot ("workspace/source_check_$($ReleaseVersion)_$runId")
$sourceRoot = Join-Path $testRoot 'source'
$releaseRoot = Join-Path $projectRoot ("artifacts/releases/$ReleaseVersion")
$reportPath = Join-Path $projectRoot ("artifacts/test-results/source_$($ReleaseVersion)_$runId.json")
$report = [ordered]@{ Version=$ReleaseVersion; Success=$false; Stage='读取归档'; Steps=@(); TestRoot=[IO.Path]::GetRelativePath($projectRoot,$testRoot) }
New-Item -ItemType Directory -Path $testRoot -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path (Split-Path $reportPath -Parent) -Force | Out-Null
$locationChanged = $false
function Invoke-SourceDotnet([string]$Step, [string[]]$CommandArguments) {
    $report.Stage = $Step
    & $dotnet @CommandArguments 2>&1 | Tee-Object -FilePath (Join-Path $testRoot ($Step + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "$Step 失败，退出码$LASTEXITCODE；原始日志已保留。" }
    $report.Steps += $Step
}
function Invoke-CheckedProcess([string]$Executable, [string[]]$ProcessArguments, [int]$TimeoutMilliseconds) {
    $argumentText = ($ProcessArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $launchOptions = @{FilePath=$Executable; WindowStyle='Hidden'; PassThru=$true}
    if ($ProcessArguments.Count -gt 0) { $launchOptions.ArgumentList = $argumentText }
    $process = Start-Process @launchOptions
    if (-not $process.WaitForExit($TimeoutMilliseconds)) { Stop-Process -Id $process.Id; throw '源码验证进程未按期退出。' }
    if ($process.ExitCode -ne 0) { throw "源码验证进程失败，退出码$($process.ExitCode)；检查隔离目录：$testRoot" }
}
try {
    $manifest = Get-Content -LiteralPath (Join-Path $releaseRoot 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.Application -ne 'CalorieJournal' -or $manifest.Version -ne $ReleaseVersion) { throw '版本清单归属或版本不匹配。' }
    $sourceZip = Join-Path $releaseRoot 'source.zip'
    $sourceEntry = @($manifest.Files | Where-Object { $_.Path -eq 'source.zip' })
    $sourceHash = (Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256).Hash
    if ($sourceEntry.Count -ne 1 -or $sourceEntry[0].Sha256 -ne $sourceHash) { throw '源码ZIP校验失败。' }
    $report.SourceZipSha256 = $sourceHash
    Expand-Archive -LiteralPath $sourceZip -DestinationPath $sourceRoot
    $locks = @{}
    Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'src'),(Join-Path $sourceRoot 'tests') -Recurse -File -Filter packages.lock.json | ForEach-Object {
        $locks[[IO.Path]::GetRelativePath($sourceRoot,$_.FullName)] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    if ($locks.Count -ne 5) { throw '源码快照应包含五个项目的依赖锁文件。' }
    $dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    $env:DOTNET_CLI_HOME = Join-Path $testRoot 'dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget/packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot '.nuget/http-cache'
    $env:CALORIE_TEST_ROOT = Join-Path $testRoot 'tests'
    Push-Location $sourceRoot; $locationChanged = $true
    $sdkVersion = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    if ((& $dotnet --version) -ne $sdkVersion) { throw '实际SDK与源码global.json不一致。' }
    $report.SdkVersion = $sdkVersion
    Invoke-SourceDotnet '普通锁定还原' @('restore','CalorieJournal.slnx','--locked-mode','-m:1')
    Invoke-SourceDotnet '源码构建' @('build','CalorieJournal.slnx','--no-restore','-m:1','-p:UseSharedCompilation=false')
    Invoke-SourceDotnet '真实SQLite测试' @('test','CalorieJournal.slnx','--no-build','--no-restore','-m:1','--blame-hang-timeout','120s','--blame-hang-dump-type','none','--logger','trx;LogFileName=source-tests.trx','--results-directory',(Join-Path $testRoot 'results'))
    $report.Stage = '独立WPF检查'
    $bindingExe = Join-Path $sourceRoot 'tests/CalorieJournal.Desktop.Tests/bin/Debug/net10.0-windows/CalorieJournal.Desktop.Tests.exe'
    Invoke-CheckedProcess $bindingExe @() 120000
    $bindingResult = Get-Content -LiteralPath (Join-Path $env:CALORIE_TEST_ROOT 'binding-check-result.txt') -Raw
    if (-not $bindingResult.StartsWith('PASS:')) { throw '独立源码的WPF检查未通过。' }
    $report.WpfChecksPassed = $true
    $report.Steps += '独立WPF检查'
    $desktopProject = 'src/CalorieJournal.Desktop/CalorieJournal.Desktop.csproj'
    Invoke-SourceDotnet '发布锁定还原' @('restore',$desktopProject,'--locked-mode','-r','win-x64','-p:SelfContained=true','-p:Configuration=Release','-m:1')
    $publishedRoot = Join-Path $testRoot 'published'
    Invoke-SourceDotnet '源码自包含发布' @('publish',$desktopProject,'--no-restore','-c','Release','-r','win-x64','--self-contained','true','-m:1','-p:UseSharedCompilation=false',"-p:Version=$ReleaseVersion",'-p:PublishTrimmed=false','-o',$publishedRoot)
    Invoke-SourceDotnet '发布后普通锁定还原' @('restore','CalorieJournal.slnx','--locked-mode','-m:1')
    Invoke-SourceDotnet '发布后普通构建' @('build','CalorieJournal.slnx','--no-restore','-m:1','-p:UseSharedCompilation=false')
    $report.Stage = '验证锁文件及重建程序'
    foreach ($relativePath in $locks.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $relativePath) -Algorithm SHA256).Hash -ne $locks[$relativePath]) { throw "验证过程修改了锁文件：$relativePath" }
    }
    $report.LockFileHashes = $locks
    $report.LockFilesUnchanged = $true
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishedRoot 'CalorieJournal.dll')).ProductVersion
    if ($productVersion -ne $ReleaseVersion) { throw '重建程序的版本与归档版本不同。' }
    $env:DOTNET_ROOT = Join-Path $testRoot '不存在的外部运行时'
    $startupData = Join-Path $testRoot 'startup-data'
    Invoke-CheckedProcess (Join-Path $publishedRoot 'CalorieJournal.exe') @('--data-dir',$startupData,'--startup-check') 15000
    $startup = Get-Content -LiteralPath (Join-Path $startupData 'startup-check.json') -Raw | ConvertFrom-Json
    if (-not $startup.IsVisible -or $startup.FoodCount -lt 50 -or $startup.EntryCount -ne 0) { throw '从源码重建的程序实际启动状态错误。' }
    $report.RebuiltAppStarted = $true
    $report.RebuiltAppVersion = $productVersion
    $report.Success = $true; $report.Stage = '完成'
}
catch { $report.Error = $_.Exception.Message; throw }
finally {
    if ($locationChanged) { Pop-Location }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "源码验证记录：$reportPath"
}
