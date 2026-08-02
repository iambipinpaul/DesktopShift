[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackagePath,

    [string] $ExpectedIdentityName = "BipinPaul.DesktopShift",

    [string] $ExpectedPublisher = "CN=B035082D-0ECF-4DD6-B68E-293CC1A48C47",

    [ValidateSet("x64", "arm64")]
    [string] $ExpectedArchitecture = "x64",

    [switch] $RequireTrustedSignature
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Condition {
    param(
        [Parameter(Mandatory)]
        [bool] $Condition,

        [Parameter(Mandatory)]
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-Entry {
    param(
        [Parameter(Mandatory)]
        [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]] $Entries,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $entry = $null
    if (-not $Entries.TryGetValue($Name.Replace("\", "/"), [ref] $entry)) {
        throw "The package is missing '$Name'."
    }

    return $entry
}

function Assert-LogicalAsset {
    param(
        [Parameter(Mandatory)]
        [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]] $Entries,

        [Parameter(Mandatory)]
        [string] $LogicalPath
    )

    $normalized = $LogicalPath.Replace("\", "/")
    if ($Entries.ContainsKey($normalized)) {
        return
    }

    $extension = [IO.Path]::GetExtension($normalized)
    $withoutExtension = $normalized.Substring(0, $normalized.Length - $extension.Length)
    $qualifiedPrefix = "$withoutExtension."
    $match = $Entries.Keys |
        Where-Object {
            $_.StartsWith($qualifiedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            $_.EndsWith($extension, [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1
    if ($null -eq $match) {
        throw "The package has no resource-qualified file for logical asset '$LogicalPath'."
    }
}

function Get-PeMachine {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchiveEntry] $Entry
    )

    $entryStream = $Entry.Open()
    $buffer = [IO.MemoryStream]::new()
    try {
        $entryStream.CopyTo($buffer)
        $buffer.Position = 0
        $reader = [IO.BinaryReader]::new($buffer, [Text.Encoding]::UTF8, $true)
        try {
            Assert-Condition ($reader.ReadUInt16() -eq 0x5A4D) `
                "'$($Entry.FullName)' has no DOS executable header."
            $buffer.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            Assert-Condition ($peOffset -gt 0 -and $peOffset -le ($buffer.Length - 6)) `
                "'$($Entry.FullName)' has an invalid PE header offset."
            $buffer.Position = $peOffset
            Assert-Condition ($reader.ReadUInt32() -eq 0x00004550) `
                "'$($Entry.FullName)' has no PE signature."
            return $reader.ReadUInt16()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $entryStream.Dispose()
        $buffer.Dispose()
    }
}

function Resolve-SignTool {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $versioned = @(
        Get-ChildItem -LiteralPath $kitsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [Version] $_.Name } -Descending
    )
    foreach ($directory in $versioned) {
        $candidate = Join-Path $directory.FullName "x64\signtool.exe"
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $fallback = Join-Path $kitsRoot "x64\signtool.exe"
    if (Test-Path -LiteralPath $fallback) {
        return $fallback
    }

    throw "SignTool was not found in the installed Windows SDK."
}

$resolvedPackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
Assert-Condition ($resolvedPackagePath.EndsWith(".msix", [StringComparison]::OrdinalIgnoreCase)) `
    "PackagePath must identify a single .msix package."

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackagePath)
try {
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $entries[$entry.FullName.Replace("\", "/")] = $entry
    }

    $manifestEntry = Get-Entry -Entries $entries -Name "AppxManifest.xml"
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try {
        [xml] $manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $namespaces.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
    $namespaces.AddNamespace("uap5", "http://schemas.microsoft.com/appx/manifest/uap/windows10/5")
    $namespaces.AddNamespace(
        "rescap",
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities")

    $identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaces)
    Assert-Condition ($null -ne $identity) "The package manifest has no Identity."
    $identityName = $identity.GetAttribute("Name")
    $publisher = $identity.GetAttribute("Publisher")
    $architecture = $identity.GetAttribute("ProcessorArchitecture")
    $version = $identity.GetAttribute("Version")
    Assert-Condition ($identityName -eq $ExpectedIdentityName) `
        "Expected identity '$ExpectedIdentityName', found '$identityName'."
    Assert-Condition ($publisher -eq $ExpectedPublisher) `
        "Expected publisher '$ExpectedPublisher', found '$publisher'."
    Assert-Condition ($architecture -eq $ExpectedArchitecture) `
        "Expected $ExpectedArchitecture package architecture, found '$architecture'."
    Assert-Condition ($version -match '^\d+\.\d+\.\d+\.\d+$') `
        "Package identity version '$version' is not a four-part MSIX version."

    $targetFamily = $manifest.SelectSingleNode(
        "/f:Package/f:Dependencies/f:TargetDeviceFamily[@Name='Windows.Desktop']",
        $namespaces)
    Assert-Condition ($null -ne $targetFamily) "The package does not target Windows.Desktop."
    Assert-Condition ([Version] $targetFamily.MinVersion -ge [Version] "10.0.22621.0") `
        "The package must require Windows 11 22H2 (10.0.22621.0) or newer."

    $vclibs = $manifest.SelectSingleNode(
        "/f:Package/f:Dependencies/f:PackageDependency[@Name='Microsoft.VCLibs.140.00.UWPDesktop']",
        $namespaces)
    Assert-Condition ($null -ne $vclibs) `
        "The package does not declare the native bridge's Microsoft VCLibs framework dependency."
    $vclibsMinimumVersion = $vclibs.GetAttribute("MinVersion")
    $parsedVclibsVersion = [Version]::new()
    Assert-Condition (
        [Version]::TryParse($vclibsMinimumVersion, [ref] $parsedVclibsVersion) -and
        $parsedVclibsVersion -gt [Version] "0.0") `
        "The Microsoft VCLibs dependency must declare a non-zero minimum version."

    $application = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application", $namespaces)
    Assert-Condition ($null -ne $application) "The package has no application."
    Assert-Condition ($application.Executable -eq "DesktopShift.exe") `
        "Expected DesktopShift.exe as the packaged executable, found '$($application.Executable)'."

    $visualElements = $application.SelectSingleNode("uap:VisualElements", $namespaces)
    Assert-Condition ($null -ne $visualElements) "The package has no VisualElements metadata."
    Assert-LogicalAsset -Entries $entries -LogicalPath $visualElements.Square150x150Logo
    Assert-LogicalAsset -Entries $entries -LogicalPath $visualElements.Square44x44Logo

    $propertiesLogo = $manifest.SelectSingleNode("/f:Package/f:Properties/f:Logo", $namespaces)
    Assert-Condition ($null -ne $propertiesLogo) "The package has no store logo."
    Assert-LogicalAsset -Entries $entries -LogicalPath $propertiesLogo.InnerText

    $startupTask = $application.SelectSingleNode(
        "f:Extensions/uap5:Extension[@Category='windows.startupTask']/uap5:StartupTask",
        $namespaces)
    Assert-Condition ($null -ne $startupTask) "The installed package would have no startup task."
    Assert-Condition ($startupTask.TaskId -eq "DesktopShiftStartupTask") `
        "The startup task has an unexpected TaskId '$($startupTask.TaskId)'."
    Assert-Condition ($startupTask.Enabled -eq "false") `
        "The startup task must be disabled until the user opts in."

    $capabilities = @(
        $manifest.SelectNodes("/f:Package/f:Capabilities/*", $namespaces) |
            ForEach-Object { $_.GetAttribute("Name") }
    )
    Assert-Condition ($capabilities.Count -eq 1 -and $capabilities[0] -eq "runFullTrust") `
        "The release package must declare only the runFullTrust capability."

    foreach ($requiredFile in @(
        "DesktopShift.exe"
        "DesktopShift.dll"
        "DesktopShift.NativeBridge.dll"
        "Microsoft.WindowsAppRuntime.dll"
        "Microsoft.ui.xaml.dll"
        "resources.pri"
    )) {
        Get-Entry -Entries $entries -Name $requiredFile | Out-Null
    }

    $expectedMachine = switch ($ExpectedArchitecture) {
        "x64" { 0x8664 }
        "arm64" { 0xAA64 }
    }
    foreach ($nativeFile in @("DesktopShift.exe", "DesktopShift.NativeBridge.dll")) {
        $entry = Get-Entry -Entries $entries -Name $nativeFile
        $machine = Get-PeMachine -Entry $entry
        Assert-Condition ($machine -eq $expectedMachine) `
            ("Expected {0} to target {1} (PE machine 0x{2:X4}), found 0x{3:X4}." -f `
                $nativeFile, $ExpectedArchitecture, $expectedMachine, $machine)
    }

    $forbiddenPayload = @(
        $entries.Keys |
            Where-Object { $_ -match '\.(pfx|p12|snk|key)$' }
    )
    Assert-Condition ($forbiddenPayload.Count -eq 0) `
        "The package contains private-key material: $($forbiddenPayload -join ', ')."

    $hasSignature = $entries.ContainsKey("AppxSignature.p7x")
    if ($RequireTrustedSignature) {
        Assert-Condition $hasSignature "The package has no AppxSignature.p7x."
    }
}
finally {
    $archive.Dispose()
}

if ($RequireTrustedSignature) {
    $signTool = Resolve-SignTool
    & $signTool verify /pa /all /v $resolvedPackagePath
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool rejected the package signature with exit code $LASTEXITCODE."
    }
}

$hash = (Get-FileHash -LiteralPath $resolvedPackagePath -Algorithm SHA256).Hash
[pscustomobject] @{
    Path = $resolvedPackagePath
    Identity = $ExpectedIdentityName
    Version = $version
    Architecture = $architecture
    Signed = $hasSignature
    Sha256 = $hash
}
