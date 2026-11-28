[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot
try {
    dotnet restore RelayLink.slnx --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build src/RelayLink.Sdk/RelayLink.Sdk.csproj --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Multi-target SDK build failed.' }
    dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release --no-restore -p:SkipAdminWebBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release --no-restore -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
    pnpm --dir src/RelayLink.AdminWeb typecheck
    if ($LASTEXITCODE -ne 0) { throw 'Admin web typecheck failed.' }
    pnpm --dir src/RelayLink.AdminWeb test
    if ($LASTEXITCODE -ne 0) { throw 'Admin web tests failed.' }
    pnpm --dir src/RelayLink.AdminWeb build
    if ($LASTEXITCODE -ne 0) { throw 'Admin web build failed.' }
    pnpm --dir src/RelayLink.AdminWeb format:check
    if ($LASTEXITCODE -ne 0) { throw 'Admin web formatting check failed.' }
    pnpm --dir src/RelayLink.AgentWeb build
    if ($LASTEXITCODE -ne 0) { throw 'Agent web build failed.' }
    pnpm --dir src/RelayLink.AgentWeb test
    if ($LASTEXITCODE -ne 0) { throw 'Agent web tests failed.' }
    pnpm --dir src/RelayLink.AgentWeb format:check
    if ($LASTEXITCODE -ne 0) { throw 'Agent web formatting check failed.' }
    node scripts/build-website.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Website build failed.' }
    npm --prefix website test
    if ($LASTEXITCODE -ne 0) { throw 'Website tests failed.' }
    node --check skills/relaylink-admin/scripts/relaylink.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Skill script syntax check failed.' }
    npm --prefix sdk/typescript test
    if ($LASTEXITCODE -ne 0) { throw 'TypeScript SDK tests failed.' }

    $pythonCmd = if (Get-Command python -ErrorAction SilentlyContinue) { 'python' } elseif (Get-Command python3 -ErrorAction SilentlyContinue) { 'python3' } else { $null }
    if ($pythonCmd) {
        & $pythonCmd -m unittest discover -s sdk/python/tests -v
        if ($LASTEXITCODE -ne 0) { throw 'Python SDK tests failed.' }
    }
    if (Get-Command go -ErrorAction SilentlyContinue) {
        Push-Location sdk/go
        try {
            go vet ./...
            if ($LASTEXITCODE -ne 0) { throw 'Go SDK vet failed.' }
            go test -v ./...
            if ($LASTEXITCODE -ne 0) { throw 'Go SDK tests failed.' }
        }
        finally { Pop-Location }
    }
    if (Get-Command cargo -ErrorAction SilentlyContinue) {
        cargo test --manifest-path sdk/rust/Cargo.toml
        if ($LASTEXITCODE -ne 0) { throw 'Rust SDK tests failed.' }
    }
    if ((Get-Command javac -ErrorAction SilentlyContinue) -and (Get-Command java -ErrorAction SilentlyContinue)) {
        $jtmp = Join-Path ([System.IO.Path]::GetTempPath()) ('rl-java-sdk-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $jtmp | Out-Null
        try {
            $sources = @(Get-ChildItem sdk/java/src/main/java/com/relaylink/sdk/*.java | ForEach-Object FullName) + 'sdk/java/src/test/java/com/relaylink/sdk/ConformanceRunner.java'
            & javac -encoding UTF-8 -d $jtmp @sources
            if ($LASTEXITCODE -ne 0) { throw 'Java SDK compilation failed.' }
            & java -cp $jtmp com.relaylink.sdk.ConformanceRunner
            if ($LASTEXITCODE -ne 0) { throw 'Java SDK conformance tests failed.' }
        }
        finally { Remove-Item -Recurse -Force $jtmp -ErrorAction SilentlyContinue }
    }

    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed.' }
}
finally {
    Pop-Location
}
