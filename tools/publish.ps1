# Builds the single-file exe and the release archive: the exe in the root, all sources in "Source".
#   .\tools\publish.ps1                          -> dist\BudzetDomowy.exe and dist\BudzetDomowy-<version>.zip
#   .\tools\publish.ps1 -OutDir g:\Claude\Wydatki  -> the same files in another folder
# Must stay plain ASCII (Windows PowerShell 5.1).
param(
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if ($OutDir -eq "") { $OutDir = Join-Path $root "dist" }
New-Item -ItemType Directory -Force $OutDir | Out-Null

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props") -Encoding UTF8
$version = "$($props.Project.PropertyGroup.Version)".Trim()
if ($version -eq "") { throw "No <Version> in Directory.Build.props" }
Write-Host "Version $version"

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("budzet-publish-" + [Guid]::NewGuid().ToString("N"))
$publish = Join-Path $work "publish"
$staging = Join-Path $work "zip"
New-Item -ItemType Directory -Force $publish, $staging | Out-Null

try {
    dotnet publish (Join-Path $root "BudzetDomowy.App\BudzetDomowy.App.csproj") -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $exe = Join-Path $publish "BudzetDomowy.App.exe"
    Copy-Item $exe (Join-Path $staging "BudzetDomowy.exe")
    Copy-Item (Join-Path $root "README.md") $staging

    $source = Join-Path $staging "Source"
    robocopy $root $source /E /XD bin obj .git .vs _sample dist TestResults /XF *.user *.dmp /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

    $zip = Join-Path $OutDir "BudzetDomowy-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip -CompressionLevel Optimal
    Copy-Item (Join-Path $staging "BudzetDomowy.exe") (Join-Path $OutDir "BudzetDomowy.exe") -Force

    Write-Host "Wrote $zip"
    Write-Host "Wrote $(Join-Path $OutDir 'BudzetDomowy.exe')"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
