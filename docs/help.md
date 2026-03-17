# Leviathan GUI Help

Leviathan is a large-file editor with Hex, Text, and CSV views.

## Quick start

- Open a file with `Ctrl+O`, or **drag and drop** a file onto the window, or pick a recent file from the Welcome Screen.
- Switch views with `F5` (Hex), `F6` (Text), `F7` (CSV) — or click the **Hex / Text / CSV** tabs at the top of the view.
- Search with `Ctrl+F`, then use `F3` / `Shift+F3` to jump matches.
- Jump to location with `Ctrl+G`.
- Save with `Ctrl+S`.
- Undo / redo with `Ctrl+Z` / `Ctrl+Y`.

## Editing and safety

- Toggle **Read-only Mode** from the `Edit` menu or command palette.
- Enable **Start in Read-only** to always open sessions with editing locked.
- **Undo / Redo** — multi-level edit history. `Ctrl+Z` to undo, `Ctrl+Y` to redo.

## View options

### Hex view
- **Bytes per row** — choose Auto, 8, 16, 24, 32, 48, or 64 via `View → Bytes per Row` or by clicking the row-count indicator in the status bar.
- **Gutter** — toggle the line-number gutter via `View → Gutter`.
- **Decimal offsets** — toggle hex/decimal offset display via `View → Decimal Offsets`, or click the offset value in the status bar.

### Text view
- **Word wrap** — toggle via `View → Word Wrap`.
- **Encoding** — switch between UTF-8, UTF-16 LE, and Windows-1252 via `View → Encoding` or by clicking the encoding indicator in the status bar.

### CSV view
- **Column visibility** — show or hide specific columns via `Edit → Column Visibility...` or the command palette.
- **Row deletion** — delete selected rows via `Edit → Delete Rows`.
- **CSV dialect** — configure delimiter via `Edit → CSV Settings...`.

## Font

- `View → Select Font...` opens a font picker.
- `Ctrl+=` increases font size, `Ctrl+-` decreases it.

## Command palette

- Open with `Ctrl+P` when a file is open.
- Use it for view switches, encoding changes, bytes/row, themes, read-only toggles, and more.
- Recently-used commands appear at the top.

## Welcome screen

When no file is open, Leviathan shows a Welcome Screen listing:
- **Recent files** — the last 20 files opened.
- **Pinned files** — files you have pinned for permanent quick access.

Click any entry to open it, or use `Ctrl+O` / drag-and-drop to open a new file.

## Status bar

The status bar at the bottom is interactive — click a field to act on it:

| Field | Click action |
|---|---|
| Encoding indicator | Opens encoding switcher menu |
| View mode indicator | Opens view mode switcher menu |
| Row / line count | Toggles word wrap (Text) or bytes-per-row menu (Hex) |
| Offset indicator | Toggles decimal / hex offset display (Hex) |

## Themes

### Where themes are stored

- **Built-in themes** are bundled with Leviathan and are read-only.
- **User themes** are JSON files stored in the `themes` folder next to the Leviathan executable.
- If the folder does not exist yet, it is created the first time you save or import a user theme.

### Import and export

- Open the **Theme** menu and choose:
  - **Import Theme...** to load a `.json` theme file.
  - **Export Current Theme...** to save the currently active theme to a `.json` file.
- You can run the same actions from the Command Palette (`Ctrl+P`) by searching for those command names.
- If an imported theme conflicts with an existing theme name/ID, Leviathan keeps both by generating a unique name/ID.

### Theme Editor (advanced editor)

- Open it from **Theme → Theme Editor...** (or via Command Palette).
- Select a theme on the left, then edit:
  - Theme **ID**, **Name**, and **Base Variant** (Dark/Light)
  - Individual color slots using **Pick**, direct text input, or **Reset**
- To customize a built-in theme, start with **Duplicate** and edit the new user copy.

### Live preview, Apply, Save, Cancel

- Valid changes are previewed live while the editor is open.
- **Apply** updates the current session only (quick try-out, not persisted).
- **Save** writes the theme to the user `themes` folder and persists it as your selected theme.
- **Cancel**, `Esc`, or closing the editor discards uncommitted preview changes and restores the previously committed theme.

### Compact Preview panel

- The **Compact Preview** panel is a mini mockup of header, gutter, text, selection/cursor, stripes, grid lines, and search highlights.
- Use it to quickly verify contrast/readability before applying or saving.

### Safety notes (built-in vs user themes)

- Built-in themes are immutable: you cannot overwrite, rename, or delete them.
- User themes are editable and can be saved, renamed, deleted, imported, and exported.
- Theme IDs must be unique and use letters, digits, or `-`.

For a step-by-step guide and JSON examples, see **[Theme Guide](themes.md)**.

## Keyboard shortcuts

- `Ctrl+O` Open file
- `Ctrl+S` Save
- `Ctrl+Z` Undo
- `Ctrl+Y` Redo
- `Ctrl+F` Find
- `Ctrl+G` Go to offset/line
- `Ctrl+P` Command palette (file-open mode)
- `Ctrl+X / Ctrl+C / Ctrl+V` Cut / Copy / Paste
- `Ctrl+Insert / Shift+Insert / Shift+Delete` Copy / Paste / Cut (legacy Windows bindings)
- `Ctrl+A` Select all
- `Ctrl+Left / Ctrl+Right` Previous / next word (Text view)
- `Ctrl+Shift+Left / Ctrl+Shift+Right` Extend selection by word (Text view)
- `Ctrl+Backspace / Ctrl+Delete` Delete previous / next word chunk (Text view)
- `Ctrl+=` / `Ctrl+-` Increase / decrease font size
- `Ctrl+W` Close file
- `Ctrl+Q` Quit
- `F1` Keyboard shortcuts + link to this page
- `F5` Switch to Hex view
- `F6` Switch to Text view
- `F7` Switch to CSV view
- `F3 / Shift+F3` Next / previous search match
