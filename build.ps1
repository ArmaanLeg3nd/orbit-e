param([switch]$Run, [switch]$Demo, [switch]$Test)
$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Orbit requires the .NET Framework 4.x desktop runtime included with Windows 10 and 11.' }
$outputPath = Join-Path $projectPath 'bin'
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/utf8output', ('/out:' + (Join-Path $outputPath 'Orbit.exe')), ('/win32manifest:' + (Join-Path $projectPath 'src\app.manifest')), ('/resource:' + (Join-Path $projectPath 'src\Widget.xaml') + ',Orbit.Widget.xaml'))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $frameworkPath $reference) }
$compilerArgs += @(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Host ('Built ' + (Join-Path $outputPath 'Orbit.exe'))
if ($Test) {
    $testProcess = Start-Process -FilePath (Join-Path $outputPath 'Orbit.exe') -ArgumentList '--self-test' -WorkingDirectory $projectPath -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath (Join-Path $outputPath 'self-test.txt')
    if ($testProcess.ExitCode -ne 0) { throw 'Self-tests failed.' }
}
if ($Run) {
    $launchArgs = @('--live')
    if ($Demo) { $launchArgs = @('--demo') }
    Start-Process -FilePath (Join-Path $outputPath 'Orbit.exe') -ArgumentList $launchArgs -WorkingDirectory $projectPath
}
