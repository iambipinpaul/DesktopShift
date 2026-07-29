# MSIX code signing

DesktopShift release packages must be signed. Windows verifies that the
certificate subject matches the `Publisher` in
`src/DesktopShift.App/Package.appxmanifest`; the current value is
`CN=Bipin Paul`. Changing that publisher changes the package identity and must
be treated as a migration, not as a routine certificate renewal.

## Certificate requirements

Use a code-signing certificate whose subject is exactly `CN=Bipin Paul`, whose
private key is available to the signing job, and whose certificate chain is
trusted by the target machines. Production releases should use an
organization-controlled certificate or a managed signing service. A
self-signed certificate is suitable only for development and must be installed
in the test account's trusted certificate store before that account installs
the package.

Never commit a `.pfx`, password, exported private key, or base64-encoded
certificate. The repository ignores common package and certificate file
extensions as a last line of defense; it is not a secret-management system.
Keep production material in the CI secret store or signing service and expose
it only to the release job. Remove any temporary certificate file in an
always-run cleanup step.

The GitHub `release` environment must define these two secrets:

- `DESKTOPSHIFT_SIGNING_CERTIFICATE_BASE64`: the PFX file encoded as one
  base64 string.
- `DESKTOPSHIFT_SIGNING_CERTIFICATE_PASSWORD`: the PFX password.

Protect that environment with the repository's release approval policy. The
workflow materializes the PFX only in the runner's temporary directory and
deletes it in an always-run cleanup step.

Microsoft requires MSIX packages to be signed and recommends timestamping
production signatures. SignTool must be given an explicit digest algorithm;
use SHA-256 for both the file and timestamp digest. See Microsoft’s
[MSIX signing overview](https://learn.microsoft.com/windows/msix/package/signing-package-overview)
and [SignTool package-signing guide](https://learn.microsoft.com/windows/msix/package/sign-app-package-using-signtool).

## Build and sign

From a PowerShell prompt at the repository root:

```powershell
$env:DESKTOPSHIFT_PACKAGE_CERTIFICATE_PASSWORD = '<read from a secret store>'
try {
    $package = & .\eng\packaging\Build-Msix.ps1 `
        -Configuration Release `
        -PackageVersion 1.2.3.4 `
        -CertificatePath C:\secure\DesktopShift.Release.pfx

    & .\eng\packaging\Validate-Msix.ps1 `
        -PackagePath $package `
        -RequireTrustedSignature
}
finally {
    Remove-Item Env:\DESKTOPSHIFT_PACKAGE_CERTIFICATE_PASSWORD `
        -ErrorAction SilentlyContinue
}
```

`Build-Msix.ps1` sends the password to MSBuild through the process environment,
not the command line. The certificate path and password are optional for a
local construction check, but an unsigned package is not a releasable artifact.
`Validate-Msix.ps1 -RequireTrustedSignature` runs SignTool verification and must
pass before publication.

If the production signing service signs an already-built package instead, use
the service’s supported MSIX flow, SHA-256, and an RFC 3161 timestamp. Run
`Validate-Msix.ps1 -RequireTrustedSignature` after signing; signing is the final
package mutation.

## Certificate rotation

For a normal renewal:

1. Obtain a replacement certificate with the same subject,
   `CN=Bipin Paul`.
2. Update only CI secret or signing-service configuration.
3. Build a version greater than the latest published four-part MSIX version.
4. Install the new package over the previous signed release and verify that
   Windows treats it as an update.
5. Run the package validator and the install-launch-uninstall smoke test.

Do not change `Identity.Name` or `Identity.Publisher` during a renewal. Windows
uses those values to determine package identity and update eligibility.

## Incident response

If private key material is exposed, stop publishing, revoke or disable the
credential with its issuer/signing service, remove it from CI, and rotate it.
Deleting a key from the latest Git commit does not remove it from history.
Coordinate repository-history cleanup separately and issue a new signed
package only after the replacement identity is trusted.
