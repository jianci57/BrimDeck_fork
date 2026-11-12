# Clipboard and file transfer

The "Clipboard and file transfer" page of the open panel has two columns: the clipboard history on the left and the file transfer on the right. Their settings are under Settings › Clipboard and Settings › File transfer; the page's switch and size are under Settings › Expanded view.

## Clipboard

BrimDeck keeps the text and pictures you copy, newest first.

- **Display**: text shows its first line, with the number of further lines at the end; a link shows its domain and path in two colors, code in a monospaced font, a color value with a swatch, and a picture as a thumbnail with its size.
- **Click**: pastes the entry into the window you are working in.
- **Right-click**: Paste, Paste as plain text, Copy only, Details, Edit (text), Add to category, Add to favorites, Delete.
- **Details**: the whole panel shows the entry in full, with the app it came from, the time, and its length or size; a link can be opened in the browser.
- **Drag out**: drag the entry into another app.

### How long entries are kept

- The history is stored on this PC, encrypted by Windows.
- Ordinary entries are cleared when BrimDeck exits.
- What is in Favorites, or in a category set to "Kept", stays until you delete it.
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
- A clipboard category can be set to "Kept" or "Cleared on exit".
- The search button at the top right of the page searches the clipboard and the file transfer together.

## Where the data is stored

The clipboard history, the file transfer's list and the categories are stored in `%LOCALAPPDATA%\BrimDeck\shelf.dat`, and copied pictures in `%LOCALAPPDATA%\BrimDeck\shelf\`. Both are encrypted for the current Windows user, used only on this PC and never uploaded. See [Data and privacy](privacy.md).
