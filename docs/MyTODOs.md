Avalonia Front-end is in src/Leviathan.GUI which depends on src/Leviathan.Core.
Avalonia docs here: https://docs.avaloniaui.net/docs/welcome
Avalonia standard controls: https://docs.avaloniaui.net/controls
Avalonia standard API: https://docs.avaloniaui.net/api/avalonia


[x] avalonia front-end: it would be cool if the hex view had a fixed header on the two parts of the display with the relative offsets from the address in the gutter.

[x] avalonia front-end: horizontal scroll bars (csv view and text view)

[ ] avalonia front-end: hex view unexpectedly slow when scrolling down

[x] avalonia front-end: text search. are there ways to speed it up? on very large files it's still quite slow. Also: is it possible to add regex match in addition to the already present case sensitive match?

[x] avalonia front-end: csv view. search as once again stopped jumping to the right row/column? also: it would be cool if I could hide and show columns. I don't know if this fact should be reflected in the details. Also, could you reduce the padding of the details but maybe make the color of the detail fields striped to make it easier to read? Maybe stripe the columns in the main view as well? Also: do the search matches get highlighted in the details? if not, that would be a nice addition. 

[x] avalonia front-end, csv view. hiding a column or showing it with the detail panel opened does not refreshes the details panel immediately.

[x] avalonia front-end: now that we have streaming search, do we still need to invalidate the search results when the file changes? can we fix them or rerun the search? also, it would seem that the streaming does not actually apply to the view as the matches are found? it seems that the view only updates after the whole file has been searched. if that's the case, it would be nice if the view updated as the matches are found, so that I can start looking at them right away instead of waiting for the whole search to finish.

[x] avalonia front-end, hex view: it would be nice if I could select a range of bytes and then copy them in hex or text format. ImHex has a very nice selection visual. Maybe also add a "copy as hex" and "copy as text" right click options to the details when a range is selected? selection should work with the usual conventions (shift + movement, shift + click, click + drag). Same in the text view if it's not already implemented. In the text view, deleting a selection should also work with the usual conventions (backspace, delete, etc). At the moment "Del" only deletes the byte under the cursor, which is a bit weird.

[x] avalonia front-end: the "unsaved changes" dialog does not get the focus, does not show the file name, and does not have visible shortcuts to the "save" and "discard" buttons. It would be nice if it did all of those things. "Esc" should cancel the dialog and "Enter" should save the file. Also: maybe the "discard" button should be red to make it more clear that it's a destructive action? also: why all the white margin below the buttons?

[x] avalonia front-end: right-click behaviour throughout the app. let's think about it together. In the hex view, right-clicking on a byte should open a context menu with options like "copy as hex", "copy as text", "go to offset", "add bookmark", etc. In the text view, right-clicking on a character should open a context menu with options like "copy", "go to line", "add bookmark", etc. In the details view, right-clicking on a field should open a context menu with options like "copy value", "go to offset", etc. In the csv view, right-clicking on a cell should open a context menu with options like "copy value", "hide column", etc.  What else?

[ ] avalonia front-end: it would be nice if I could customize the keyboard shortcuts. Maybe also add some default shortcuts for common actions (like "save", "open", "go to offset", etc)?

[x] avalonia front-end: themes. maybe there should be an "import/export theme" command?  Maybe there should also be a theme editor within leviathan itself? or is that an overkill?

[x] can you make sure the avalonia front-end has its version via GitVersioning like the TUI2, and gets compiled, tested and released by the github action pipelines along with the TUI2? what's the best practice for projects which release multiple different separate artifacts? Also: could you check if the release notes are up to the task? I noticed a sad "click here for full changelog" but maybe a list of main items inlined would help a returning user? Also: I think that a push to a branch different than main should trigger a pre-release instead of a full release. But I might want to test the exes of a pre-release manually. 

[x] avalonia front-end: a bit of attention to the spacing of the gutter. It looks a bit cramped at the moment. Maybe add some padding between the gutter and the views? Also: maybe add a vertical line to separate the gutter from View? also: the gutter should be able to be hidden and shown. And maybe the file offset in the hex view should also be displayable in decimal?

[x] avalonia front-end: the status bar at the bottom could contain better information but also have fields whose size remains a bit more constant. At the moment they widen and shrink during indexing and so on.

[ ] avalonia front-end: multiple file support. It would be nice if I could open multiple files in different tabs? 

[x] avalonia front-end: it would be nice if I could open a file in the hex view and then open the same file in the text view in a different tab, and have them synchronized. So that if I scroll in one view, the other view scrolls to the same position. This would be very useful for large files where I want to see both the hex and the text representation at the same time.

[x] avalonia front-end: status bar toggling of options (like the encoding). It would be nice if I could click on the encoding in the status bar and have a dropdown menu to select a different encoding. Same for other bits of info in the status bar.

[x] avalonia front-end: read-only mode to guarantee we don't accidentally modify a file. Maybe also a "safe mode" where the app starts in read-only mode and then you can explicitly enable editing if you want to?

[x] avalonia front-end: F1 should also include a clickable link which would open an external browser pointing to a help page on the Leviathan origin repo. The page does not exist yet and needs to be created as a markdown file in the docs folder. Maybe also link on the welcome page?

[x] avalonia front-end: no ctrl-p/command palette on welcome page, I think

[ ] avalonia front-end and core: refactoring and complete code review. keep the code a bit DRY-er. Find places where there is duplicated code and try to abstract it away. Anything that makes sense moving to the core should be moved to the core. Some files might benefit from being split into smaller files.

[ ] avalonia front-end: search and replace?

[ ] avalonia front-end: revision of the command palette. Maybe some options should open sub menus (like: encoding or bytes per row?) instead of being inline in the command palette. 

[x] avalonia front-end: it would be nice if the command palette had a "recently used" section at the top, so that I can quickly access the commands I use most often.

[x] avalonia front-end: the edit > copy/paste/cut should have standard keyboard shortcuts.

[x] avalonia front-end: in the welcome view, pin works but unpin does not.

[x] avalonia front-end, csv view: what's the point of having in the status bar R x/n and then another fields with "rows: n"? One of them shows when it's approximate, the other doesn't. But apart from that... 

[ ] can you update the readme and the deep dives with all the new stuff? In particular, but not exclusively, the new avalonia front-end. For instance: a document on how to create new themes and how to install them? The emphasis has now moved to the GUI front-end (no need to scare away the users by mentioning Avalonia).
