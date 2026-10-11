# Music Station — design system

Music Station is a Windows desktop app (WPF/C#) for live pitch detection, a polyphonic piano roll, multi-track playback and file analysis. The look is a dark studio desk: quiet grey layers, one warm amber for action, one cool blue for pitch. This system holds the exact values of the Home, Live Tuner, File Analysis and Settings mockups (1920 × 1080) and the rules agreed while building them.

## Content

- Plain, short labels: "New project", "Open piano roll", "Stop listening", "Load file". Sentence case, no exclamation marks, no emoji.
- Unknown data stays a placeholder in brackets: [Project name], [Input device], [Sample rate], [File name]. Never invent names, stats, device names or colours in a mockup.
- Numbers always carry their unit in `ink-subtle`: 440.51 Hz, 120 BPM, −3.0 dB, +2 cents, 2800 ms. Use the real minus sign (−) and "#" for sharps (C#3): IBM Plex has no ♯, and a fallback glyph breaks the even mono columns.
- Empty readouts show "—" plus the unit; the big note shows "No pitch" rather than a giant dash.
- Settings apply instantly; say "Changes apply straight away" instead of adding a Save button.

## Colour

One dark theme. Build depth with the grey layers, not shadows: `bg` (window, wells) → `bg-chrome` (header, tool bars, rulers) → `surface` (panels, session bar) → `surface-raised` (secondary buttons, selected rows) → `surface-active` (hover, pressed toggles). Outline panels with `border-subtle`, controls with `border`, bar lines with `border-strong`. `ink-faint` is decorative only.

- `accent` (amber) means act or MIDI: primary buttons, Play, the active track's notes, the tuning needle, the active tab bar, a key while its note sounds. One amber button per panel or bar. Text on it is `on-accent`, never white.
- `pitch` (blue) means pitch or selection: the in-tune band, the detected key and its row (`pitch-lane`), selected notes (outlined `selection-ring`), selected table rows, audio tracks, meters, focus rings, Solo on. Blue text on dark is `pitch-text`; blue pills, cards and selected rows sit on `pitch-well` with `pitch-edge` dividers.
- `record` and `mute` are states only. `record` also marks quiet destructive text buttons (Remove selected plugin). Muted tracks dim their clips to 40%.
- Each track has a colour from `track-1`…`track-8`, assigned in order and changeable. The selected track's notes use its colour; other tracks show as faint ghost notes in their colours (22%), not editable. Selected notes are always `pitch`, whatever the track colour.
- Amber and blue differ in lightness as well as hue, so MIDI and audio stay distinguishable without colour vision.

## States

- Hover: one layer up (`surface-raised` → `surface-active`); inputs, combo boxes and slider rails switch their outline to `border-strong`. Primary buttons at 90% opacity.
- Pressed: secondary buttons sink to `bg`; primary at 80%.
- Focus: 2px `pitch` outline offset 2px outside the control. Buttons, toggles, combo boxes and sliders show it on keyboard focus only; text inputs whenever focused.
- Disabled: 40% opacity. Controls that depend on an off setting stay visible, disabled and indented.
- Toggles on: `surface-active` with a `border-strong` outline. Solo on: `pitch` fill, `on-pitch` label. Mute on: `mute` fill. Record-armed: `record` fill; both labels `on-accent`.
- Faders (track volume, mixer, master) never jump to a clicked point; other sliders may.

## Type

IBM Plex Sans for the interface, IBM Plex Mono for every number, note name and unit, both from Google Fonts (static TTFs in the app). Sans: `display` for one panel headline at most, `title` for the app name, `body` for descriptions and names, `label` for form labels and tools, `caption` in dense rows. Mono: `readout-hero` for the detected note, `readout` for Hz/cents tiles, `readout-sm` for the transport position, `value` in inputs, `overline` (uppercase, `ink-subtle`) for panel section labels, `meta` for dB, pan, ms and metadata. WPF has no letter-spacing: type overlines in capitals.

## Layout patterns

- Navigation header, 72px, on every screen: app mark and name, then tabs (Home, Live Tuner, File Analysis, Settings; active tab in `ink-strong` with a 3px `accent` bar at the bottom), then [Input device] · [Sample rate], then the listening pill. The pill is status only, fixed at 260px so it never shifts: Listening + note + "in tune", Listening + "No pitch", or Not listening on `surface-raised`.
- Session bar, 72px: record or load on the left, one transport in the centre (Rewind, Play, Stop, Loop, position in bar.beat.step), then Tempo ([120] BPM with ▲▼, 40–240, and a fixed "4/4"), then Export ▾. Live Tuner alone adds a Metronome toggle; with it on, Record starts after a 1-bar count-in. Each tab has exactly one Play, and it always plays that tab's notes.
- Stop: the first press returns the playhead to the loop start (or 0 with no loop set); a second press returns it to 0.
- Tempo changes keep notes on their beats: playback gets faster or slower, pitch is unchanged.
- Tuner panel under the session bar in Live Tuner and File Analysis: big note, Detected / Target / Offset tiles and the ±50 cent meter, with Collapse tuner and a drag handle to resize. Collapsed it becomes a 48px strip (status, compact note, Expand tuner). Open by default in Live Tuner, collapsed in File Analysis.
- Editor tool bar, 56px: chord tools (disabled with no selection), Undo (enabled whenever there is history), Undo and Redo (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z; they cover every edit including cut, paste and delete), then the edit tools as one icon group, one active at a time: Select (V: drag a box, Shift-click to add, drag a body to move, drag edges to resize), Draw (D), Erase (E), Split (S: click a note to split it at that point). Then Grid: Bar, 1/2, 1/4 (beat), 1/8, 1/16, 1/32, 1/8T, 1/16T, Off (default 1/16). The grid sets the snap for the time position, moving, resizing and splitting, the length of a drawn note, and the grid lines. "?" help popover on the right. "Cut" only ever means the clipboard.
- Piano roll: ruler in bars and beats only (bar numbers, beat ticks); grid lines every beat (`border-subtle`) and stronger every bar (`border-strong`). Loop: draggable S and E handles on the ruler; with Loop on they are `accent` with an `accent` wash over the region, with Loop off they are grey. Marquee selection: dashed `pitch` box.
- Clipboard: Ctrl+C copy, Ctrl+X cut, Ctrl+V paste, Delete, Ctrl+A select all. Paste puts the earliest copied note at the selected time position (the playhead, set by clicking the ruler or empty grid), keeps pitches and spacing, and leaves the pasted notes selected. Paste is unavailable during playback.
- Right-click menu on notes or the grid: Cut, Copy, Paste at [position], Delete (in `record`), a divider, Select all; each item shows its shortcut in `ink-subtle` mono on the right, and unavailable items are disabled. Menu: `surface-raised`, `border` outline, `radius-md`+2, the only element allowed a drop shadow.
- Key column: 72px wide, rows 24px, every key labelled; black keys 26px shorter. Detected key: solid `pitch`. Selected notes' keys: 4px `pitch` bar on the grid edge. Hover: lighter key plus speaker icon. Sounding (playback, a clicked key, or a MIDI keyboard): `accent` key, and the sounding notes get an `ink-strong` outline. Pitch-class colour: a 6px strip on the outer edge when the setting is on.
- Tracks panel (left, 300px, both editing tabs): one card per track with colour swatch, name, M / S, instrument picker, volume slider (no jump-to-click) and pan. The selected card is `surface-raised` with a `border-strong` outline; a muted card drops to 60%. Right-click a card for Rename, Colour, Delete; double-click the name to rename. "+ Add track" (dashed) at the bottom. In Live Tuner every Record adds a new take track and selects it.
- Drum tracks: the key column widens to 150px and lists only the drum sounds in use, by name with the note on the right; hits are short fixed blocks. The instrument picker lists drum kits only.
- MIDI loading: the Load MIDI parts dialog lists every part with its note count, its future track colour and the auto-picked instrument (channel 10 = drum kit); each ticked part becomes a track. Primary button reads "Load N tracks".
- Scrolling in the piano roll: mouse wheel scrolls horizontally, Ctrl + wheel vertically; ← → and ↑ ↓ always scroll, never move notes. Both scrollbars are visible.
- Side panels 440px, collapsible. Tables show 4 key columns; the clicked row expands into detail tiles.
- Settings: section list 280px on the left, one section at a time on the right; label column 260px, control, info icon.

## Space and shape

Spacing runs `space-1` 8 → `space-5` 32. Panels sit `space-4` apart; header and bars pad `space-5` at the sides. Corners: `radius-lg` panels, `radius-md` buttons and inputs, `radius-sm` clips, notes and M/S/R buttons.

## Iconography and mark

Icons are inline stroke drawings, 1.8–2.2px, round caps and joins, in `currentColor`; transport icons are solid. Icon-only buttons carry an accessible name. The app mark is five rounded vertical strokes of rising and falling height in `accent`; it has no file yet, so draw it inline and do not stretch it. The window title reads "Music Station".

## Accessibility

Controls are real buttons, inputs and selects. Main buttons are 40–52px tall. M/S/R buttons (26px) are below the 44px touch guideline, accepted for a mouse-driven desktop app. Text tokens pass 4.5:1 on `bg`, `surface` and `surface-raised`; `ink-faint` does not and is never used for text that must be read.
