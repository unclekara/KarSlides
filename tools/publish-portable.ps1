# Builds the portable KarSlides for Windows x64: self-contained (.NET, ASP.NET Core and Windows Desktop
# runtimes inside), no installer. Output: artifacts\KarSlides-<version>-win-x64\ and a .zip next to it.
# Not trimmed and not single-file: WinForms and late-bound COM do not survive trimming, and appsettings.json
# and web\ must sit next to the exe anyway.
param(
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\KarSlides.App\KarSlides.App.csproj"
# The one version of the product.
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "KarSlides-$version-win-x64"
$out = Join-Path $root "artifacts\$name"
$zip = Join-Path $root "artifacts\$name.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root "docs\PORTABLE.md") (Join-Path $out "README.md")
Copy-Item (Join-Path $root "CHANGELOG.md") (Join-Path $out "CHANGELOG.md")
# License terms travel with the binaries: MIT, third-party notices with the NDI and Microsoft terms, Apache-2.0 text.
Copy-Item (Join-Path $root "LICENSE") (Join-Path $out "LICENSE.txt")
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") (Join-Path $out "THIRD-PARTY-NOTICES.md")
Copy-Item (Join-Path $root "licenses") (Join-Path $out "licenses") -Recurse
Compress-Archive -Path "$out\*" -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
"Portable: $out"
"Zip:      $zip ($size MB)"
