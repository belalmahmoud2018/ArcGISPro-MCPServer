# Builds the ArcGIS Pro add-in and the single-file ArcGISProMCP-Setup.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

Write-Host '== 1/3 Building ArcGIS Pro add-in (Pro SDK 3.0, works on Pro 3.0 - 3.7) ==' -ForegroundColor Cyan
dotnet build "$root\src\ArcGISProMcp.AddIn\ArcGISProMcpBridge.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Add-in build failed' }

Write-Host '== 2/3 Publishing self-contained MCP server (embeds the add-in) ==' -ForegroundColor Cyan
dotnet publish "$root\src\ArcGISProMcp.Server\ArcGISProMcp.Server.csproj" -c Release -o "$root\src\ArcGISProMcp.Server\bin\publish" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed' }

Write-Host '== 3/3 Collecting output ==' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root\src\ArcGISProMcp.Server\bin\publish\ArcGISProMCP.exe" "$dist\ArcGISProMCP-Setup.exe" -Force
Copy-Item "$root\src\ArcGISProMcp.AddIn\bin\Release\ArcGISProMcpBridge.esriAddinX" "$dist\ArcGISProMcpBridge.esriAddinX" -Force
Get-ChildItem $dist | Format-Table Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
Write-Host "Done: $dist\ArcGISProMCP-Setup.exe" -ForegroundColor Green
