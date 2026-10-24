param(
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$PreviousVersion,
    [Parameter(Mandatory)][string]$DesktopEvidence,
    [Parameter(Mandatory)][string]$DesktopObservation
)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
foreach($version in @($ReleaseVersion,$PreviousVersion)){if($version-notmatch '^\d+\.\d+\.\d+$'){throw '稳定版本号无效。'}}
function Json([string]$path){Get-Content -LiteralPath $path -Raw|ConvertFrom-Json}
$root=Join-Path $project "artifacts/releases/$ReleaseVersion"
$manifest=Json (Join-Path $root manifest.json)
$previous=Json (Join-Path $project "artifacts/releases/$PreviousVersion/manifest.json")
if($manifest.Version-ne$ReleaseVersion-or$manifest.Stability-ne'candidate'-or$previous.Stability-ne'stable'){throw '仅允许将本次候选归档在验收后晋升，不覆盖稳定归档。'}
$reports=Join-Path $project artifacts/test-results
$sources=@(Get-ChildItem -LiteralPath $reports -Filter "source_$($ReleaseVersion)_*.json"|Where-Object{(Json $_.FullName).Success})
if($sources.Count-ne 1){throw '成功源码报告不唯一。'}
$source=Json $sources[0].FullName
if(-not$source.WpfChecksPassed-or-not$source.LockFilesUnchanged-or-not$source.RebuiltAppStarted-or$source.SourceZipSha256-ne(Get-FileHash -LiteralPath (Join-Path $root source.zip)).Hash){throw '正式源码验收不完整。'}
$release=Json (Join-Path $reports "release_$ReleaseVersion.json")
if(-not$release.SelfContainedWithInvalidExternalRuntime-or-not$release.CliBackupAndRestore-or-not$release.InvalidBackupRejected-or-not$release.VersionCapabilitiesMatch){throw '程序包验收不完整。'}
$rollbackName=if($manifest.SchemaVersion-eq$previous.SchemaVersion){"cross-rollback_$($ReleaseVersion)_to_$PreviousVersion.json"}else{"schema-rollback_$($ReleaseVersion)_to_$PreviousVersion.json"}
$rollback=Json (Join-Path $reports $rollbackName)
if(-not$rollback.AllNineCollectionsIdentical-or-not$rollback.SettingsIdentical){throw '回退尚未完整保留九类资料和设置。'}
foreach($path in @($DesktopEvidence,$DesktopObservation)){if(-not[IO.Path]::GetFullPath($path).StartsWith((Join-Path $project workspace)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '桌面证据应来自独立工作目录。'}}
$desktop=Json $DesktopEvidence;$observation=Json $DesktopObservation
if(-not$desktop.Success-or-not$desktop.FormalReadback-or-not$observation.FormalProgramObserved){throw '正式桌面与回读未完成。'}
foreach($file in $manifest.Files){if((Get-FileHash -LiteralPath (Join-Path $root $file.Path)).Hash-ne$file.Sha256){throw '验收后归档内容改变。'}}
$archive=Join-Path $project ('workspace/release-candidates/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $archive -ErrorAction Stop|Out-Null
$package=Join-Path $project "artifacts/releases/每日热量记录_$($ReleaseVersion)_win-x64.zip"
Copy-Item -LiteralPath (Join-Path $root manifest.json) -Destination (Join-Path $archive candidate-manifest.json)
Copy-Item -LiteralPath (Join-Path $reports "release_$ReleaseVersion.json") -Destination (Join-Path $archive candidate-release-verification.json)
Move-Item -LiteralPath $package -Destination (Join-Path $archive ([IO.Path]::GetFileName($package))) -ErrorAction Stop
$manifest.Stability='stable';$manifest|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $root manifest.json) -Encoding utf8
[IO.Compression.ZipFile]::CreateFromDirectory($root,$package)
@{Version=$ReleaseVersion;Stable=$true;PreviousStable=$PreviousVersion;PreservedCandidate=$archive;PackageSha256=(Get-FileHash -LiteralPath $package).Hash}|ConvertTo-Json
