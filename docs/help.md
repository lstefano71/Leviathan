# Leviathan GUI Help

Leviathan is a large-file editor with Hex, Text, and CSV views.

## Quick start

- Open a file with `Ctrl+O`.
- Switch views with `F5` (Hex), `F6` (Text), `F7` (CSV).
- Search with `Ctrl+F`, then use `F3` / `Shift+F3` to jump matches.
- Jump to location with `Ctrl+G`.
- Save with `Ctrl+S`.

## Editing and safety

- Toggle **Read-only Mode** from the `Edit` menu or command palette.
- Enable **Start in Read-only** to always open sessions with editing locked.

## Command palette

- Open with `Ctrl+P` when a file is open.
- Use it for view switches, encoding changes, bytes/row, themes, and read-only toggles.

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
- `Ctrl+F` Find
- `Ctrl+G` Go to offset/line
- `Ctrl+P` Command palette (file-open mode)
- `Ctrl+X / Ctrl+C / Ctrl+V` Cut / Copy / Paste
- `Ctrl+Insert / Shift+Insert / Shift+Delete` Copy / Paste / Cut (legacy Windows bindings)
- `Ctrl+A` Select all
- `Ctrl+Left / Ctrl+Right` Previous / next word (Text view)
- `Ctrl+Shift+Left / Ctrl+Shift+Right` Extend selection by word (Text view)
- `Ctrl+Backspace / Ctrl+Delete` Delete previous / next word chunk (Text view)
- `Ctrl+W` Close file
- `Ctrl+Q` Quit
- `F1` Keyboard shortcuts + link to this page
