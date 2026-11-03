# Run from the published folder after copying it to its permanent location.
param([switch]$EnableAutoStart)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'GfnFullscreen.exe'
$icon = Join-Path $PSScriptRoot 'GeForceNOW.ico'
if (!(Test-Path -LiteralPath $exe) -or !(Test-Path -LiteralPath $icon)) {
    throw 'The launcher or icon is missing. Run setup.ps1 from the complete published folder.'
}
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'GeForce NOW.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.IconLocation = "$icon,0"
$shortcut.Arguments = ''
$shortcut.Save()
if ($EnableAutoStart) {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'GfnFullscreen' -Value ('"' + $exe + '"')
}
Write-Host "Created $shortcutPath"
if ($EnableAutoStart) { Write-Host 'Enabled launch at Windows sign-in.' }
