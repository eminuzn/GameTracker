$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build src/GameTracker.CoverService -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build başarısız.' }
    dotnet run --project src/GameTracker.CoverService -c Release --no-build --urls http://localhost:5188
} finally { Pop-Location }
