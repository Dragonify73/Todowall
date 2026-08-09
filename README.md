# TodoWall

A two-week to-do board that lives on your desktop — the days of the week across,
one week per block, editable directly from the desktop.

```
┌────────────────────────────────────────────────────────────────────────────────┐
│ ◀  Jul 27 – Aug 9  ▶  Today                             Clear done   ⚙         │
│  MON        TUE        WED        THU        FRI        SAT        SUN         │
│  27         28         29         30        ┌31┐        1 Aug      2           │
│  ✓ gym      ○ invoice  ○ dentist            │○ ship v1  ○ market               │
│  ○ email               ○ review             │○ groceries                       │
│  + add      + add      + add      + add     └+ add      + add      + add       │
│ ─────────────────────────────────────────────────────────────────────────────  │
│  MON        TUE        WED        THU        FRI        SAT        SUN         │
│  3          4          5          6          7          8          9           │
│  ○ retro    + add      ✓ service  + add      + add      + add      + add       │
└────────────────────────────────────────────────────────────────────────────────┘
```

Each week is a complete week in its own right, stacked below the last — its own
`MON…SUN` header, its own seven columns across the full width of the bar, and its own
full height. Showing a second week makes the **bar taller**; it does not divide one
bar's space in two. So a week looks and measures exactly the same whether you show
one or two, and adding a week never shrinks the one you had.

A clock rides the top edge of the screen in the same glass, shaped like a MacBook
notch, and pulls down into a month calendar. Both it and the calendar can be
turned off in Settings.

```
        ─────╮   14:47   ╭─────
             │ SAT 1 AUG │
             ╰─────⌄─────╯
```

Accordingly, **Week height** in Settings sizes a single week, not the window. Two
weeks at 310 gives a bar of about 580. If that won't fit the screen the weeks give
ground together, staying equal.

## Build

Needs the **.NET 10 SDK** — the project targets `net10.0-windows`, and the build
script checks for it before doing anything.

```powershell
.\build.ps1 -Run
```

Output lands in `.\dist\TodoWall.exe`. Add `-SelfContained` to bundle the .NET
runtime into a single exe that runs on machines without .NET installed.

## Sharing it

```powershell
.\build.ps1 -Package
```

Builds self-contained, writes a read-me alongside the exe, and zips the pair into
`.\release\TodoWall-<version>-win-x64.zip` (~60 MB). That zip is the whole thing —
the recipient unzips it and double-clicks; there is no installer and no runtime to
install. It does not touch `.\dist`, so the copy you run yourself keeps going.

Two things worth telling them, both covered in the read-me: the exe is unsigned, so
the first launch shows SmartScreen's "Windows protected your PC" (More info → Run
anyway), and a zip that came through a browser may need Properties → **Unblock**.
Signing it away costs a code-signing certificate.

## Using it

| Action | How |
|---|---|
| Add a task | Click **+ add task** under any day |
| Add several in a row | Type, press **Enter** — a fresh box opens straight away |
| Complete a task | Click its circle — it fills in, gets a ✓ and a strikethrough |
| Un-complete | Click the filled circle again |
| Edit text | Click the task text |
| Delete | Hover the task and click **✕**, or middle-click it |
| Move to another day | Right-click the task → **Move to** |
| Other weeks | **◀ / ▶** in the top strip; **Today** jumps back |
| Show/hide next week | **Show next week** in the top strip, beside **⚙** |
| Overflowing column | Scroll it — the bottom fades when there's more below |
| Wipe finished tasks | **Clear done** in the top strip |
| Open the calendar | Click the dash under the clock — hover it and it becomes an arrow |
| Jump to a week | Click any day in the calendar |
| Settings | The **⚙** button, right-click the bar or the clock, or the tray icon |
| Uninstall | Settings → **Remove** → *Uninstall TodoWall…* |

Everything saves itself; there is no save button.

## Settings

**Appearance** — dark/light theme, accent colour, tint strength, text size, week
height, vertical position (top/middle/bottom), distance from the screen edge,
side margins, corner rounding, the frosted backdrop and its blur amount,
animations, and whether to show the weekend. (One or two weeks is not here —
it's the **Show next week** button on the bar itself, since it gets flipped
far more often than a setting should be.)

