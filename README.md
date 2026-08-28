# DesktopShift

Automatically assign application windows to managed Windows Virtual Desktops.

## Install

Get it from the
[Microsoft Store](https://apps.microsoft.com/detail/9mvqdq26sccg), or install it
from a terminal:

```powershell
winget install 9MVQDQ26SCCG -s msstore
```

Windows 11 22H2 or newer, on x64 or ARM64. Everything the app needs ships inside
the package: no separate runtime to install, no DLLs to copy, no configuration
files to edit.

## What it does

Windows virtual desktops work well right up until you lose track of which one
your editor ended up on. DesktopShift takes the bookkeeping away: you say once
that VS Code belongs on **Development**, and from then on its windows go
there by themselves.

It lives in the tray. This page explains what it does in plain words, so nothing
it does comes as a surprise.

![The DesktopShift Home page, showing workspace health, enabled rule and managed
desktop counts, and quick settings for automatic assignment](docs/images/home.png)

## Getting started

1. **Launch it.** DesktopShift creates three managed desktops alongside your
   existing first one, and starts with rules for a handful of common apps. You
   can rename any of them.
2. **Make it yours.** On **Application rules**, point the apps you care about at
   the desktop where they belong. Anything without a rule goes to the first
   desktop, so nothing is ever lost.
3. **Open an app.** Its window lands on the right desktop, and your screen
   follows you there.
4. **Tidy what is already open.** Press **Reassign now** on Home, or
   **Reassign all windows** in the tray menu.

Start with Windows and native window tiling are on by default. Local activity
recording is off until you turn it on.

![The DesktopShift tray menu, offering Open DesktopShift, Reassign all windows,
Pause automatic assignment, and Exit](docs/images/tray-menu.png)

## The one idea to remember

**Your screen follows a window only when you asked for that window.**

If you open an app, you want to see it, so DesktopShift takes you to it.
If a window appears on its own, you did not ask for it, so DesktopShift moves
it quietly and leaves you where you are.

The rest of this page is the detail behind that sentence.

## What happens to a window

### When a window opens

| What happens | Where the window goes | Does your screen move? |
| --- | --- | --- |
| You open an app. A rule names it. | To the desktop in the rule | **Yes.** You see the app you opened. |
| You open an app. No rule names it. | To the first desktop | **Yes.** You see the app you opened. |
| You open an app. Its rule says **Anywhere**. | Stays where you are | No. Nothing moves. |
| A window opens by itself. A rule names it. | To the desktop in the rule | **No.** You keep working. |
| A window opens by itself. No rule names it. | To the first desktop | **No.** You keep working. |
| A window opens and closes very fast. | Nowhere. It is gone. | No |
| The window is already on the right desktop. | It stays. Nothing is moved. | No |

"A window opens by itself" means you did not start it. For example: an update
window, a helper window, or an app waking up.

A new window is not moved the instant it appears. DesktopShift waits about
150 milliseconds first, to see whether you start using the window. That is very
short, and you will not notice it. It is how the app tells the two cases above
apart.

### When you use a window that is already open

| What happens | Where the window goes | Does your screen move? |
| --- | --- | --- |
| You click an old window. A rule names it. It is already on the right desktop. | It stays | No. You are already there. |
| You click an old window. A rule names it. It is on the wrong desktop. | To the desktop in the rule | **Yes** |
| You click an old window. No rule names it. | It stays. It is not moved. | No |

The last row is on purpose. A window must never jump away while you are
clicking it.

### Everything else

| What happens | Where windows go | Does your screen move? |
| --- | --- | --- |
| You press **Reassign all** | All to their desktops | **No.** Many windows move. You stay still. |
| DesktopShift starts up | Old windows are tidied | **No.** You stay where you signed in. |
| You pause the app | Nowhere. Nothing moves. | No |
| DesktopShift just moved your screen | — | It ignores its own change, so it cannot loop. |

## Rules

Every window gets one of three answers.

| Your rules say | What happens to the window |
| --- | --- |
| A rule names the app, and points to a desktop | The window moves to that desktop |
| A rule names the app, and says **Anywhere** | The window stays where it is |
| No rule names the app | The window moves to the **first desktop** |

So every window goes somewhere. Nothing is forgotten. The first desktop is the
one Task View shows first. DesktopShift never renames it and never adds it to
your managed desktops.

On a fresh setup, the desktop order is **Default**, **Run & Observe**,
**Development**, then **Remote**.
"Default" is the existing first Windows desktop; DesktopShift creates and owns
only the three managed desktops that follow it.

DesktopShift ships with one grouped **Anywhere** default for File Explorer and
Notepad. Windows-managed surfaces such as Settings, credential prompts, and
passkey prompts are shown separately on the Rules page and are always left
where Windows opens them. Everything else is your decision.
When a window is swept to the first desktop, the Activity page offers a
**Create a rule for this app** button on that row.

![The Application rules page, listing rules that map processes and packages to
desktops, above the Windows-managed windows DesktopShift never
moves](docs/images/application-rules.png)

### The switch setting on each rule

Each rule has its own setting for this, in the rule editor.

| Setting | What it means |
| --- | --- |
| **Never switches desktop** | The window moves. Your screen never follows it. |
| **Switches on foreground activation** | Your screen follows whenever you make the window active. |
| **Switches on a new window's first activation** | Your screen follows only the first time, when the window is new. |

## Native window tiling

DesktopShift can also tile windows with a native BSP layout. It does not need
an external tiling window manager or tiling hotkeys. It is on by default for
new setups. See [Native BSP window tiling](docs/native-bsp-tiling.md) for the
configuration and known Windows restrictions. You can turn it off globally or
for one managed desktop.

## Activity and diagnostics

Local activity recording starts **off**. Turn on **Record local activity** in
First Run, Activity, or Settings when you want privacy-safe assignment history
and rotating troubleshooting logs. Turning it off stops new records without
deleting existing history; **Clear local activity** remains the explicit delete
action. Nothing is uploaded, and crash reporting is independent.

## Privacy

DesktopShift makes no network connections. Nothing it records leaves your
device.

By default, a rule knows only which application a window belongs to. It matches
on the process name, the package family name, the app ID, and the executable
path. When you create a rule from the Activity page, only those are filled in.

You **can** add a window title or a command line to a rule yourself, as an extra
condition. This is opt in. It is there so you can separate two profiles of the
same app. Nothing adds them for you.

Whatever your rules use, window titles, command lines, and profile paths are
never written to local activity or included in a diagnostic bundle.

Settings, rules, and any recorded activity live under
`%LOCALAPPDATA%\DesktopShift`.

## Building

The solution needs real MSBuild, because it contains a C++ project.
`dotnet build` will not work.

```bash
msbuild DesktopShift.slnx -p:Platform=x64
msbuild DesktopShift.slnx -p:Platform=ARM64
```

Release packaging produces separate `x64` (AMD64) and `arm64` MSIX files; each
contains a native bridge compiled for the matching processor architecture. See
[docs/release/release-process.md](docs/release/release-process.md) for the full
release and submission process.
