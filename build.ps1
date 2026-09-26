$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$refs = Join-Path $root '.build\refasm48'

if (-not (Test-Path (Join-Path $refs 'build\.NETFramework\v4.8'))) {
    New-Item -ItemType Directory -Force (Join-Path $root '.build') | Out-Null
    $zip = Join-Path $root '.build\refasm48.zip'
    Invoke-WebRequest -Uri 'https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3' -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath $refs -Force
    Remove-Item $zip
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found (install Visual Studio Build Tools).' }

& $msbuild (Join-Path $root 'src\PcControl.csproj') /nologo /v:m /restore:false "/p:TargetFrameworkRootPath=$refs\build"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $(Join-Path $root 'bin\PcControl.exe')"
