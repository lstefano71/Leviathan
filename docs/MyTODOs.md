


[x] avalonia front-end: it would be cool if the hex view had a fixed header on the two parts of the display with the relative offsets from the address in the gutter.

[ ] avalonia front-end: horizontal scroll bars (csv view and text view)

[ ] avalonia front-end: hex view unexpectedly slow when scrolling down

[ ] avalonia front-end: text search. are there ways to speed it up? on very large files it's still quite slow. Also: is it possible to add regex match in addition to the already present case sensitive match?


[ ] avalonia front-end: csv view. it would be cool if I could hide and show columns. I don't know if this fact should be reflected in the details. Also, could you reduce the padding of the details but maybe color the background of the detail fields striped to make it easier to read? Also: could you add a "copy value" button to the details? Also: do the search matches get highlighted in the details? if not, that would be a nice addition. 

[ ] avalonia front-end, hex view: it would be nice if I could select a range of bytes and then copy them in hex or text format. ImHex has a very nice selection visual. Maybe also add a "copy as hex" and "copy as text" right click options to the details when a range is selected? selection should work with the usual conventions (shift + movement, shift + click, click + drag). Same in the text view if it's not already implemented. In the text view, deleting a selection should also work with the usual conventions (backspace, delete, etc). At the moment "Del" only deletes the byte under the cursor, which is a bit weird.

[ ] avalonia front-end: the "unsaved changes" dialog does not get the focus, does not show the file name, and does not have visible shortcuts to the "save" and "discard" buttons. It would be nice if it did all of those things. "Esc" should cancel the dialog and "Enter" should save the file. Also: maybe the "discard" button should be red to make it more clear that it's a destructive action?

[ ] avalonia front-end: right-click behaviour throughout the app. let's think about it together. In the hex view, right-clicking on a byte should open a context menu with options like "copy as hex", "copy as text", "go to offset", "add bookmark", etc. In the text view, right-clicking on a character should open a context menu with options like "copy", "go to line", "add bookmark", etc. In the details view, right-clicking on a field should open a context menu with options like "copy value", "go to offset", etc. In the csv view, right-clicking on a cell should open a context menu with options like "copy value", "hide column", etc.  What else?

[ ] avalonia front-end: it would be nice if I could customize the keyboard shortcuts. Maybe also add some default shortcuts for common actions (like "save", "open", "go to offset", etc)?

[ ] avalonia front-end: themes. maybe there should be an "import/export theme" command?  Maybe there should also be a theme editor within leviathan itself? or is that an overkill?

[ ] can you make sure the avalonia front-end has its version via GitVersioning like the TUI2, and gets compiled, tested and released by the github action pipelines along with the TUI2? what's the best practice for projects which release multiple different separate artifacts? Also: could you check if the release notes are up to the task? I noticed a sad "click here for full changelog" but maybe a list of main items inlined would help a returning user?


[ ] can you update the readme and the deep dives with all the new stuff? In particular, but not exclusively, the new avalonia front-end. For instance: a document on how to create new themes and how to install them? 
