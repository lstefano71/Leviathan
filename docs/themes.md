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
