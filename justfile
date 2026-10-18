default:
    @just --list

build:
    dotnet build CortadoPlugin.sln -c Release

format-check:
    dotnet format CortadoPlugin.sln --verify-no-changes

format:
    dotnet format CortadoPlugin.sln

test:
    dotnet test CortadoPlugin.sln -c Release

test-core:
    dotnet test tests/Cortado.Core.Tests/Cortado.Core.Tests.csproj -c Release

release $tag: test
    gh release create --verify-tag --draft --generate-notes "$tag"