The defaults are the settings this was tuned to: dark, `#FF9BE36D` accent, 0.82
tint, 14.5 pt text, 310 px per week, Middle, 78 px from the edge, 172 px side
margins, 32 px corners. **Reset layout** restores exactly that set. Defaults only apply to a
fresh install — an existing `settings.json` always wins.

**Clock** — the notch, and its pull-down calendar. Both on by default.

### The second week

**Show next week** folds the following week in and out rather than snapping the
bar to a new size. The panel grows away from whichever edge you've parked it
against — top, middle or bottom — with the week that's arriving fading in over the
back of the travel instead of being uncovered all at once.

It works the same way the calendar does, and for the same reason. The window is
cut for the *two-week* shape at all times — even showing one — and the bar is
pinned inside it against your chosen edge, so the whole transition is one
interpolated number driving one panel height and the window itself never moves.
Not once, not even at the ends: resizing a layered window hands it a new origin
before anything has been painted into the new surface, so for a frame or two
Windows shows the old picture at the new position, and a fold that began or ended
with a resize began or ended with a blink. The reserved space is fully
transparent, and a layered window passes the mouse straight through transparent
pixels, so it is in nothing's way.

Nothing inside reflows either — the weeks keep the height they were given and the
panel simply clips what there is no room for yet — and the frosted backdrop is cut
for that same two-week shape and pinned in place, so a fold neither re-blurs the
wallpaper nor lets the glass slide about under a bar that is supposed to be
sitting on it.

A week that isn't showing doesn't exist: its rows are built when you ask for it
and dropped again once it has folded away, so hiding it isn't a hidden week
quietly being laid out and rasterised on every repaint of the bar. The week you
*can* see is never rebuilt by a fold — that, at the exact moment a transition
starts or ends, is the other thing you would see as a blink.

### The clock notch

A tab moulded into the top edge of the screen, MacBook-style: rounded at the
bottom, and *inverted* at the top where the sides flare out to meet the edge. It
is a second desktop-hosted window rather than part of the bar, because the bar is
a full-width panel you can park anywhere while the notch is always centred on the
top edge.

It is not separately themed. Glass, tint, colours, corner rounding and text size
all come from **Appearance**, so it changes with the bar and never drifts out of
step with it.

Under the time is a dash. Hover the notch and it bends into an arrow; click and
the notch unfolds into the current month.

Unfolding is animated, and the window itself never moves while it happens. It is
sized for the *open* calendar at all times and the outline is a clip over it, so
opening is one interpolated number driving one nine-segment path — no per-frame
`SetWindowPos`, no re-blurring the wallpaper, no re-placing in the z-order. The
part of the window the notch isn't covering is fully transparent, and a layered
window passes the mouse straight through transparent pixels, so the reserved space
is not in anything's way. The tallest possible month is reserved too, so browsing
from February to March doesn't resize anything either. A day with tasks carries a dot —
accent if anything is still unfinished, grey once the day is cleared — and
clicking a day scrolls the bar to that week. **‹ ›** move a month at a time, and
clicking the time folds it back up.

Both widgets sit at the bottom of the window stack, below everything real, but the
clock is deliberately kept one step above the bar — so an open calendar lies *over*
the board instead of disappearing behind it.

Turning the calendar off leaves a plain clock with no dash.

### The frosted backdrop

The panel background is the slice of wallpaper behind the bar, blurred.

It's rendered from the wallpaper **file**, not grabbed off the screen. A screen
capture would bake in your desktop icons and whatever window happened to be
sitting there, and would need the bar hidden at exactly the right instant.
Reproducing Windows' own Fill/Fit/Stretch/Center/Tile/Span maths against the
source image gives the same result with none of that. It blurs at 1/6 scale with
three box passes (a cheap Gaussian approximation) and lets WPF smooth it back up,
so it costs a few milliseconds — and it only recomputes when the wallpaper, the
bar geometry or the theme actually changes, never on a routine layout pass.

TodoWall does **not** set or manage your wallpaper — it reads the image path and
fit mode live from `HKCU\Control Panel\Desktop`, the same values the Windows
Personalisation page writes. Change your wallpaper however you normally would and
the backdrop follows within a few seconds.

