param([switch]$Test)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x C# compiler was not found.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll',
    (Join-Path $framework 'WPF\UIAutomationClient.dll'), (Join-Path $framework 'WPF\UIAutomationTypes.dll'),
    (Join-Path $framework 'WPF\WindowsBase.dll'))
$options = @('/nologo', '/optimize+', '/warn:4', '/utf8output') + @($references | ForEach-Object { '/reference:' + $_ })
& $compiler @options '/target:winexe' ('/out:' + (Join-Path $output 'IDMClose.exe')) (Join-Path $PSScriptRoot 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination $output
$outputDocs = Join-Path $output 'docs'
New-Item -ItemType Directory -Path $outputDocs -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\DEVELOPMENT.md') -Destination $outputDocs
if ($Test) {
    & $compiler @options '/target:exe' '/main:IdmClose.Tests' ('/out:' + (Join-Path $output 'IDMClose.Tests.exe')) (Join-Path $PSScriptRoot 'Program.cs') (Join-Path $PSScriptRoot 'Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & (Join-Path $output 'IDMClose.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
Write-Output ('Built: ' + (Join-Path $output 'IDMClose.exe'))
