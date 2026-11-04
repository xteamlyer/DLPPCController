$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "DLPPCController\DLPPCController.csproj"

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

Write-Host ""
Write-Host "Publish completed:"
Write-Host (Join-Path $PSScriptRoot "DLPPCController\bin\Release\net10.0-windows\win-x64\publish\DLPPCController.exe")
