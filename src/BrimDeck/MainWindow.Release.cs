using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    // Lets go of what the pages switched off in the settings built (App.ReleaseUnused, a while after the switch): their content and
    // what was kept to show them again without building. A page switched on later is built when it is first shown, as at the start.
    // The page on screen is never one of them: with every page off the usage page stays, to say so. False when nothing was held.
    internal bool ReleaseHiddenPages()
    {
        bool released = false;
        foreach (var page in DeckPages.Order)
        {
            if (DeckPages.Shown(Settings, page) || page == CurrentPage) continue;
            var content = (Panel)PageContent(page);
            bool held = content.Children.Count > 0;
            content.Children.Clear();
            switch (page)
            {
                case DeckPage.Usage:
                    held |= _usageBuilt is not null || _usageHeader is not null;
                    ModelToolbar.Children.Clear(); ApplicationPages.Children.Clear();
                    _usageBuilt = null; _usageHeader = null; _usageForm = ""; _modelScroll = null;
                    break;
                case DeckPage.Music:
                    _musicControls = null; _musicFullControl = null; _musicSmtcHint = null; _musicControlButtons.Clear();
                    _musicSeek = null; _musicElapsed = null; _musicDuration = null; _musicEqualizer = null; _musicTitle = null; _musicMessage = null; _musicInfo = null;
                    _lyricCurrent = null; _lyricPrevious = null; _lyricNext = null; _lyricsAbove = []; _lyricsBelow = [];
                    _musicRenderKey = ""; _musicSong = ""; _musicForm = ""; _musicCoverShown = null; _musicHadLyrics = false;
                    break;
                case DeckPage.Monitor:
                    held |= _monitorView is not null;
                    _monitorView?.CloseCard();
                    _monitorView = null; _monitorKey = ""; _monitorLayout = null;
                    break;
                case DeckPage.Devices:
                    held |= _devicesView is not null;
                    _devicesView = null; _devicesKey = ""; _devicesLayout = null;
                    break;
                case DeckPage.Clipboard:
                    held |= _shelfBuilt is not null;
                    ShelfToolbar.Children.Clear(); _shelfRows.Clear(); _shelfTabs.Clear(); _fileFrame = null; _searchBox = null; _editBox = null;
                    _shelfBuilt = null; _shelfStale = true;
                    break;
            }
            released |= held;
        }
        // A picture of a page that was leaving is not kept either.
        if (released) _handover.Drop();
        return released;
    }
}
