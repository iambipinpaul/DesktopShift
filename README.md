# DesktopShift

Automatically assign application windows to managed Windows Virtual Desktops.

DesktopShift runs in the tray. You write rules that say which app belongs on
which desktop. After that, windows go where they belong on their own.

This page explains what the app does, in plain words. It is meant to be read
before you use it, so nothing it does comes as a surprise.

## The one idea to remember

**Your screen follows a window only when you asked for that window.**

If you open an app, you want to see it, so DesktopShift takes you to it.
If a window appears on its own, you did not ask for it, so DesktopShift moves
it quietly and leaves you where you are.

## When a window opens

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

## When you use a window that is already open

| What happens | Where the window goes | Does your screen move? |
| --- | --- | --- |
| You click an old window. A rule names it. It is already on the right desktop. | It stays | No. You are already there. |
| You click an old window. A rule names it. It is on the wrong desktop. | To the desktop in the rule | **Yes** |
| You click an old window. No rule names it. | It stays. It is not moved. | No |

The last row is on purpose. A window must never jump away while you are
clicking it.

## Everything else

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
**IDE Development**, **Agent Development**, **Infrastructure**, then **Remote**.
“Default” is the existing first Windows desktop; DesktopShift creates and owns
only the five managed desktops that follow it.

DesktopShift ships with one grouped **Anywhere** default for File Explorer and
Notepad. Windows-managed surfaces such as Settings, credential prompts, and
passkey prompts are shown separately on the Rules page and are always left
where Windows opens them. Everything else is your decision.
When a window is swept to the first desktop, the Activity page offers a
**Create a rule for this app** button on that row.

Local activity recording starts **off**. Turn on **Record local activity** in
First Run, Activity, or Settings when you want privacy-safe assignment history
and rotating troubleshooting logs. Turning it off stops new records without
deleting existing history; **Clear local activity** remains the explicit delete
action. Nothing is uploaded, and crash reporting is independent.

### The switch setting on each rule

Each rule has its own setting for this, in the rule editor.

| Setting | What it means |
| --- | --- |
| **Never switches desktop** | The window moves. Your screen never follows it. |
| **Switches on foreground activation** | Your screen follows whenever you make the window active. |
| **Switches on a new window's first activation** | Your screen follows only the first time, when the window is new. |

## Privacy

By default, a rule knows only which application a window belongs to. It matches
on the process name, the package family name, the app ID, and the executable
path. When you create a rule from the Activity page, only those are filled in.

You **can** add a window title or a command line to a rule yourself, as an extra
condition. This is opt in. It is there so you can separate two profiles of the
same app. Nothing adds them for you.

Whatever your rules use, window titles, command lines, and profile paths are
never written to local activity or included in a diagnostic bundle.

## Building

The solution needs real MSBuild, because it contains a C++ project.
`dotnet build` will not work.

```bash
msbuild DesktopShift.slnx -p:Platform=x64
msbuild DesktopShift.slnx -p:Platform=ARM64
```

Release packaging produces separate `x64` (AMD64) and `arm64` MSIX files; each
contains a native bridge compiled for the matching processor architecture.
