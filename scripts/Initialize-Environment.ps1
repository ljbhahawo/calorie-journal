$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path $PSScriptRoot -Parent
$downloadRoot = Join-Path $projectRoot 'workspace/downloads'
$sdkRoot = Join-Path $projectRoot '.tools/dotnet'
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot 'dotnet.exe'))) {
    $sdkArchive = Join-Path $downloadRoot 'dotnet-sdk-10.0.402-win-x64.zip'
    if (-not (Test-Path -LiteralPath $sdkArchive)) {
        Invoke-WebRequest -Uri 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-win-x64.zip' -OutFile $sdkArchive
    }
    $expectedHash = 'ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd'
    if ((Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA512).Hash -ne $expectedHash) { throw 'SDK校验失败，停止解压。' }
    Expand-Archive -LiteralPath $sdkArchive -DestinationPath $sdkRoot
}
& (Join-Path $sdkRoot 'dotnet.exe') --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'SDK验证失败。' }
