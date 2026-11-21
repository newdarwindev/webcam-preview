$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'src'
$output = Join-Path $root 'dist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$preview = Join-Path $output 'WebcamPreview.exe'
$installer = Join-Path $output 'Install-WebcamPreview.exe'

if (!(Test-Path -LiteralPath $compiler)) {
    throw 'The 64-bit .NET Framework compiler was not found.'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$preview" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $source 'WebcamPreview.cs')
if ($LASTEXITCODE -ne 0) { throw 'Webcam Preview compilation failed.' }

& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$installer" "/resource:$preview,WebcamPreview.exe" /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll (Join-Path $source 'Installer.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

Write-Output $installer
