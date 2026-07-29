# Signed package install, startup, and uninstall matrix

Run this matrix against the exact signed x64 MSIX that passed automated package
validation. Use a clean, interactive, non-administrator Windows 11 22H2-or-newer
test account with no existing DesktopShift package or process.

Record the package path, SHA-256, package version, certificate thumbprint,
Windows build, tester, and result for every row.

| # | Action | Expected result |
|---|---|---|
| 1 | Run `eng/packaging/Validate-Msix.ps1 -PackagePath <path> -RequireTrustedSignature`. | Validation reports identity `BipinPaul.DesktopShift`, x64, the expected four-part version, a trusted signature, and no missing payload. |
| 2 | Confirm `Get-AppxPackage -Name BipinPaul.DesktopShift` and `Get-Process DesktopShift` return nothing. Run `eng/packaging/Smoke-Test-Msix.ps1 -PackagePath <path>`. | The script installs for this user, launches DesktopShift, closes the launched process, uninstalls, and reports success. No elevation prompt appears. |
| 3 | Install the same MSIX through the supported distribution route (App Installer, Store, enterprise deployment, or `Add-AppxPackage -Path <path>` on a machine where its declared VCLibs framework dependency is available). | Installation succeeds for the current user without requesting DLL copies, a separate Windows App Runtime install, or configuration edits. |
| 4 | Launch DesktopShift from Start. | The WinUI window and notification-area icon appear. No console window appears. Only one DesktopShift process owns the application instance. |
| 5 | Inspect **Settings > Apps > Startup** before opting in. | DesktopShift is present and disabled; a fresh install does not silently add itself to sign-in. |
| 6 | In DesktopShift, enable **Start DesktopShift with Windows**. Close the app and sign out, then sign back in. | DesktopShift starts for this user without elevation or an error dialog. |
| 7 | Disable **Start DesktopShift with Windows** in DesktopShift. Sign out and back in. | DesktopShift does not start. |
| 8 | Disable DesktopShift from Windows **Startup apps**, then try to enable it in DesktopShift. | DesktopShift respects the Windows user decision and explains that startup must be re-enabled in Windows Settings. |
| 9 | Launch DesktopShift again and exercise the Overview, Desktops, Rules, Settings, and Activity pages. | The installed package finds its native bridge and Windows App SDK files; no missing-DLL, missing-runtime, or configuration-file error appears. |
| 10 | Uninstall from **Settings > Apps > Installed apps**. | The uninstall completes without elevation. The Start entry, package registration, and startup entry are removed. |
| 11 | Run `Get-AppxPackage -Name BipinPaul.DesktopShift` and check **Startup apps** and Start. | No package, startup task, or application shortcut remains for this user. |
| 12 | If a complete data reset is part of this test, first preserve any needed diagnostics, then remove `%LOCALAPPDATA%\DesktopShift`. | Only the test account’s retained DesktopShift configuration/diagnostics are removed. This is optional user-data cleanup, not a prerequisite for package uninstall. |

The automated smoke test refuses an existing installation so it cannot replace
or uninstall a tester’s real copy. Do not bypass that guard. Use a disposable
account or VM when repeated install/uninstall testing is required.
