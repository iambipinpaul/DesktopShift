[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackagePath,

    [int] $LaunchTimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedPackagePath = (Resolve-Path -LiteralPath $PackagePath).Path

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackagePath)
try {
    $manifestEntry = $archive.GetEntry("AppxManifest.xml")
    if ($null -eq $manifestEntry) {
        throw "The package has no AppxManifest.xml."
    }

    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try {
        [xml] $manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
$identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaces)
$application = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application", $namespaces)
if ($null -eq $identity -or $null -eq $application) {
    throw "The package manifest has no installable application identity."
}

$packageName = $identity.GetAttribute("Name")
$applicationId = $application.Id
$alreadyInstalled = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
if ($alreadyInstalled.Count -ne 0) {
    throw "Package '$packageName' is already installed for this user. The smoke test will not replace or remove an existing installation."
}

$existingProcesses = @(Get-Process -Name "DesktopShift" -ErrorAction SilentlyContinue)
if ($existingProcesses.Count -ne 0) {
    throw "DesktopShift is already running. Close it before running the package smoke test."
}

$installedPackage = $null
$launchedProcesses = @()
try {
    Add-AppxPackage -Path $resolvedPackagePath -ErrorAction Stop
    $installedPackage = Get-AppxPackage -Name $packageName -ErrorAction Stop
    if ($null -eq $installedPackage) {
        throw "Add-AppxPackage returned without installing '$packageName' for the current user."
    }

    $installedManifest = Get-AppxPackageManifest -Package $installedPackage
    $startupExtension = @(
        $installedManifest.Package.Applications.Application.Extensions.Extension |
            Where-Object { $_.Category -eq "windows.startupTask" }
    )
    if ($startupExtension.Count -ne 1) {
        throw "The installed package does not expose exactly one startup task."
    }

    $applicationUserModelId = "$($installedPackage.PackageFamilyName)!$applicationId"
    Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$applicationUserModelId"

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($LaunchTimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $launchedProcesses = @(Get-Process -Name "DesktopShift" -ErrorAction SilentlyContinue)
    } while ($launchedProcesses.Count -eq 0 -and [DateTimeOffset]::UtcNow -lt $deadline)

    if ($launchedProcesses.Count -eq 0) {
        throw "The installed package did not launch DesktopShift within $LaunchTimeoutSeconds seconds."
    }

    foreach ($process in $launchedProcesses) {
        Stop-Process -Id $process.Id -ErrorAction Stop
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    $launchedProcesses = @()
}
finally {
    foreach ($process in $launchedProcesses) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    }

    if ($null -eq $installedPackage) {
        $installedCandidates = @(
            Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
        if ($installedCandidates.Count -gt 1) {
            throw "More than one current-user package matched '$packageName' during cleanup."
        }

        if ($installedCandidates.Count -eq 1) {
            $installedPackage = $installedCandidates[0]
        }
    }

    if ($null -ne $installedPackage) {
        Remove-AppxPackage -Package $installedPackage.PackageFullName -ErrorAction Stop
        $remaining = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
        if ($remaining.Count -ne 0) {
            throw "Package '$packageName' remained installed after Remove-AppxPackage."
        }
    }
}

Write-Output "Installed, launched, and uninstalled $packageName for the current user."
