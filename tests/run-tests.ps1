# Builds and runs the AccurateRip / offset-correction checks (tests\*.cs) against the app's own sources.
# The checksum vectors come from arver's and whipper's test suites; their sample files are downloaded into
# bin\testdata on the first run (they aren't redistributed here).
#
#   .\tests\run-tests.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $csc = & $vswhere -latest -products * -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
}
if (-not $csc) { throw "Roslyn C# compiler not found (see build.ps1)." }

$data = Join-Path $root 'bin\testdata'
New-Item -ItemType Directory -Force $data | Out-Null
$fixtures = @{
    'sample.wav' = 'https://raw.githubusercontent.com/arcctgx/arver/HEAD/tests/data/samples/sample.wav'
    'dBAR-013-00206791-01486a82-a710de0d.bin' = 'https://raw.githubusercontent.com/arcctgx/arver/HEAD/tests/data/responses/dBAR-013-00206791-01486a82-a710de0d.bin'
    'dBAR-002-0000f21c-00027ef8-05021002.bin' = 'https://raw.githubusercontent.com/whipper-team/whipper/develop/whipper/test/dBAR-002-0000f21c-00027ef8-05021002.bin'
}
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
foreach ($name in $fixtures.Keys) {
    $path = Join-Path $data $name
    if (-not (Test-Path $path)) {
        Write-Host "Downloading $name"
        try { Invoke-WebRequest -UseBasicParsing -Uri $fixtures[$name] -OutFile $path } catch { Write-Warning "Couldn't download ${name}: $_" }
    }
}

$exe = Join-Path $root 'bin\discphew-tests.exe'
$sources = @(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs) + @(Get-ChildItem $PSScriptRoot -Filter *.cs) | ForEach-Object FullName
& $csc /nologo /target:exe /langversion:latest /platform:anycpu /utf8output /codepage:65001 /debug- /optimize+ `
    /main:Discphew.Tests.TestProgram "/out:$exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    @sources
if ($LASTEXITCODE -ne 0) { throw "Test build failed." }

& $exe $data
exit $LASTEXITCODE
