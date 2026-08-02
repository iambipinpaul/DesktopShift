---
name: clear-local-config
description: Safely preview and delete DesktopShift's local configuration and managed-desktop binding files on Windows while preserving logs and diagnostics. Use when the user asks to clear, delete, reset, factory-reset, or recreate DesktopShift configuration, forget its desktop bindings, or run the project-level local-config cleanup.
---

# Clear DesktopShift Local Config

Reset only DesktopShift configuration stored in `%LOCALAPPDATA%\DesktopShift`.
Use the bundled script instead of composing deletion commands.

## Workflow

1. Run a preview:

   ```powershell
   & ".agents\skills\clear-local-config\scripts\clear-local-config.ps1"
   ```

2. Check that the printed root is exactly the current user's
   `%LOCALAPPDATA%\DesktopShift` directory and review every listed file.

3. If the user's current request explicitly says to clear, delete, reset, or
   run the cleanup, execute it without requesting duplicate confirmation:

   ```powershell
   & ".agents\skills\clear-local-config\scripts\clear-local-config.ps1" -ConfirmDeletion
   ```

4. Report the deleted files and explain that the next launch uses first-run
   configuration. If the script says DesktopShift is running, ask the user to
   exit it from the tray and do not kill the process or bypass the guard.

## Boundaries

- Delete only the exact configuration, last-valid, candidate, temporary, and
  managed-desktop binding filenames declared by the bundled script.
- Preserve `logs`, `diagnostics`, `crash.log`, exported files, and unrelated
  content.
- Do not recursively delete `%LOCALAPPDATA%\DesktopShift`.
- Do not delete or reorder Windows virtual desktops. This reset forgets stored
  bindings; it does not remove desktops.
- Do not change the Windows Startup Task registration. Treat that as a separate
  user request.
