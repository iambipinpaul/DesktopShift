[CmdletBinding()]
param(
    [switch] $ConfirmDeletion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$localAppData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localAppData)) {
    throw 'Windows did not provide a LocalApplicationData directory.'
}

$expectedParent = [IO.Path]::GetFullPath($localAppData)
$configurationRoot = [IO.Path]::GetFullPath(
    (Join-Path -Path $expectedParent -ChildPath 'DesktopShift'))

if (-not [string]::Equals(
        [IO.Path]::GetDirectoryName($configurationRoot),
        $expectedParent,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals(
        [IO.Path]::GetFileName($configurationRoot),
        'DesktopShift',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing an unexpected configuration root: $configurationRoot"
}

$configurationFileNames = @(
    'configuration.json'
    'configuration.candidate.json'
    'configuration.last-valid.json'
    'configuration.json.tmp'
    'configuration.candidate.json.tmp'
    'configuration.last-valid.json.tmp'
    'managed-desktop-bindings.json'
    'managed-desktop-bindings.json.tmp'
)

$targets = @(
    foreach ($fileName in $configurationFileNames) {
        $candidate = Join-Path -Path $configurationRoot -ChildPath $fileName
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            [IO.Path]::GetFullPath($candidate)
        }
    }
)

Write-Output "DesktopShift configuration root: $configurationRoot"
if ($targets.Count -eq 0) {
    Write-Output 'No DesktopShift configuration files were found.'
    exit 0
}

Write-Output 'Configuration files:'
foreach ($target in $targets) {
    Write-Output "  $target"
}

if (-not $ConfirmDeletion) {
    Write-Output 'Preview only. Run again with -ConfirmDeletion to delete these files.'
    exit 0
}

$running = @(Get-Process -Name 'DesktopShift' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw 'DesktopShift is running. Exit it from the tray before clearing configuration.'
}

foreach ($target in $targets) {
    Remove-Item -LiteralPath $target -Force
    Write-Output "Deleted: $target"
}

Write-Output "Deleted $($targets.Count) configuration file(s)."
Write-Output 'Logs, diagnostics, crash data, and Windows virtual desktops were preserved.'
