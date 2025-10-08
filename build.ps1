# EVE Market Prophet Build Script
param(
    [string]$Configuration = "Release",
    [switch]$Clean,
    [switch]$Restore
)

Write-Host "=== EVE Market Prophet Build Script ===" -ForegroundColor Green
Write-Host "Configuration: $Configuration" -ForegroundColor Cyan

# Set the MSBuild path
$MSBuildPath = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\msbuild.exe"

if (-not (Test-Path $MSBuildPath)) {
    Write-Error "MSBuild not found at: $MSBuildPath"
    Write-Host "Please install Visual Studio 2022 Community or adjust the path"
    exit 1
}

Write-Host "Using MSBuild: $MSBuildPath" -ForegroundColor Gray

# Clean if requested
if ($Clean) {
    Write-Host "Cleaning previous build..." -ForegroundColor Yellow
    Remove-Item ".\EveMarketProphet\bin", ".\EveMarketProphet\obj" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Cleaned bin and obj folders" -ForegroundColor Gray
}

# Restore NuGet packages if requested
if ($Restore) {
    Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
    & $MSBuildPath ".\EveMarketProphet\EveMarketProphet.csproj" /t:Restore /v:m
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Package restore failed" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# Build the project
Write-Host "Building $Configuration configuration..." -ForegroundColor Yellow
& $MSBuildPath ".\EveMarketProphet\EveMarketProphet.csproj" /p:Configuration=$Configuration /v:m /m:1 /nr:false

$ExePath = ".\EveMarketProphet\bin\$Configuration\EveMarketProphet.exe"

if ($LASTEXITCODE -eq 0 -and (Test-Path $ExePath)) {
    Write-Host "[SUCCESS] Build completed successfully!" -ForegroundColor Green
    Write-Host "Executable: $ExePath" -ForegroundColor Cyan
    
    # Show file info
    $FileInfo = Get-Item $ExePath
    Write-Host "Size: $([math]::Round($FileInfo.Length/1MB, 2)) MB" -ForegroundColor Gray
    Write-Host "Modified: $($FileInfo.LastWriteTime)" -ForegroundColor Gray
    
    if ($Configuration -eq "Release") {
        Write-Host "[INFO] Release build with GitVersion support" -ForegroundColor Yellow
        
        # Check if ZIP was created
        $ZipPath = ".\EveMarketProphet\bin\EveMarketProphet-*.zip"
        $ZipFile = Get-ChildItem $ZipPath -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($ZipFile) {
            Write-Host "ZIP package: $($ZipFile.Name)" -ForegroundColor Green
        }
    }
} else {
    Write-Host "[ERROR] Build failed with exit code: $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}