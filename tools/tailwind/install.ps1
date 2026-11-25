# Downloads the Tailwind CSS standalone CLI (one binary, no Node) into
# tools\tailwind\bin\, checked against the release's published SHA-256.
# See install.sh for what it is for. Run: powershell -File tools\tailwind\install.ps1
$ErrorActionPreference = "Stop"
$Version = "4.3.3"
$Asset = "tailwindcss-windows-x64.exe"
$Sum = "e0e260ce048014e9268f6237ff18f8ccf02cef521cbd0ae04e82c2cdf7aa3955"

$bin = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Force -Path $bin | Out-Null
$tmp = Join-Path $bin ".download.exe"
Invoke-WebRequest -UseBasicParsing -OutFile $tmp -Uri "https://github.com/tailwindlabs/tailwindcss/releases/download/v$Version/$Asset"
$actual = (Get-FileHash -Algorithm SHA256 $tmp).Hash.ToLowerInvariant()
if ($actual -ne $Sum) { Remove-Item $tmp; throw "Checksum mismatch for $Asset ($actual)" }
Move-Item -Force $tmp (Join-Path $bin "tailwindcss.exe")
& (Join-Path $bin "tailwindcss.exe") --help | Select-Object -First 1
