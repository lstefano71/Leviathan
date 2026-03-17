# Leviathan Theme Guide

This guide covers everyday theme workflows in Leviathan GUI: importing/exporting themes, editing with live preview, and safely managing built-in vs user themes.

## Theme locations

- **Built-in themes** ship with Leviathan and are always available.
- **User themes** are stored as `.json` files in a `themes` folder next to the Leviathan executable.
  - Example: if `Leviathan.GUI.exe` is in `C:\Apps\Leviathan\`, user themes are in `C:\Apps\Leviathan\themes\`.

## Import a theme

1. Open **Theme → Import Theme...**.
2. Choose a `.json` file.
3. Leviathan imports it and adds it to your theme list.

Notes:
- If the incoming theme ID or name conflicts with an existing theme, Leviathan automatically adjusts the imported ID/name so both themes can coexist.
- Invalid JSON theme files are rejected.

## Export the current theme

1. Activate the theme you want to export.
2. Open **Theme → Export Current Theme...**.
3. Choose destination and filename, then save.

Use this to back up your theme or share it with others.

## Open and use Theme Editor

Open from:
- **Theme → Theme Editor...**
- or **Command Palette** (`Ctrl+P`) → `Theme Editor...`

Inside the editor:
- Pick a theme from the list.
- Edit **ID**, **Name**, and **Base Variant** (Dark/Light).
- Edit colors with:
  - **Pick** (color picker)
  - direct value entry
  - **Reset** per color slot
- Use **Duplicate** to create a user-editable copy of a built-in theme.

## Live preview, Apply, Save, Cancel

- As you edit valid values, Leviathan shows a **live preview** immediately.
- **Apply**: commits the preview for the current session only.
- **Save**: writes/updates a user theme file in the `themes` folder and persists it as your selected theme.
- **Cancel**, `Esc`, or closing the dialog: discards uncommitted preview changes and restores the last committed theme.

## Compact Preview panel

The **Compact Preview** panel is a quick visual check for:
- header + gutter
- primary/secondary/muted text
- selection + cursor highlight + cursor bar
- row/column stripes
- grid lines
- match + active-match highlighting

Use it to verify readability and contrast before applying/saving.

## Built-in vs user themes (safety)

- Built-in themes are **immutable**:
  - cannot be overwritten
  - cannot be renamed
  - cannot be deleted
- User themes are fully manageable:
  - save updates
  - rename
  - delete
  - import/export

## Theme JSON basics

Theme files are JSON with the main fields:
- `id`
- `name`
- `base` (`"dark"` or `"light"`)
- `colors` (color-slot dictionary)

Color values support common formats such as:
- `#RRGGBB`
- `#AARRGGBB`
- `rgba(r,g,b,a)`

## Quick tips

- Start from a built-in theme and **Duplicate** before customizing.
- Keep IDs simple (letters, digits, `-`) for portability.
- Export themes before large edits if you want easy rollback points.

---

## Creating a theme from scratch

You can write a theme JSON file by hand without opening Leviathan at all.

### Minimal example

```json
{
  "id": "my-theme",
  "name": "My Theme",
  "base": "dark",
  "colors": {
    "textPrimary":          "#E8E8E8",
    "textSecondary":        "#A0A8B0",
    "textMuted":            "#606878",
    "background":           "#1E1E2E",
    "selectionHighlight":   "#3A3A5A",
    "cursorHighlight":      "#505070",
    "cursorBar":            "#7070FF",
    "gridLine":             "#2E2E3E",
    "headerBackground":     "#252535",
    "headerText":           "#C8C8D8",
    "gutterBackground":     "#1A1A2A",
    "matchHighlight":       "#5A4000",
    "activeMatchHighlight": "#B07800",
    "rowStripe":            "#00000010",
    "columnStripe":         "#FFFFFF08"
  }
}
```

### Color slot reference

| Key | Purpose |
|---|---|
| `textPrimary` | Main content text (hex bytes, text characters) |
| `textSecondary` | Address / offset column text |
| `textMuted` | ASCII panel, decorators, dimmed labels |
| `background` | View background fill |
| `selectionHighlight` | Selected byte / character range background |
| `cursorHighlight` | Cursor cell / character background |
| `cursorBar` | Thin cursor-line left-edge indicator |
| `gridLine` | Hex grid separator lines |
| `headerBackground` | Column header row background |
| `headerText` | Column header text |
| `gutterBackground` | Line number gutter background |
| `matchHighlight` | Non-focused search match background |
| `activeMatchHighlight` | Currently focused search match background |
| `rowStripe` | Alternating even-row tint (use low-alpha for subtlety) |
| `columnStripe` | Alternating even-column tint (use low-alpha for subtlety) |

### Color value formats

All color values are strings. Supported formats:

| Format | Example | Notes |
|---|---|---|
| `#RRGGBB` | `#1E1E2E` | Fully opaque |
| `#AARRGGBB` | `#801E1E2E` | Alpha in the first byte |
| `rgba(r,g,b,a)` | `rgba(30,30,46,0.5)` | Alpha 0.0–1.0 |

### `base` field

Set `"base"` to `"dark"` or `"light"`. This tells the theme engine which built-in fallback palette to use for any color slot that is invalid or missing. It also controls the Avalonia window chrome (title bar, scroll bars) on platforms that respect the OS theme variant.

### `id` field

Must be unique across all loaded themes. Use only letters, digits, and `-`. Leviathan auto-adjusts IDs on import if there is a conflict.

---

## Installing a theme manually

If you just want to drop a theme file into Leviathan without using the import dialog:

1. Locate the folder that contains `Leviathan.GUI.exe` (or `Leviathan.GUI` on Linux/macOS).
2. Create a `themes` sub-folder next to the executable if it doesn't exist yet.
3. Copy your `.json` file into that folder.
4. Restart Leviathan (or use **Theme → Import Theme...** to load it without restarting).

The file name does not matter — Leviathan identifies themes by the `id` field inside the JSON.

**Example layout:**

```
C:\Apps\Leviathan\
├── Leviathan.GUI.exe
└── themes\
    ├── my-theme.json
    └── colleague-theme.json
```
