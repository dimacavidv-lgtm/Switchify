# Compila Switchify.exe con el compilador de C# que ya trae Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw = Split-Path $csc
$winmd = Join-Path $env:WINDIR 'System32\WinMetadata'
$out = if ($args[0]) { $args[0] } else { Join-Path $PSScriptRoot 'Switchify.exe' }
$icon = Join-Path $PSScriptRoot 'assets\switchify.ico'

# Las APIs de Windows (PDF y OCR) se usan a través de los .winmd del sistema.
# El icono va dos veces: como icono del .exe (/win32icon) y como recurso para la bandeja (/resource).
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out" `
    "/win32icon:$icon" "/resource:$icon,Switchify.ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Xml.dll /r:System.Xml.Linq.dll `
    "/r:$fw\System.Runtime.dll" "/r:$fw\System.Threading.Tasks.dll" "/r:$fw\System.Runtime.WindowsRuntime.dll" `
    "/r:$winmd\Windows.Foundation.winmd" "/r:$winmd\Windows.Data.winmd" "/r:$winmd\Windows.Storage.winmd" `
    "/r:$winmd\Windows.Media.winmd" "/r:$winmd\Windows.Graphics.winmd" "/r:$winmd\Windows.Globalization.winmd" `
    (Join-Path $PSScriptRoot 'src\*.cs')

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Listo: $out"

# Compilación normal (sin ruta de salida): se instala la copia que arranca con Windows.
# Si está abierta, se cierra, se reemplaza y se vuelve a abrir.
if (-not $args[0]) {
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\Switchify'
    $installed = Join-Path $installDir 'Switchify.exe'
    New-Item -ItemType Directory -Force $installDir | Out-Null
    $running = @(Get-Process Switchify -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installed })
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
    Copy-Item $out $installed -Force
    if ($running.Count -gt 0) { Start-Process $installed }
    Write-Host "Instalado: $installed"
}
