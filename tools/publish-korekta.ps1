# Builds the single-file exe of the separate tool for one-off corrections of the data (BudzetDomowy.Korekta.exe).
# It is not part of the release archive of the main program: build it when it is needed.
#   .\tools\publish-korekta.ps1                          -> dist\BudzetDomowy.Korekta.exe
#   .\tools\publish-korekta.ps1 -OutDir g:\Claude\Wydatki  -> the same file in another folder
# Must stay plain ASCII (Windows PowerShell 5.1).
param(
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if ($OutDir -eq "") { $OutDir = Join-Path $root "dist" }
New-Item -ItemType Directory -Force $OutDir | Out-Null

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("korekta-publish-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null

try {
    dotnet publish (Join-Path $root "BudzetDomowy.Korekta\BudzetDomowy.Korekta.csproj") -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $work
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $target = Join-Path $OutDir "BudzetDomowy.Korekta.exe"
    Copy-Item (Join-Path $work "BudzetDomowy.Korekta.exe") $target -Force
    Write-Host "Wrote $target"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
