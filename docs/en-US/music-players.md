# Supported players

The music page and the collapsed view show the song that is playing and offer playback controls. A title that fits stays still; one that does not (the longer the equalizer, the less room the title has) scrolls, as on the collapsed view. With animation off it does not scroll, and hovering shows the whole title. What is available depends on the player:

| Player | Shown | Controls |
| --- | --- | --- |
| NetEase Cloud Music | Track, cover, progress | With SMTC on: play, pause, previous and next<br>With full control: also seeking, playback modes (shuffle, repeat all, repeat one), and liking or unliking the current song |
| QQ Music | Track, progress | Play, pause, previous, next and seeking |
| Apple Music | Track, cover, progress | Play, pause, previous and next<br>With full control: also seeking, shuffle on or off, and the repeat mode |
| KuGou Music | Track, cover<br>With full control: also the actual progress | Play, pause, previous and next |
| Other players | The track, cover and progress the player shares with Windows media controls | The play, pause, previous, next and seeking the player supports |

"Other players" are players that use Windows media controls, that is, players that appear in the media card of the Windows volume flyout while they play.

## NetEase Cloud Music

- **By default**: the track, cover and progress are shown without any setup. The progress is an estimate.
- **With SMTC on**: in NetEase Cloud Music, open 设置 › 系统 and turn on 开启 SMTC (the NetEase client is in Chinese). You can then play, pause and change tracks from BrimDeck.
- **Full control**: select Enable full control on the music page and confirm; NetEase Cloud Music restarts. It then shows the actual progress, and you can seek and switch playback modes (shuffle, repeat all, repeat one). While NetEase Cloud Music is signed in and online, a heart also appears right of the controls: select it to add the current song to your liked songs, and again to remove it; a heart selected in NetEase Cloud Music shows in BrimDeck too. Starting NetEase Cloud Music the normal way returns it to normal mode. SMTC and normal mode offer no liking, because Windows media controls have no command for it.

Full control needs NetEase Cloud Music to open a local debugging port, which carries some risk while it is open. Read [Data and privacy](privacy.md#netease-cloud-music-full-control) before you turn it on.

- **Always on**: turn on Always enable full control in Settings › Music › Players › NetEase Cloud Music and confirm; NetEase Cloud Music restarts once. This changes 2 places in the `libcef.dll` in its install folder; after that, NetEase Cloud Music starts with full control every time, whatever starts it, with nothing to enable again. Turning the switch off restores the file; it does not close NetEase Cloud Music, which returns to normal mode at its next start. While the switch is on, if an update replaces the file, BrimDeck changes it again and restarts NetEase Cloud Music once.
- When NetEase Cloud Music is installed in a folder that needs administrator rights, such as `C:\Program Files`, Windows asks for them to change the file.
- To enable it just once instead, select Enable full control on the music page, or turn on Always show "Enable full control" in Settings › Music › Players.

## Apple Music

- **By default**: the track, cover and progress are shown, and you can play, pause and change tracks.
- **Full control**: select Enable full control on the music page and confirm. You can then seek, turn shuffle on or off, and switch the repeat mode (off, repeat all, repeat one). These work by operating the buttons and the progress bar in the Apple Music window through Windows UI Automation, so they need the Apple Music main window; while that window is not minimized, using them brings it to the front. Full control lasts until BrimDeck restarts.

## KuGou Music

- **By default**: check "支持系统播放控件，如锁屏界面" (support system playback controls) in KuGou's 设置 › 常规设置 › 播放 (Settings › General › Playback); the track and cover are then shown, and you can play, pause and change tracks. Without it and without full control, BrimDeck cannot see KuGou. KuGou does not share its progress with Windows, so no progress is shown and the lyrics cannot follow the song.
- **Full control**: the actual progress and a larger cover are shown, the lyrics follow the song, and KuGou's own lyrics are used first. Full control does not need the option above. KuGou offers no way to set the position, so seeking is still not possible.
- **Turning it on and off**: turn on Always enable full control in Settings › Players › KuGou Music, or select Enable full control on the music page in normal mode, and confirm; KuGou restarts once. Full control needs 9 bytes of `libcef.dll` in KuGou's installation folder changed; after that, KuGou starts with full control every time, however it is started. Turning the switch off restores the original file without closing KuGou, which returns to normal mode at its next start. While the switch is on, BrimDeck changes the file again after a KuGou update has replaced it, and restarts KuGou once.
- When KuGou is installed in a folder that needs administrator rights, such as `C:\Program Files`, Windows asks for them when the file is changed.
- BrimDeck reads and writes only these 9 places. A file that tools such as LyricsWall changed the same way is used as it is; one they changed in part gets only the remaining places written when full control is turned on. If another tool changed any of these places to something else, BrimDeck leaves the file alone; if KuGou's debugging port is opened some other way, BrimDeck connects to it.

Full control makes KuGou open a local debugging port. Read [Data and privacy](privacy.md#kugou-music-full-control) before you turn it on.
