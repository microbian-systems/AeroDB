# AeroDB.Sable package-consumer regression

This is deliberately a separate test project with **no `ProjectReference`** to
`AeroDB.Sable` or `AeroDB.SourceGenerators`. It models a downstream application
that can only see the locally packed `AeroDB.Sable` NuGet package.

The test defines a consumer-owned `SableDocument<long>` and verifies that the
package's source-generator analyzer asset registers a generated materializer.
`MetadataRegistry.TryMaterialize` returns `false` when that analyzer was not
present. This test was first observed failing before the packaging fix, and now
passes against a fresh local package containing the compiler extension assets.

Run this only after the parent test coordinator has serialized the build. Run
the commands from the ContractorAI repository root; this avoids AeroDB's pinned
SDK selection when that SDK is not installed locally:

```powershell
$packageOutput = Join-Path $env:TEMP ('aerodb-package-check-' + [Guid]::NewGuid().ToString('N'))
$isolatedGlobalPackages = Join-Path $packageOutput 'global-packages'
New-Item -ItemType Directory -Force -Path $packageOutput | Out-Null

dotnet pack .\AeroCMS\AeroDB\src\AeroDB.Sable\AeroDB.Sable.csproj --no-restore `
  -p:PackageOutputPath=$packageOutput

Copy-Item .\AeroCMS\AeroDB\tests\AeroDB.PackageConsumer.Tests\NuGet.fixture.config `
  (Join-Path $packageOutput 'NuGet.config')

$env:NUGET_PACKAGES = $isolatedGlobalPackages
dotnet restore .\AeroCMS\AeroDB\tests\AeroDB.PackageConsumer.Tests\AeroDB.PackageConsumer.Tests.csproj `
  --configfile (Join-Path $packageOutput 'NuGet.config') `
  --force-evaluate
dotnet build .\AeroCMS\AeroDB\tests\AeroDB.PackageConsumer.Tests\AeroDB.PackageConsumer.Tests.csproj --no-restore
dotnet exec .\AeroCMS\AeroDB\tests\AeroDB.PackageConsumer.Tests\bin\Debug\net10.0\AeroDB.PackageConsumer.Tests.dll `
  --treenode-filter "/*/*/NuGetOnlyGeneratedMaterializationTests/*" `
  --maximum-parallel-tests 1 `
  --timeout 1m `
  --no-progress `
  --disable-logo
```

The isolated `NUGET_PACKAGES` location prevents a previously restored package
with the same prerelease version from masking the just-packed artifact. Do not
point this project at source projects as a workaround; a package-only consumer
is the behavior under test.

This harness changes no production pin or release. Source mapping
requires AeroDB.Sable from the local feed, while other dependencies come from
NuGet.org. This prevents a remote package with the same version from masking the
local artifact. `NUGET_PACKAGES` should be set only in the testing shell and
restored afterward; do not persist that environment change.

Do not override `PackageVersion` globally for this pack: it also changes the
driver ProjectReference dependency versions in the generated nuspec. Isolated
package caches plus explicit source mapping enforce the local artifact instead.
