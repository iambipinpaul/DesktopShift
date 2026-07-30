# DesktopShift release process

This process produces one reproducible x64, per-user MSIX. It assumes Windows
11 22H2 or newer and a Visual Studio installation with MSBuild, the .NET desktop
and WinUI workloads, MSIX tooling, the Windows SDK, and C++ desktop build tools.

The package is self-contained for the Windows App SDK and carries
`DesktopShift.NativeBridge.dll`. Users install the MSIX; they do not copy DLLs,
install the Windows App Runtime separately, or edit configuration files.
The manifest declares the versioned
`Microsoft.VCLibs.140.00.UWPDesktop` framework dependency required by the
native bridge. The chosen distribution channel must resolve that dependency;
never instruct users to copy Visual C++ runtime DLLs into the package.

## Version

Choose a four-part numeric package version such as `1.2.3.4`. It must be greater
than the version already distributed under the same identity. The checked-in
baseline stays aligned across:

- `Package.appxmanifest` `Identity.Version`
- `DesktopShift.App.csproj` `FileVersion`
- the first three components of the project `Version`

The release command supplies the exact package version through
`AppxPackageVersion`.

## Local release candidate

An unsigned build is useful for construction and payload validation:

```powershell
$package = & .\eng\packaging\Build-Msix.ps1 `
    -Configuration Release `
    -PackageVersion 1.2.3.4

& .\eng\packaging\Validate-Msix.ps1 -PackagePath $package
```

The default output is `.artifacts\package`. The build fails unless exactly one
`.msix` is produced. The validator checks the package identity, publisher,
version, x64 architecture, Windows target, icons, opt-in startup task,
capabilities, versioned VCLibs declaration, application executable, native
bridge, Windows App Runtime payload, and absence of private-key files.

Use the signed command in [code-signing.md](code-signing.md) for a releasable
candidate. Do not publish the unsigned construction artifact.

## Required gates

The release workflow must complete these gates in order:

1. Restore dependencies.
2. Run warnings-as-errors Debug and Release x64 builds.
3. Run formatting/static analysis, host tests, unit tests, and the supported
   non-destructive Windows contract tests.
4. Construct exactly one Release x64 MSIX with
   `eng/packaging/Build-Msix.ps1`.
5. Sign it without placing key material in the repository or artifact.
6. Set `DESKTOPSHIFT_MSIX_PATH` to that signed file and run
   `PackagingArtifactContractTests`.
7. Run `eng/packaging/Validate-Msix.ps1 -RequireTrustedSignature`.
8. On a clean interactive x64 Windows test account, run
   `eng/packaging/Smoke-Test-Msix.ps1`.
9. Upload only the package that passed all preceding gates, plus its SHA-256
   digest.

The smoke script intentionally refuses to run when DesktopShift is already
installed or running. It installs for the current user, verifies the installed
startup-task declaration, launches through the package application user model
ID, stops only the process it observed, and removes only the package it
installed in its `finally` cleanup.

Use a disposable release-smoke account or VM. The runner must provide an
interactive desktop; launching a WinUI application from a non-interactive
service session is not a valid launch test.

## Manual acceptance

Run [package-install-smoke.md](../manual-tests/package-install-smoke.md) for the
release candidate. In particular, confirm that:

- the application launches without a console;
- Start with Windows is disabled on fresh install and works after the user
  opts in;
- no DLL copying, Windows App Runtime installation, or configuration editing is
  requested;
- uninstall removes the package registration, Start menu entry, and startup
  task.

Then run
[performance-budgets.md](../manual-tests/performance-budgets.md) against the
installed Release build. It measures the two things no automated test can — what
the application costs while nobody is touching it, and whether that cost stays
flat over a working day — and it produces the exported `performance.json` that
the release notes carry.

A performance figure that misses its budget in `docs/performance/budgets.md` is a
release blocker. The exported report names it as one, with the budget, the
measured value, and the consequence, so the decision to ship anyway is a decision
somebody has to take in writing rather than one that happens by omission.

Record the package SHA-256, four-part version, signing-certificate thumbprint,
runner Windows build, automated workflow URL, manual-test result, and the
exported performance report in the release notes.

## Install and uninstall for support

For a trusted signed package:

```powershell
Add-AppxPackage -Path .\DesktopShift.msix
Get-AppxPackage -Name BipinPaul.DesktopShift
```

Normal users may instead double-click the MSIX and use App Installer. Both
routes register the package for the current user. `Add-AppxPackage` adds a
signed package to a user account; see Microsoft’s
[cmdlet documentation](https://learn.microsoft.com/powershell/module/appx/add-appxpackage).

To uninstall from Settings, open **Apps > Installed apps**, find
**DesktopShift**, and choose **Uninstall**. The PowerShell equivalent is:

```powershell
Get-AppxPackage -Name BipinPaul.DesktopShift |
    Remove-AppxPackage
```

DesktopShift stores user-created configuration and diagnostics under the
current user’s local application-data directory. Package uninstall is not a
request to destroy that user data. A user who explicitly wants a complete data
reset may remove `%LOCALAPPDATA%\DesktopShift` after uninstalling and after
confirming that no diagnostics or configuration need to be retained.

## Failure handling

Do not upload an artifact after any failed gate. Preserve the unsigned build
logs, validation output, and test results for diagnosis, but never preserve or
upload a temporary private key. A failed smoke run should leave no package that
the script installed; if cleanup itself fails, quarantine the runner and remove
the test package before reusing the account.
