param(
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$RollbackVersion,
    [ValidateSet('Status','Prepare','Publish','Verify')][string]$Mode = 'Status',
    [string]$BodyFile
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($version in @($ReleaseVersion,$RollbackVersion)) {
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '只接受已验收的稳定版本号。' }
}
if ($ReleaseVersion -eq $RollbackVersion) { throw '新版与上一稳定版必须不同。' }
$proof = Get-Content -LiteralPath (Join-Path $projectRoot "docs/版本归档/$ReleaseVersion.json") -Raw | ConvertFrom-Json
if (-not $proof.Success -or $proof.Version -ne $ReleaseVersion -or -not $proof.Checks.ArchivedSourceRebuild -or
    -not $proof.Checks.SelfContainedStartupAndBackupRestore -or -not $proof.Checks.RealDesktopOperations -or
    $proof.Checks.RollbackTo -ne $RollbackVersion -or $proof.Checks.BusinessAndRealSqliteTestsPassed -lt 1) { throw '该版交付验收不完整，不发布。' }
$stateRoot = Join-Path $projectRoot "workspace/github-release-$ReleaseVersion"
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$releaseState = Join-Path $stateRoot 'release.json'
$repoPath = '/repos/ljbhahawo/calorie-journal'
$env:GIT_TERMINAL_PROMPT = '0'
$credentialText = @('protocol=https','host=github.com','') -join "`n" | git -c credential.interactive=false credential fill 2>$null
if ($LASTEXITCODE -ne 0) { throw '本机GitHub认证不可用。' }
$credential = @{}
foreach ($line in $credentialText) { if ($line -match '^([^=]+)=(.*)$') { $credential[$matches[1]] = $matches[2] } }
if (-not $credential.password) { throw '本机GitHub认证缺少必要信息。' }
$headers = @{Authorization='Bearer '+$credential.password; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28'; 'User-Agent'='CalorieJournal-Release'}
function Api([string]$Path, [string]$Method='Get', [object]$Body=$null, [switch]$AllowNotFound) {
    try {
        $uri = 'https://api.github.com' + $Path
        if ($Method -eq 'Get') { $uri += $(if ($Path.Contains('?')) { '&' } else { '?' }) + '_request=' + [Guid]::NewGuid().ToString('N') }
        $parameters = @{Uri=$uri; Headers=$headers; Method=$Method; TimeoutSec=45}
        if ($null -ne $Body) { $parameters.ContentType='application/json; charset=utf-8'; $parameters.Body=$Body | ConvertTo-Json -Depth 8 -Compress }
        $response = Invoke-RestMethod @parameters
        foreach ($item in $response) { $item }
    }
    catch {
        if ($AllowNotFound -and $_.Exception.Response.StatusCode.value__ -eq 404) { return $null }
        throw "GitHub接口失败：$Method $Path；$($_.Exception.Message.Replace($credential.password,'[凭据已隐藏]'))"
    }
}
try {
    $repo = Api $repoPath
    if ($repo.full_name -ne 'ljbhahawo/calorie-journal' -or $repo.private -or $repo.default_branch -ne 'main') { throw '仓库身份、公开状态或默认分支不符合用户授权。' }
    $tag = 'v' + $ReleaseVersion
    $remoteTag = Api ($repoPath + '/git/ref/tags/' + $tag)
    $localTag = git -C $projectRoot rev-parse "refs/tags/$tag"
    if ($LASTEXITCODE -ne 0 -or $remoteTag.object.type -ne 'tag' -or $remoteTag.object.sha -ne $localTag) { throw '远程附注标签与本地不一致。' }
    if (Test-Path -LiteralPath $releaseState) {
        $saved = Get-Content -LiteralPath $releaseState -Raw | ConvertFrom-Json
        $release = Api ($repoPath + '/releases/' + $saved.ReleaseId)
    } else { $release = Api ($repoPath + '/releases/tags/' + $tag) -AllowNotFound }
    if (-not $release) {
        if ($Mode -ne 'Prepare') { throw 'Release尚未准备，请先使用Prepare。' }
        if (-not $BodyFile) { throw '创建Release需要已审核的说明文件。' }
        $bodyPath = [IO.Path]::GetFullPath($BodyFile)
        if (-not $bodyPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '发布说明须位于本项目目录。' }
        $body = Get-Content -LiteralPath $bodyPath -Raw
        if ([string]::IsNullOrWhiteSpace($body)) { throw '发布说明为空。' }
        $release = Api ($repoPath + '/releases') 'Post' @{tag_name=$tag; target_commitish='main'; name="每日热量记录 $ReleaseVersion"; body=$body; draft=$true; prerelease=$false; generate_release_notes=$false}
        @{ReleaseId=$release.id; Tag=$tag; CreatedByThisWorkflow=$true} | ConvertTo-Json | Set-Content -LiteralPath $releaseState -Encoding utf8
    }
    if ($release.tag_name -ne $tag) { throw 'Release版本不匹配，不操作其他版本。' }
    if ($Mode -eq 'Status') {
        @{ReleaseId=$release.id; Draft=$release.draft; Assets=@(Api ($repoPath + '/releases/' + $release.id + '/assets') | Select-Object id,name,state,size,digest)} | ConvertTo-Json -Depth 5
        return
    }
    $items = foreach ($version in @($ReleaseVersion,$RollbackVersion)) {
        $path = Join-Path $projectRoot "artifacts/releases/每日热量记录_$($version)_win-x64.zip"
        @{Path=$path; Name="calorie-journal_$($version)_win-x64.zip"; Sha=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    }
    if ($items[0].Sha -ne $proof.Package.Sha256 -or (Get-Item -LiteralPath $items[0].Path).Length -ne $proof.Package.Bytes) { throw '程序包与交付摘要不符。' }
    $previousProof = Get-Content -LiteralPath (Join-Path $projectRoot "docs/版本归档/$RollbackVersion.json") -Raw | ConvertFrom-Json
    if (-not $previousProof.Success -or $previousProof.Version -ne $RollbackVersion -or $items[1].Sha -ne $previousProof.Package.Sha256 -or
        (Get-Item -LiteralPath $items[1].Path).Length -ne $previousProof.Package.Bytes) { throw '上一稳定包与原验收摘要不符，停止发布。' }
    $checksums = @($items | ForEach-Object { $_.Sha + '  ' + $_.Name })
    $checksumPath = Join-Path $stateRoot 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $checksumPath)) { $checksums | Set-Content -LiteralPath $checksumPath -Encoding utf8NoBOM }
    if (((Get-Content -LiteralPath $checksumPath) -join "`n") -cne ($checksums -join "`n")) { throw '已有校验清单与本次程序不符。' }
    $items = @(@{Path=$checksumPath; Name='SHA256SUMS.txt'; Sha=(Get-FileHash -LiteralPath $checksumPath).Hash}) + $items
    foreach ($item in $items) {
        $file = Get-Item -LiteralPath $item.Path
        $assets = @(Api ($repoPath + '/releases/' + $release.id + '/assets'))
        $found = @($assets | Where-Object name -eq $item.Name)
        if ($found.Count -eq 0) {
            if ($Mode -ne 'Prepare' -or -not $release.draft) { throw "附件缺失，不更改正式版：$($item.Name)" }
            $uploadUrl = "https://uploads.github.com$repoPath/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($item.Name))"
            $responsePath = Join-Path $stateRoot ('upload_' + $item.Name + '.json')
            $curlConfig = @('request = "POST"', ('url = "{0}"' -f $uploadUrl), ('header = "Authorization: Bearer {0}"' -f $credential.password), 'header = "Accept: application/vnd.github+json"', 'header = "Content-Type: application/octet-stream"', 'header = "Expect:"', ('data-binary = "@{0}"' -f $file.FullName.Replace([char]92,[char]47))) -join "`n"
            Write-Output "上传$($item.Name)，$($file.Length)字节。"
            $curlConfig | curl.exe --config - --http1.1 --silent --show-error --fail-with-body --connect-timeout 20 --max-time 900 --speed-time 60 --speed-limit 1024 --write-out '上传字节:%{size_upload} 平均上传字节每秒:%{speed_upload} HTTP:%{http_code}' --output $responsePath
            $curlConfig = $null
            if ($LASTEXITCODE -ne 0) { throw "附件上传未完成：$($item.Name)，保留草稿及响应，不自动覆盖附件。" }
            $asset = Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json
        } elseif ($found.Count -eq 1) { $asset = $found[0] } else { throw '存在同名重复附件，停止。' }
        if ($asset.state -ne 'uploaded' -or $asset.size -ne $file.Length -or $asset.digest -ne ('sha256:' + $item.Sha)) { throw "附件大小或SHA256不符：$($item.Name)" }
        Write-Output "已核对$($item.Name)的远程大小及SHA256。"
    }
    if ($Mode -eq 'Publish' -and $release.draft) { $release = Api ($repoPath + '/releases/' + $release.id) 'Patch' @{draft=$false} }
    if ($Mode -eq 'Verify' -and $release.draft) { throw 'Release仍为草稿。' }
    $result = @{Repository=$repo.full_name; Private=$repo.private; ReleaseId=$release.id; Tag=$release.tag_name; Draft=$release.draft; ReleaseUrl=$release.html_url; AllThreeAssetsSizeAndSha256Verified=$true}
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stateRoot 'verified.json') -Encoding utf8
    $result | ConvertTo-Json
}
finally {
    $credential.Clear(); $headers.Clear(); $credentialText=$null; $curlConfig=$null
}