If no wallpaper is readable it falls back to a flat panel colour.

**Behaviour**
- *Show on* — which monitor hosts the bar.
- *When a new week starts* — carry unfinished tasks over (default), start empty,
  or copy the whole week.
- *Start with Windows*.
- *Draw behind desktop icons* — see the note below.

## Does it need administrator rights?

No — and it shouldn't be run that way. An elevated process **cannot** reparent
itself into Explorer's desktop, so the *Welded to the desktop* placement can only
ever fail while elevated. Running elevated also means "Start with Windows"
behaves differently at login (that always launches un-elevated), and an ordinary
shell can't stop the process to rebuild over it.

## How it attaches to the desktop

The bar behaves like part of the wallpaper: it never appears in Alt+Tab or the
taskbar, never covers a real window, and stays put when you press Win+D.

Three placements, in Settings → **Placement**:

- **On the desktop** (default). A plain top-level window that never activates,
  pinned to the bottom of the z-order. Explorer's desktop is always the very
  bottom window, so this lands just above the wallpaper and below every real
  window — without depending on Explorer's internals, which differ between
  Windows builds. Clickable. Draws over any desktop icons it overlaps.
- **Welded to the desktop.** Reparented into the window that hosts the desktop
  icons, as a genuine `WS_CHILD`. Sticks harder, but depends on Explorer's
  window layout and breaks when Explorer restarts. Falls back to the first
  option if it fails — including when running elevated, where it always will.
- **Behind the desktop icons.** Genuinely part of the wallpaper, under your
  icons — but **read-only**: Explorer's icon layer sits on top and swallows
  every click. Use it if you keep the board as a glanceable display and edit it
  rarely (switch placement, edit, switch back).

Typing happens in a small popup that floats over the row being edited. That is
deliberate: a desktop-child window can take mouse input but cannot reliably own
the keyboard focus, so text entry is delegated to a real top-level window.

If Explorer restarts, you change the wallpaper, or you plug in a monitor, the
bar re-attaches itself within a few seconds. **Re-attach to desktop** in the
tray menu forces it immediately.

## Where your data lives

