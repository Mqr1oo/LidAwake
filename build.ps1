# Builds bin\LidAwake.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4.x) not found' }

$ico = Join-Path $root 'assets\LidAwake.ico'
if (-not (Test-Path $ico)) { & (Join-Path $root 'tools\make-icon.ps1') -Out $ico }

$wpf = Join-Path (Split-Path $csc) 'WPF'
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

& $csc /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu `
    "/out:$bin\LidAwake.exe" `
    "/win32icon:$ico" "/win32manifest:$root\src\app.manifest" "/resource:$ico,LidAwake.ico" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Management.dll "/r:$wpf\UIAutomationClient.dll" "/r:$wpf\UIAutomationTypes.dll" `
    "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Write-Host "Built $bin\LidAwake.exe"
