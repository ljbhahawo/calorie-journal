param([switch]$Apply)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$releaseRoot=Join-Path $projectRoot 'artifacts/releases'
$legacy=@{'1.0.0'='C288221FD328A455406B7D3743BF9224C350CAD89607E36FCC40E446BE8E1E4B';'1.1.0'='F38368D4269FC045BCD9640E681603F05191FB637F54B26540238C60F4A02D42';'1.1.1'='536556B63F95A929723F0360BEB00E9139FB3FBF1EA63C9FD87519F034D9C2D3';'1.2.0'='89F979720192B16B0D77089234B8DE52A70BA7D64C55CDC0D3DC5A0C82C96627';'1.3.0'='5FCADB3BB7E8245CEC88F5F82F08FC44923192334D357AD112CC781DEBEFD019'}
$running=@(Get-CimInstance Win32_Process -Filter "Name = 'CalorieJournal.exe'" | ForEach-Object { if(-not $_.ExecutablePath){throw '无法确认运行程序路径，停止清理。'}; $_.ExecutablePath })
$targets=[Collections.Generic.List[object]]::new(); $retained=[Collections.Generic.List[string]]::new()
function Assert-LocalPath([string]$Path){
    $cursor=Get-Item -LiteralPath $Path
    while($cursor.FullName -ne $projectRoot){
        if($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '清理路径包含链接，停止。'}
        $parent=Split-Path $cursor.FullName -Parent
        if(-not $parent -or -not $parent.StartsWith($projectRoot,[StringComparison]::OrdinalIgnoreCase)){throw '清理路径祖先越界。'}
        $cursor=Get-Item -LiteralPath $parent
    }
}
function Add-Target([string]$Path,[string]$Reason){
    if(-not(Test-Path -LiteralPath $Path)){return}
    $absolute=[IO.Path]::GetFullPath($Path)
    if($targets.Path -contains $absolute){return}
    if(-not $absolute.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $absolute -notmatch '\\(workspace|artifacts)\\'){throw '清理目标越界。'}
    $item=Get-Item -LiteralPath $absolute
    Assert-LocalPath $absolute
    $files=if($item.PSIsContainer){@(Get-ChildItem -LiteralPath $absolute -Recurse -Force -File)}else{@($item)}
    $links=@(Get-ChildItem -LiteralPath $absolute -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $links.Count -or ($files | Where-Object Name -Match '\.(db|sqlite|sqlite3)$|\.calorie-backup\.json$') -or ($running | Where-Object {$_ -eq $absolute -or $_.StartsWith($absolute+'\',[StringComparison]::OrdinalIgnoreCase)})){$retained.Add([IO.Path]::GetRelativePath($projectRoot,$absolute));return}
    $targets.Add(@{Path=$absolute;RelativePath=[IO.Path]::GetRelativePath($projectRoot,$absolute);Bytes=[long](($files | Measure-Object Length -Sum).Sum);Reason=$Reason})
}
function Verify-Payload([string]$Directory,[string]$Package){
    $zip=[IO.Compression.ZipFile]::OpenRead($Package)
    try{
        foreach($required in @('manifest.json','source.zip','app/CalorieJournal.exe','回滚版本.ps1')){if(-not $zip.GetEntry($required)){throw '完整程序包缺少必要内容。'}}
        if($zip.Entries.FullName | Where-Object {$_ -match '(?i)\.(db|sqlite|sqlite3)$|\.calorie-backup\.json$'}){throw '程序包中发现资料文件，停止清理。'}
        if(Test-Path -LiteralPath $Directory){
            foreach($file in Get-ChildItem -LiteralPath $Directory -Recurse -Force -File){
                $relative=[IO.Path]::GetRelativePath($Directory,$file.FullName).Replace('\','/')
                $entry=$zip.GetEntry($relative)
                if(-not $entry){return $false}
                $stream=$entry.Open();try{$hash=(Get-FileHash -InputStream $stream).Hash}finally{$stream.Dispose()}
                if($hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){return $false}
            }
        }
        return $true
    }finally{$zip.Dispose()}
}
$packages=@(Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object Name -Match '^每日热量记录_\d+\.\d+\.\d+_win-x64\.zip$')
$stable=@{}; foreach($package in $packages){
    $version=[regex]::Match($package.Name,'_(\d+\.\d+\.\d+)_').Groups[1].Value
    $proofPath=Join-Path $projectRoot "docs/版本归档/$version.json"
    $expected=if(Test-Path -LiteralPath $proofPath){(Get-Content -LiteralPath $proofPath -Raw | ConvertFrom-Json).Package.Sha256}else{$legacy[$version]}
    if(-not $expected -or (Get-FileHash -LiteralPath $package.FullName).Hash -ne $expected){throw "稳定包校验失败：$version"}
    $stable[$version]=$package.FullName
}
$keep=@($stable.Keys | Sort-Object {[version]$_} -Descending | Select-Object -First 2)
foreach($version in $stable.Keys){
    $directory=Join-Path $releaseRoot $version
    if($version -notin $keep -and (Test-Path -LiteralPath $directory)){
        if(Verify-Payload $directory $stable[$version]){Add-Target $directory '已有完整校验稳定ZIP的重复展开目录'}else{$retained.Add("artifacts/releases/$version（内容不同）")}
    }
}
foreach($directory in Get-ChildItem -LiteralPath $releaseRoot -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+-rc\.\d+$'){
    $version=$directory.Name -replace '-rc\.\d+$',''; $package=Join-Path $releaseRoot ('每日热量记录_'+$directory.Name+'_win-x64.zip')
    if($stable.ContainsKey($version) -and (Test-Path -LiteralPath $package) -and (Verify-Payload $directory.FullName $package)){
        Add-Target $directory.FullName '已被稳定版替代的候选展开目录'
        if(-not($running | Where-Object { $_.StartsWith($directory.FullName+'\',[StringComparison]::OrdinalIgnoreCase)})){Add-Target $package '已被稳定版替代的候选ZIP'}
    }
}
foreach($package in Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object Name -Match '^每日热量记录_\d+\.\d+\.\d+-rc\.\d+_win-x64\.zip$'){
    $candidate=[regex]::Match($package.Name,'^每日热量记录_(.+)_win-x64\.zip$').Groups[1].Value
    $version=$candidate -replace '-rc\.\d+$',''; $directory=Join-Path $releaseRoot $candidate
    if($stable.ContainsKey($version) -and -not($running | Where-Object {$_.StartsWith($directory+'\',[StringComparison]::OrdinalIgnoreCase)}) -and (Verify-Payload $directory $package.FullName)){Add-Target $package.FullName '已被稳定版替代的候选ZIP'}
}
foreach($check in Get-ChildItem -LiteralPath (Join-Path $projectRoot workspace) -Directory -Filter 'source_check_*'){
    Add-Target (Join-Path $check.FullName published) '可从保留源码和锁文件重建的发布输出'
    foreach($folder in @('source/src','source/tests')){ $root=Join-Path $check.FullName $folder; if(Test-Path -LiteralPath $root){foreach($part in Get-ChildItem -LiteralPath $root -Directory){foreach($name in @('bin','obj')){Add-Target (Join-Path $part.FullName $name) '独立源码重建的重复中间输出'}}}}
}
foreach($check in Get-ChildItem -LiteralPath (Join-Path $projectRoot workspace) -Directory -Filter 'release_check_*'){
    $payload=Join-Path $check.FullName '中文 路径中的程序'
    if(Test-Path -LiteralPath (Join-Path $payload manifest.json)){Add-Target $payload '正式程序解压验证后的重复程序副本'}
}
$report=[ordered]@{Applied=$Apply.IsPresent;StablePackagesRetained=$stable.Count;ExpandedStableVersionsRetained=$keep;Targets=@($targets);Protected=@($retained);RemovedBytes=0L}
$reportRoot=Join-Path $projectRoot 'workspace/cleanup';New-Item -ItemType Directory -Path $reportRoot -Force|Out-Null
$reportPath=Join-Path $reportRoot ('cleanup_'+[Guid]::NewGuid().ToString('N')+'.json')
$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $reportPath -Encoding utf8
if($Apply){foreach($target in $targets){
    $resolved=(Resolve-Path -LiteralPath $target.Path).Path
    if(-not $resolved.StartsWith($projectRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '删除前解析路径越界。'}
    Assert-LocalPath $resolved
    Remove-Item -LiteralPath $resolved -Recurse -Force
    $report.RemovedBytes+=$target.Bytes
    $report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $reportPath -Encoding utf8
}}
@{Applied=$report.Applied;TargetCount=$targets.Count;StablePackagesRetained=$stable.Count;RemovedBytes=$report.RemovedBytes;PlannedBytes=($targets|Measure-Object Bytes -Sum).Sum;Protected=$retained.Count;Report=$reportPath}|ConvertTo-Json
