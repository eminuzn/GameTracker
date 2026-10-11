$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build src/GameTracker.Desktop -c Debug -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build başarısız.' }
    dotnet run --project src/GameTracker.Desktop --no-build
} finally { Pop-Location }
