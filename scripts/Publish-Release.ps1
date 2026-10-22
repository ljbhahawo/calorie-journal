param(
    [string]$ReleaseVersion = '1.0.0-rc.1',
    [ValidateSet('candidate','stable')][string]$Stability = 'candidate'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$') { throw '版本号格式无效。' }
if ($Stability -eq 'stable' -and $ReleaseVersion.Contains('-')) { throw '预发布版本不能标记为稳定版。' }
$releaseRoot = Join-Path $projectRoot "artifacts/releases/$ReleaseVersion"
if (Test-Path -LiteralPath $releaseRoot) { throw '该版本目录已存在，不允许覆盖。请使用新版本号。' }
$lockHashes = @{}
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src'),(Join-Path $projectRoot 'tests') -Recurse -File -Filter packages.lock.json | ForEach-Object {
    $lockHashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$desktopProject = Join-Path $projectRoot 'src/CalorieJournal.Desktop/CalorieJournal.Desktop.csproj'
& (Join-Path $PSScriptRoot 'dotnet.ps1') restore $desktopProject --locked-mode -r win-x64 '-p:SelfContained=true' '-p:Configuration=Release' '-m:1'
if ($LASTEXITCODE -ne 0) { throw '发布依赖锁定还原失败，不创建版本目录。' }
$appRoot = Join-Path $releaseRoot 'app'
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
& (Join-Path $PSScriptRoot 'dotnet.ps1') publish $desktopProject --no-restore -c Release -r win-x64 --self-contained true '-m:1' '-p:UseSharedCompilation=false' "-p:Version=$ReleaseVersion" '-p:PublishTrimmed=false' -o $appRoot
if ($LASTEXITCODE -ne 0) { throw '发布构建失败，该目录不能视为可用版本。' }
foreach ($lockPath in $lockHashes.Keys) {
    if ((Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash -ne $lockHashes[$lockPath]) { throw '发布修改了依赖锁文件，停止归档。' }
}
$licenseRoot = Join-Path $releaseRoot 'licenses'
New-Item -ItemType Directory -Path $licenseRoot | Out-Null
foreach ($noticeFile in @('LICENSE.txt','ThirdPartyNotices.txt')) {
    $noticePath = Join-Path $projectRoot ('.tools/dotnet/' + $noticeFile)
    if (Test-Path -LiteralPath $noticePath) { Copy-Item -LiteralPath $noticePath -Destination (Join-Path $licenseRoot ('dotnet_' + $noticeFile)) }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $appRoot 'CalorieJournal.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtimeConfig.runtimeOptions.includedFrameworks) { throw '发布结果没有自包含运行时声明。' }
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $runtimePackage = Join-Path $projectRoot ('.nuget/packages/' + $framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version)
    $runtimeLicenses = Join-Path $licenseRoot ($framework.name + '_' + $framework.version)
    New-Item -ItemType Directory -Path $runtimeLicenses -Force | Out-Null
    Get-ChildItem -LiteralPath $runtimePackage -File | Where-Object { $_.Name -match '(?i)license|notice|\.nuspec$' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $runtimeLicenses }
}
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/CalorieJournal.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $packageRoot = Join-Path $projectRoot ('.nuget/packages/' + $library.Name.ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $packageRoot)) { throw "依赖许可目录不存在：$($library.Name)" }
    $destinationRoot = Join-Path $licenseRoot ($library.Name.Replace('/','_'))
    New-Item -ItemType Directory -Path $destinationRoot | Out-Null
    Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '(?i)license|notice|\.nuspec$' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destinationRoot }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Rollback-Version.ps1') -Destination (Join-Path $releaseRoot '回滚版本.ps1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-Journal.ps1') -Destination (Join-Path $releaseRoot '启动软件.ps1')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/使用说明.md') -Destination (Join-Path $releaseRoot '使用说明.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/版本记录.md') -Destination (Join-Path $releaseRoot '版本记录.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/第三方说明.md') -Destination (Join-Path $releaseRoot '第三方说明.md')
$sourceRoot = Join-Path $projectRoot ('workspace/source_' + $ReleaseVersion)
if (Test-Path -LiteralPath $sourceRoot) { throw '源码暂存目录已存在，停止以保护已有文件。' }
New-Item -ItemType Directory -Path $sourceRoot | Out-Null
foreach ($sourceDirectory in @('src','tests','data','scripts','docs')) {
    Get-ChildItem -LiteralPath (Join-Path $projectRoot $sourceDirectory) -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($projectRoot, $_.FullName)
            $destination = Join-Path $sourceRoot $relative
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
}
foreach ($sourceFile in @('README.md','AGENTS.md','.gitignore','global.json','Directory.Build.props','NuGet.Config','CalorieJournal.slnx')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $sourceFile) -Destination (Join-Path $sourceRoot $sourceFile)
}
[IO.Compression.ZipFile]::CreateFromDirectory($sourceRoot, (Join-Path $releaseRoot 'source.zip'))
$files = @(Get-ChildItem -LiteralPath $releaseRoot -Recurse -File | ForEach-Object {
    @{Path=[IO.Path]::GetRelativePath($releaseRoot,$_.FullName); Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$chineseIndex = Get-Content -LiteralPath (Join-Path $projectRoot 'data/lookup/usda-zh.json') -Raw | ConvertFrom-Json
$searchAliases = Get-Content -LiteralPath (Join-Path $projectRoot 'data/search/aliases.json') -Raw | ConvertFrom-Json
@{Application='CalorieJournal'; Version=$ReleaseVersion; Stability=$Stability; SdkVersion=(Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version; SchemaVersion=1; BackupFormatVersion=1; FoodPackageVersion='usda-sr-2018-04.zh.1'; ReferenceFormatVersion=1; ChineseIndexVersion=$chineseIndex.Version; SearchAliasesVersion=$searchAliases.Version; BuiltAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); Files=$files} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releaseRoot 'manifest.json') -Encoding utf8
$packagePath = Join-Path (Split-Path $releaseRoot -Parent) ("每日热量记录_" + $ReleaseVersion + '_win-x64.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($releaseRoot, $packagePath)
Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
