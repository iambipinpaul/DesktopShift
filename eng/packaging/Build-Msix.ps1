[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [ValidateSet("x64", "arm64")]
    [string] $Architecture = "x64",

    [string] $PackageVersion = "0.1.0.0",

    [string] $OutputDirectory = ".artifacts\package",

    [string] $CertificatePath,

    [string] $CertificatePasswordEnvironmentVariable = "DESKTOPSHIFT_PACKAGE_CERTIFICATE_PASSWORD"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-MSBuild {
    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "MSBuild was not found. Install Visual Studio with .NET desktop, WinUI, MSIX, and C++ desktop build tools."
    }

    $installationPath = & $vswhere `
        -latest `
        -products * `
        -requires Microsoft.Component.MSBuild `
        -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath)) {
        throw "Visual Studio with MSBuild was not found."
    }

    $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "MSBuild was not found at '$candidate'."
    }

    return $candidate
}

if ($PackageVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "PackageVersion must contain four numeric components, for example 1.2.3.4."
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$projectPath = Join-Path $repositoryRoot "src\DesktopShift.App\DesktopShift.App.csproj"
$platform = if ($Architecture -ieq "arm64") { "ARM64" } else { "x64" }
$runtimeIdentifier = "win-$($Architecture.ToLowerInvariant())"
$resolvedOutputDirectory = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$arguments = @(
    $projectPath
    "/restore"
    "/t:Rebuild"
    "/m"
    "/verbosity:minimal"
    "/p:Configuration=$Configuration"
    "/p:Platform=$platform"
    "/p:RuntimeIdentifier=$runtimeIdentifier"
    "/p:TreatWarningsAsErrors=true"
    "/p:GenerateAppxPackageOnBuild=true"
    "/p:AppxBundle=Never"
    "/p:UapAppxPackageBuildMode=SideloadOnly"
    "/p:AppxPackageVersion=$PackageVersion"
    "/p:AppxPackageDir=$resolvedOutputDirectory\"
)

$previousPassword = [Environment]::GetEnvironmentVariable(
    "PackageCertificatePassword",
    [EnvironmentVariableTarget]::Process)

try {
    if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
        $arguments += "/p:AppxPackageSigningEnabled=false"
    }
    else {
        $resolvedCertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
        $certificatePassword = [Environment]::GetEnvironmentVariable(
            $CertificatePasswordEnvironmentVariable,
            [EnvironmentVariableTarget]::Process)
        if ([string]::IsNullOrEmpty($certificatePassword)) {
            throw "Set $CertificatePasswordEnvironmentVariable for the package-signing certificate. The password is passed to MSBuild through the process environment, not the command line."
        }

        [Environment]::SetEnvironmentVariable(
            "PackageCertificatePassword",
            $certificatePassword,
            [EnvironmentVariableTarget]::Process)
        $arguments += @(
            "/p:AppxPackageSigningEnabled=true"
            "/p:PackageCertificateKeyFile=$resolvedCertificatePath"
        )
    }

    $msbuild = Resolve-MSBuild
    & $msbuild @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX construction failed with exit code $LASTEXITCODE."
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        "PackageCertificatePassword",
        $previousPassword,
        [EnvironmentVariableTarget]::Process)
}

$packages = @(
    Get-ChildItem -LiteralPath $resolvedOutputDirectory -Recurse -File |
        Where-Object { $_.Extension -eq ".msix" }
)
if ($packages.Count -ne 1) {
    $found = if ($packages.Count -eq 0) {
        "none"
    }
    else {
        ($packages.FullName -join "', '")
    }
    throw "Expected exactly one $Architecture MSIX below '$resolvedOutputDirectory', but found $found."
}

$package = $packages[0]
Write-Output $package.FullName