`%APPDATA%\TodoWall\`

- `board.json` — every week you've ever filled in, keyed by Monday's date.
  Written via a temp file and atomic replace, so a crash mid-save can't shred it.
- `settings.json`
- `todowall.log` — placement and layout breadcrumbs, written only when the state
  changes (desktop hosting fails in ways that are invisible by definition)
- `error.log` — only if something went wrong

Empty weeks older than a year are pruned automatically; weeks with content are
kept forever.

## Memory

Measured on a 1920×1200 single-monitor machine, idle on the desktop:

| | before | after |
|---|---:|---:|
| Working set | 273 MB | **~12–16 MB** |
| Private (committed) | 176 MB | **~55 MB** |
| Threads | 34 | **18** |

Working set is what the app actually keeps resident; it rises while you interact
and settles back. Private bytes is the honest "how much did it commit" number.

What was actually costing the memory:

- **The GPU stack.** WPF spun up a D3D device, pulling in ~167 MB of mapped
  graphics-driver DLLs — while the bar uses `AllowsTransparency`, which WPF
  composites in **software** regardless. We were paying for a pipeline never
  used. `RenderOptions.ProcessRenderMode = SoftwareOnly` drops it at no visual
  cost. Set `"hardwareAcceleration": true` in `settings.json` to opt back in.
- **The backdrop decoded the whole wallpaper.** Producing a ~260×52 blurred
  thumbnail meant decoding 1920×1200 at 32bpp *twice* (~18 MB) through GDI+. It
  now reads only the image header for dimensions, then decodes via WIC directly
  at the size the blur needs, so a 4K wallpaper costs no more than a small one.
- **GC tuning.** Workstation, non-concurrent, `RetainVM` off — a small heap
  returned to the OS rather than hoarded, and one fewer background thread.
- **No ICU** (`InvariantGlobalization`). The day-name headers were already
  hardcoded English, so dates now just format consistently with them. This one is
  not free, though — see below.
- **Polling replaced with an event.** Staying at the bottom of the z-order meant
  walking every top-level window every 3 seconds, allocating a string per window.
  Only a window being *raised* can bury us, so it now listens for exactly that
  (`EVENT_SYSTEM_FOREGROUND`). The remaining timer does slow housekeeping at 30 s.
- **Trimming after the bursts.** Startup and backdrop rendering are the peaks;
  memory is handed back afterwards, and every 5 minutes while idle — never while
  you're hovering or typing, where the page faults would be felt.

Also fixed along the way: the log deduplicated against a single previous message,
but `host:` and `layout:` alternate, so each looked new and the file grew on every
pass. It now dedupes per message kind.

Dropping ICU had a casualty that took a while to surface: **every ComboBox in the
Settings window stopped opening.** WPF gives each element a `Language` of the OS tag
(`en-us`), and any binding needing a culture resolves it via
`XmlLanguage.GetSpecificCulture()` — which, with no ICU, cannot map `en-us` to a
non-neutral culture and throws. A combo opens by writing `IsDropDownOpen` back through
exactly such a binding. Because the dispatcher's exception handler was doing its job,
nothing crashed and nothing was visible; the dropdowns just quietly did nothing, and
the only trace was a stack in `error.log`. The element tree is now pinned to the
invariant language to match, which is consistent — `CurrentCulture` is already
invariant in this build and the UI is hardcoded English either way.

The second casualty was worse and hid the same way: **no TextBox would accept a
character** — the add/edit popup opened, took focus, and silently ignored every key.
WPF's text editor asks the keyboard layout for its culture on *every* character
(`InputLanguageManager.CurrentInputLanguage`), and it asks by LCID. Invariant mode
rejects every culture that is not the invariant one, LCID lookups included, so each
keystroke threw `CultureNotFoundException` inside `TextEditorTyping.DoTextInput`
before the character was inserted — again caught by the dispatcher handler, again
leaving nothing but a stack in `error.log`. `PredefinedCulturesOnly=false` makes that
lookup return an invariant-behaving culture instead of throwing. ICU still never
loads, so this costs nothing: the two settings are separate levers, and only the
first one is the memory one.

And the week-change transition only ever played once. `SlideIn` reset the element by
assigning `Opacity` and the translate offset directly — but the previous run's
animation was still *holding* those properties at their end values, and in WPF a held
animation outranks a local value. So the reset was silently discarded and every switch
after the first was instant. The helpers now release the property before rewinding it.

**What did *not* help:** removing the wallpaper-setting feature. It was dropped
because it went unused, and that is a fine reason — but it moved the numbers by
~1.5 MB, i.e. measurement noise. The wallpaper code retained nothing; it ran once
and returned. Memory went to the GPU stack and to *decoding* pixels, and the blur
still decodes pixels either way.

The remaining lever is dropping the WinForms dependency (pulled in only for the
tray icon) in favour of raw `Shell_NotifyIcon`. Worth roughly 10 MB of the 54,
at the cost of hand-rolling tray menu behaviour that is notoriously fiddly.

> `%APPDATA%\TodoWall\wallpaper.png` may still exist from when TodoWall managed
> wallpapers. **Don't delete it** — Windows itself now points at that file as your
> desktop wallpaper. Set a different wallpaper through Windows first if you want
> it gone.

## Troubleshooting

Two scripts sit next to the build:

- `.\diag.ps1` — dumps where the bar's window actually landed, Explorer's desktop
  window stack on this machine, the screen layout, and the tail of both logs.
- `.\shot.ps1` — renders the bar to a PNG via `PrintWindow`, so you can inspect
  it without minimising anything or capturing whatever is on top of it.

If the bar isn't visible, the tray menu's **Bring to front (troubleshoot)** pulls
it to the top of the z-order. If it appears, the window renders fine and only its
stacking is wrong; if it doesn't, the problem is the window itself.

## Notes and limits

- Windows 10/11. Run it **un-elevated** — see above.
- One monitor at a time. The bar spans the working area of the monitor you pick.
- **Move to** relocates a task within its own week only. Between weeks isn't
  supported (unfinished tasks roll forward on their own when the week turns over).
- Text entry happens in a small popup floated over the row, not inline. A window
  living at desktop level takes mouse input fine but cannot reliably own the
  keyboard focus, so typing is delegated to a real top-level window.
