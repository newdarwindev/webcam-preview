$ErrorActionPreference = 'Stop'
$installer = & (Join-Path $PSScriptRoot 'build.ps1')
$process = Start-Process -FilePath $installer -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Installation failed with exit code $($process.ExitCode)." }
