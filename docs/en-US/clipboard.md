# Clipboard and file transfer

The "Clipboard and file transfer" page of the open panel has two columns: the clipboard history on the left and the file transfer on the right. Their settings are under Settings › Clipboard and Settings › File transfer; the page's switch and size are under Settings › Expanded view.

Under Settings › Page layout › Show the page can show only the clipboard or only the file transfer, which then fills the page. While only the clipboard shows, files dragged onto the panel are not added to the file transfer, and the collapsed view does not show their number. The arrangements below apply only while both columns show.

Drag the divider between the columns to change their widths; double-click it to go back to 60% for the clipboard and 40% for the file transfer. Under Settings › Page layout the columns can also be stacked as two cards, the one behind showing only a side strip with its icon, name and number of entries. Click that strip, the title of the card in front, or the page's button once more to switch cards. Three more ways of switching can be turned on under Settings › Page layout: hovering the pointer over the strip, a middle click (on by default), and a two-finger sideways swipe or Shift + wheel.

Each column can show its entries in several ways, chosen for each under Settings › Page layout and combined freely. By default the clipboard shows one line an entry and the file transfer two:

- Clipboard: one line; three lines (each entry three lines high, with up to three lines of its text at the left and, at the right, when it was copied, its characters or a picture's size, and the application it came from); blocks (in as many columns as the width holds, a picture filling its block).
- File transfer: one line; two lines (a large icon at the left, the size and the date changed under the name); grid (large icons over the names, in as many columns as the width holds); row (a single row, scrolled sideways with the wheel); column (a single column, suited to a narrow file transfer).

In the grid, the row, the column and on two lines, picture files show a thumbnail. With the pointer on a file, four buttons appear: open, show in folder, remove, and details.

## Clipboard

BrimDeck keeps the text and pictures you copy, newest first.

- **Display**: text shows its first line, with the number of further lines at the end; a link shows its domain and path in two colors, code in a monospaced font, a color value with a swatch, and a picture as a thumbnail with its size.
- **Click**: pastes the entry into the window you are working in.
- **Right-click**: Paste, Paste as plain text, Copy only, Details, Edit (text), Add to category, Add to favorites, Delete.
- **Details**: the whole panel shows the entry in full, with the app it came from, the time, and its length or size; a link can be opened in the browser.
- **Drag out**: drag the entry into another app.

### How long entries are kept

- The history is stored only on this PC, not encrypted.
- Ordinary entries are cleared when BrimDeck exits by default.
- With "Keep history after exit" on in Settings › Clipboard, ordinary entries stay after BrimDeck exits, and entries older than "Keep for" (1 day, 7 days or 30 days; 30 days by default) are deleted. The time counts from the last copy.
- What is in Favorites, or in a category set to "Kept", stays until you delete it, whatever the two settings above.
- The entries' text is held in memory, to show and search it; the formatting copied from web pages, Word and other apps, and pictures, are kept on disk and read only to paste or show them. With common content, 9,000 entries use about 30 MB of memory.
- One entry holds up to 1,000,000 characters; longer copies are not recorded.

### What is not recorded

- With "Record the clipboard" off under Settings › Clipboard, nothing is recorded.
- Copies from the apps under "Apps not recorded" are not recorded.
- Copies that an app marks as not to be kept in clipboard history, as password managers do, are not recorded.

### Favorites and phrases

Favorites is the fixed first category, and what is in it is always kept. Right-click any entry and choose "Add to favorites" to put it there.

A phrase is text you write directly, without copying it first: choose "New phrase" under Settings › Clipboard › Favorites, type it and save.

## File transfer

The file transfer holds files you will need shortly. It keeps only where each file is, never a copy. Adding a file does not change the original. "Delete the original" sends it to the Recycle Bin; with "Move" selected for dragging files out, the drop target can move the original.

- **Put in**: drag files or folders onto the notch, the capsule or the open panel. Dragged onto the collapsed panel, it opens on this page; move away without dropping and the panel goes back to how it was. You can also drop onto a category's name.
- **Click**: pastes the file into the current window. It becomes an attachment in a chat window, and a copy in the current folder in File Explorer.
- **Select several**: hold Ctrl or Shift and click to select several files, then paste, drag out or remove them together.
- **Right-click**: Paste, Open, Show in folder, Copy path, Details, Add to category, Remove from the file transfer, Delete the original.
- **Drag out**: drag a file to where you need it; a button in the column's title row drags out all files at once. Files are copied by default; with "Move" chosen under Settings › File transfer › Drag files out, the target can move the originals, while chat apps, browsers and other places that only take copies still get copies. An entry is removed automatically only when the target reports a move and a background check confirms the original is missing. If the target reports no result or the original location is temporarily unavailable, the entry stays and can be removed manually.
- A file whose original has been moved or deleted is marked "Missing".
- The notch and capsule can show how many files the file transfer holds.

## Categories and search

- The clipboard and the file transfer each have their own categories, made, renamed, reordered and deleted under Settings › Clipboard and Settings › File transfer. An entry or a file can be in several categories.
- Each category can have a rule for what goes in by itself. The clipboard's conditions cover the kind of content, the text (contains, starts with, ends with, a built-in format or a regular expression), a link's domain and the app it was copied from; the file transfer's cover the file type, extension, file name and folder. Several conditions can require any or all of them.
- A clipboard category can be set to "Kept"; otherwise what is in it is cleared with the rest of the history, and its label shows "Cleared on exit" or the length kept.
- The search button at the top right of the page searches the clipboard and the file transfer together.

## Where the data is stored

The clipboard history, the file transfer's list and the categories are stored in `%LOCALAPPDATA%\BrimDeck\shelf.dat`, and copied pictures and formatting in `%LOCALAPPDATA%\BrimDeck\shelf\`. Both are not encrypted, used only on this PC and never uploaded. See [Data and privacy](privacy.md).
