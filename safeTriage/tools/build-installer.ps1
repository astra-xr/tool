param(
    [string]$AppPath,
    [string]$CompilerPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (-not $AppPath) {
    $AppPath = Join-Path $project 'safeTriage-glass.exe'
}
$payload = (Resolve-Path -LiteralPath $AppPath).Path
$app = Get-Item -LiteralPath $payload
if ($app.VersionInfo.ProductName -ne 'safeTriage') {
    throw 'AppPath must point to a built safeTriage executable.'
}

if (-not $CompilerPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $CompilerPath = if ($compiler) {
        $compiler.Source
    } else {
        Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    }
}
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Install Inno Setup 6.7 or pass its ISCC.exe path with -CompilerPath.'
}
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $project 'release'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$destination = (Resolve-Path -LiteralPath $OutputDirectory).Path

$chinese = Join-Path (Split-Path -Parent $compiler) 'Languages\ChineseSimplified.isl'
$temporaryTranslation = $null
try {
    if (-not (Test-Path -LiteralPath $chinese -PathType Leaf)) {
        $temporaryTranslation = Join-Path ([IO.Path]::GetTempPath()) ('safetriage-inno-' + [Guid]::NewGuid().ToString('N') + '.isl')
        $translationUrl = 'https://raw.githubusercontent.com/jrsoftware/issrc/6ef32198ef1f7b7b375cd4b6b90896c2a58eb4c2/Files/Languages/ChineseSimplified.isl'
        Invoke-WebRequest -UseBasicParsing -Uri $translationUrl -OutFile $temporaryTranslation -TimeoutSec 30
        if ((Get-FileHash -LiteralPath $temporaryTranslation -Algorithm SHA256).Hash -ne 'E0B0B350E2245F3C5E65586DFE43D574F6E7F06F2261149ABA284954B3FC9A8D') {
            throw 'The Simplified Chinese translations failed verification.'
        }
        $chinese = $temporaryTranslation
    }

    & $compiler "/DPayloadExe=$payload" "/DChineseMessages=$chinese" "/O$destination" (Join-Path $PSScriptRoot 'safeTriage.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Installer compilation failed (ISCC exit code $LASTEXITCODE)."
    }
} finally {
    if ($temporaryTranslation -and (Test-Path -LiteralPath $temporaryTranslation)) {
        Remove-Item -LiteralPath $temporaryTranslation
    }
}

$installerName = 'safeTriage-Setup-Windows-x64.exe'
$installer = Join-Path $destination $installerName
$digest = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$installer.sha256", "$digest  $installerName" + [char]10, [Text.Encoding]::ASCII)
Write-Output $installer
