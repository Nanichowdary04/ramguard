# Builds RamGuard with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
#   powershell -ExecutionPolicy Bypass -File build.ps1              # bin\RamGuard.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Installer   # also dist\RamGuard-Setup-x.y.z.exe
param([switch]$Installer)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

$refs = '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$ico = Join-Path $root 'assets\RamGuard.ico'
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu `
    "/out:$bin\RamGuard.exe" "/win32manifest:$root\app.manifest" "/win32icon:$ico" "/resource:$ico,RamGuard.ico" @refs @src
if ($LASTEXITCODE -ne 0) { throw 'RamGuard build failed' }

& $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$bin\rg-dummy.exe" @refs (Join-Path $root 'test\Dummy.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test helper build failed' }

"Built $bin\RamGuard.exe"

if ($Installer) {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
              "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
              "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found' }
    & $iscc /Q (Join-Path $root 'installer\RamGuard.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    Get-ChildItem (Join-Path $root 'dist') -Filter 'RamGuard-Setup-*.exe' | ForEach-Object { "Built installer $($_.FullName)" }
}
