[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot
try {
    dotnet restore RelayLink.slnx --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release --no-restore -p:SkipAdminWebBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release --no-restore -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
    pnpm --dir src/RelayLink.AdminWeb test
    if ($LASTEXITCODE -ne 0) { throw 'Admin web tests failed.' }
    pnpm --dir src/RelayLink.AdminWeb format:check
    if ($LASTEXITCODE -ne 0) { throw 'Admin web formatting check failed.' }
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed.' }
}
finally {
    Pop-Location
}
