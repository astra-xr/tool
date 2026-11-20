param(
    [ValidatePattern('^[A-Za-z0-9_-]+\.exe$')]
    [string]$OutputName = 'safeTriage-Windows-x64.exe'
)

$ErrorActionPreference = 'Stop'

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
$source = Join-Path $PSScriptRoot 'src'
$output = Join-Path $PSScriptRoot $OutputName

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The x64 .NET Framework compiler was not found.'
}

$arguments = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/optimize+'
    '/codepage:65001'
    "/out:$output"
    ('/win32manifest:' + (Join-Path $source 'App.manifest'))
    ('/win32icon:' + (Join-Path $source 'Icon.ico'))
    ('/resource:' + (Join-Path $source 'Icon.ico') + ',SafeTriage.Icon.ico')
    ('/resource:' + (Join-Path $source 'MainWindow.xaml') + ',SafeTriage.MainWindow.xaml')
    '/reference:System.Xaml.dll'
    '/reference:System.Xml.Linq.dll'
    '/reference:System.ServiceProcess.dll'
    '/reference:System.Management.dll'
)
foreach ($assembly in 'PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') {
    $arguments += '/reference:' + (Join-Path $wpf $assembly)
}
$arguments += Get-ChildItem -LiteralPath $source -Filter '*.cs' | ForEach-Object { $_.FullName }

& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output $output
