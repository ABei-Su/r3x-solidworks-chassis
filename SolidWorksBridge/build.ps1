param(
    [Parameter(Mandatory = $true)]
    [string]$InteropDirectory
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$interopRoot = (Resolve-Path -LiteralPath $InteropDirectory).Path
$sldworks = Join-Path $interopRoot 'SolidWorks.Interop.sldworks.dll'
$swconst = Join-Path $interopRoot 'SolidWorks.Interop.swconst.dll'
foreach ($required in @($compiler, $sldworks, $swconst)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Missing build dependency: $required"
    }
}
$outputDirectory = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$source = Join-Path $PSScriptRoot 'Program.cs'
$executable = Join-Path $outputDirectory 'SolidWorksBridge.exe'
& $compiler /nologo /target:exe /codepage:65001 "/out:$executable" "/reference:$sldworks" "/reference:$swconst" $source
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE"
}
Copy-Item -LiteralPath $sldworks, $swconst -Destination $outputDirectory -Force
Write-Output "Build succeeded: $executable"
