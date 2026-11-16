$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The 64-bit .NET Framework C# compiler is required.'
}

$source = Get-Content -Raw -LiteralPath 'Mirror.cs'
$version = [regex]::Match($source, 'AssemblyFileVersion\("([0-9]+\.[0-9]+\.[0-9]+)\.0"\)').Groups[1].Value
if (-not $version) {
    throw 'Mirror.cs must declare the release version.'
}

$name = "CardputerZeroMirror-$version-win-x64"
New-Item -ItemType Directory -Path 'dist' -Force | Out-Null
$executable = "dist\$name.exe"
$options = @(
    '/nologo', '/warnaserror+', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    '/win32manifest:app.manifest', '/win32icon:app.ico', '/resource:device.py,CardputerZero.device.py',
    "/out:$executable"
)
if (Test-Path -LiteralPath 'connection.local.conf' -PathType Leaf) {
    $options += '/resource:connection.local.conf,CardputerZero.connection'
}
& $compiler @options '.\Mirror.cs'
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$file = Get-Item -LiteralPath $executable
if ($file.VersionInfo.FileVersion -ne "$version.0") {
    throw "Unexpected embedded file version $($file.VersionInfo.FileVersion)."
}

Compress-Archive -LiteralPath $file.FullName -DestinationPath "dist\$name.zip" -Force
Get-FileHash -LiteralPath $file.FullName, "dist\$name.zip"
