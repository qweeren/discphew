# Builds bin\discphew.exe with the Roslyn C# compiler against .NET Framework 4.8,
# which ships with Windows 10 and 11. No NuGet packages, no SDK required.
#
#   .\build.ps1          build
#   .\build.ps1 -Run     build and launch
#   .\build.ps1 -Debug   build with debug symbols, no optimizations

param([switch]$Run, [switch]$Debug)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $csc = & $vswhere -latest -products * -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
}
if (-not $csc) {
    throw "Roslyn C# compiler not found. Install the free 'Build Tools for Visual Studio' (MSBuild workload) or Visual Studio, then re-run."
}

$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$sources = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object FullName

$cscArgs = @(
    '/nologo', '/target:winexe', '/langversion:latest', '/platform:anycpu', '/utf8output', '/codepage:65001',
    "/out:$out\discphew.exe",
    "/win32icon:$root\assets\discphew.ico",
    "/win32manifest:$root\src\app.manifest",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Web.Extensions.dll'
)
if ($Debug) { $cscArgs += @('/debug:full', '/optimize-', '/define:DEBUG') } else { $cscArgs += @('/debug-', '/optimize+') }
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$exe = Get-Item "$out\discphew.exe"
Write-Host ("Built {0} ({1:N0} KB)" -f $exe.FullName, ($exe.Length / 1KB))
if ($Run) { Start-Process $exe.FullName }
