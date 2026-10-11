param([switch]$Publish)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build GameTracker.sln -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build başarısız.' }
    dotnet run --project tests/GameTracker.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Testler başarısız.' }
    if ($Publish) {
        dotnet publish src/GameTracker.Desktop -c Release -p:PublishProfile=Portable -m:1 -o artifacts/portable-win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Publish başarısız.' }
    }
} finally { Pop-Location }
