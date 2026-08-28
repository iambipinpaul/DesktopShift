# Native BSP window tiling

DesktopShift can tile normal application windows without an external window
manager. It keeps one BSP tree for each Windows virtual desktop and monitor.
The feature is on by default for new setups.

Open **System settings > Window tiling** to enable or disable the feature and set the
outer gap, inner gap, minimum tile width, and minimum tile height. Select
**Save and apply** to update windows that are already open.

Open **Managed desktops** to turn BSP tiling on or off for one managed
desktop. DesktopShift saves this choice with the stable semantic desktop key,
so it remains correct when Windows replaces a desktop ID.

The Settings page keeps existing advanced float and ignore rules unchanged.
You can manage those rules through the portable JSON configuration when you
need identity matching beyond the basic layout controls.

Add the `tiling` section to `configuration.json`:

```json
"tiling": {
  "isEnabled": true,
  "layout": "bsp",
  "applyToAllVirtualDesktops": true,
  "outerGap": 5,
  "innerGap": 10,
  "insertMode": "splitFocusedOrLargest",
  "minimumTileWidth": 320,
  "minimumTileHeight": 240,
  "floatRules": [],
  "ignoreRules": [],
  "disabledManagedDesktopKeys": []
}
```

Gap and minimum-size values use 96-DPI units. DesktopShift scales them for
each monitor. The work area excludes the taskbar and other app bars.

`floatRules` and `ignoreRules` use the same application identity fields as
application assignment rules. An ignore rule removes the window from tiling.
A float rule observes the window but does not add it to the BSP tree.
Add a managed desktop semantic key to `disabledManagedDesktopKeys` to leave
that desktop under normal Windows placement. An empty list enables tiling on
all managed desktops.

## Window behavior

- A new window is tiled after application assignment finishes.
- A new leaf splits the focused tile when possible. Otherwise, it splits the
  largest tile.
- Minimized, hidden, application-cloaked, maximized, and fullscreen windows do
  not reserve visible layout space.
- A window that the Windows shell cloaks during a virtual desktop switch keeps
  its BSP slot. This keeps the layout order stable when the window returns.
- A skipped window joins the layout again when it returns to normal mode.
- DesktopShift does not restore a maximized window and does not change focus or
  Z order.
- Window positions are applied in one deferred Windows batch.

## Windows restrictions

- Full virtual-desktop tiling needs the validated full topology provider. If
  DesktopShift enters Limited Mode and cannot read the current desktop ID, it
  skips placement.
- Elevated and protected windows can reject state or desktop reads. DesktopShift
  reports and skips them. It does not request elevation.
- Some applications enforce their own minimum size or change the requested
  rectangle. DesktopShift reads the result once for diagnostics. It does not
  poll or repeatedly fight the application.
- BSP trees are kept in memory. After DesktopShift restarts, it rebuilds them
  from eligible top-level windows.
