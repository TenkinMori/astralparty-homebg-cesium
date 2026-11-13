$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

dotnet build "$root\src\HomeBackgroundMod" -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败" }

$out = Join-Path $root 'dist\HomeBackgroundMod'
New-Item -ItemType Directory -Force "$out\assets" | Out-Null
Copy-Item "$root\src\HomeBackgroundMod\bin\Release\netstandard2.0\HomeBackgroundMod.dll" $out -Force
Copy-Item "$root\src\HomeBackgroundMod\HomeBackgroundMod.json" $out -Force
Copy-Item "$root\src\HomeBackgroundMod\icon.png" $out -Force
Copy-Item "$root\src\HomeBackgroundMod\assets\bg.png" "$out\assets\" -Force
Write-Host "OK -> $out"
Get-ChildItem $out -Recurse -File | Select-Object FullName, Length | Format-Table -AutoSize
