$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet test ./tests/CallRecorder.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    foreach ($rid in @('win-x64', 'win-arm64')) {
        dotnet publish ./src/CallRecorder.csproj -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "./publish/$rid"
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $rid" }
    }
} finally { Pop-Location }
