# Panel theme guide

The Notch, Capsule and Indicator, the quota notice, the expanded panel and its pop-ups are colored by a "panel theme". Choose it in **Settings › Panel theme**. There are three kinds:

| Kind | Themes |
| --- | --- |
| Basics | System, Dark (the default: the original pure black look), Light |
| Theme extensions | One Dark Pro, GitHub Dark, GitHub Light, Dracula Official, Night Owl, Island Holiday, Island Holiday 2, Pixel Blocks, Cyberpunk: Edgerunners (the demo) |
| Custom | The theme files in the themes folder, which can be created, edited and deleted in settings |

Each theme remembers the theme, warning and critical colors of the app columns and the clock's style and color you change while using it: they are saved when you switch to another theme and come back when you return. A theme chosen for the first time uses its own colors and suggested clock. Restoring defaults in "Quotas and usage" or "Collapsed view" restores the current theme's defaults.

One Dark Pro, GitHub Dark, GitHub Light, Dracula Official and Night Owl are ported from open-source VS Code themes and use their colors under each theme's MIT License: [One Dark Pro](https://github.com/Binaryify/OneDark-Pro), [GitHub Theme](https://github.com/primer/github-vscode-theme), [Dracula Official](https://github.com/dracula/visual-studio-code) and [Night Owl](https://github.com/sdras/night-owl-vscode-theme). The copyright and license notices of these projects ship with BrimDeck in `ThirdPartyNotices.txt`.

## Changing colors in settings

A theme can be adjusted without writing a file. Under "Colors" in **Settings › Panel theme**:

- **Background, foreground and accent**: changing one of them makes the colors that follow from it again. The background sets the surface and the pop-ups; the foreground sets the text, dividers and fills; the accent sets keyboard focus, chosen dates and the date range. When the background turns from dark to light (or the reverse) and the foreground would no longer read on it, the foreground changes too.
- **Advanced**: lists every color, the default app colors and the effects (rim width, shadow, scan lines). A translucent color keeps its opacity when a new color is picked.

A changed item is marked "Unsaved" and can be undone on its own, or all at once with "Undo all changes". **Changes take effect only once saved**: until then the panel and the theme cards stay as they are. While there are changes, cards under "Custom" turn into save cards:

- **Save current theme** (blue, in place of "New from current theme"): saves the current theme with the changes as a new custom theme and switches to it. A built-in theme cannot be changed and is saved this way only.
- **Overwrite current theme** (amber, in place of the chosen custom theme's card): writes the changes into that theme's file. Only the changed values are rewritten; comments and layout stay as they were.

Unsaved changes are kept while BrimDeck runs and are lost when it exits. The surface and rim gradients, the clock's preset colors and gradients, and the color picker's presets are still set in the theme file.

## Theme files

Like a VS Code theme extension, a theme is a JSON file:

- The themes folder is `%LOCALAPPDATA%\BrimDeck\themes` ("Open themes folder" on the settings page).
- Every `*.json` in the folder, and every `theme.json` in a subfolder, is a theme.
- Comments (`//`) and trailing commas are allowed; unknown properties are ignored.
- Every theme has a **base theme** (`base`, `dark` if left out); the file only needs what differs from it.

The smallest theme is a few lines:

```json
{
  "id": "midnight-teal",
  "name": "Midnight teal",
  "base": "dark",
  "colors": { "surface": "#04161A", "accent": "#2DD4BF", "clock": "#5EEAD4" }
}
```

The full template is [`theme-template.json`](../../src/BrimDeck.Core/Templates/theme-template.json). It lists every color, app color and effect with its use and contrast minimum. **New from current theme** on the settings page fills the template with every value of the current theme, saves it as a new custom theme and switches to it; open it afterwards from "Open themes folder" or the file link under "Current theme". While the settings page is shown, saving a theme file reloads it and the panel updates.

### Colors

Colors are `#RRGGBB` or `#AARRGGBB` (AA is the opacity). The minimum in brackets is a WCAG 2 contrast. The three text levels, `menuDetail`, `accent` and `danger` must reach it on the surface (each stop of its gradient included) and on the pop-up surface; `liked`, `clock` and `equalizer` on the surface only; `onAccent` on the accent and `indicatorProgress` on the indicator. Text also stands on fills: `textPrimary` on `fillSelected`, `pressed`, `input`, `rangeFill`, `tooltip` and `bubble`, and `textSecondary` on `fill` and `hover`, each needing 4.5:1.

| Key | Use |
| --- | --- |
| `surface` | The notch, capsule and expanded panel; always opaque |
| `textPrimary` | Titles and figures (7:1) |
| `textSecondary` | Labels (4.5:1) |
| `textTertiary` | Reset times, notes and other small text (4.5:1) |
| `accent` / `onAccent` | Keyboard focus and chosen dates (3:1) / text on them, such as a notice's main button (4.5:1) |
| `danger` | Errors and paused sync (4.5:1) |
| `liked` | A liked song (3:1) |
| `clock` | The clock when its color is "Theme default" (4.5:1) |
| `equalizer` | The equalizer when it does not follow the cover (3:1) |
| `hairline` `track` `ringTrack` `fill` `fillSelected` `hover` `pressed` `outline` | Dividers, tracks, joined switches, hover and pressed states, outlines |
| `popup` `popupBorder` `input` `rangeFill` | Menus and pop-ups, date fields and date ranges |
| `coverPlaceholder` `coverOutline` `scrollThumb` | A missing cover, the cover outline, scroll bars (the thumb 2:1 on the surface) |
| `rim` `shadow` | The line along the surface's inner edge; the collapsed shadow (a bright color with no depth is a glow) |
| `indicator` `indicatorProgress` | The Indicator and the playback progress on it (2:1) |
| `switchFill` `seekRail` `rowDivider` `rowHover` | The toolbar page switch, the seek bar track, model detail row dividers and hover |
| `menuDetail` | Song titles in the playback source menu (3:1) |
| `tooltip` `tooltipBorder` `bubble` `bubbleBorder` | Tooltips on the panel and the time bubble over the seek bar |

### App colors (apps)

The default colors of the quota columns, bars and rings: `claude`, `codex`, `antigravity`, `cursor`, `zcode`, `dsh`, `grokBot`, `kimi`, `opencode`, `manual` (every source entered by hand), `other`, and `warning` and `critical` (these two are also text, so 4.5:1). A color chosen for a column in "Quotas and usage" takes precedence.

### Clock

The clock style and color that suit the theme. Choosing the theme in settings switches the collapsed clock to them; it can still be changed on the Collapsed view page. Its fields are inherited one by one like the colors: a file that names only a `style` keeps its base theme's color.

```json
"clock": { "style": "segment", "color": "default", "custom": "#B9A8FF", "gradient": ["#FF79C6", "#BD93F9", "#8BE9FD"] }
```

`style` is one of `minimal`, `stacked`, `digits`, `dots`, `segment`, `neon`, `serif`, `wide`, `condensed`, `pixel` (a block dot matrix, suggested by Pixel Blocks), `rounded` (the round Fredoka face, suggested by Island Holiday 2); `color` is `default` (the `clock` color above), `amber`, `cyan`, `pink`, `lime`, `custom`, `gradient` or `cover`. `palette` sets this theme's colors for the amber, cyan, pink and lime swatches, and `gradients` the preset gradients it offers (one to eight, each exactly three colors); both need 4.5:1 on the surface. When the theme changes, a preset gradient in use becomes the new theme's preset in the same place. `custom` is used when `color` is `custom`; `gradient`, exactly three colors, when `color` is `gradient`. On light themes the glow of the neon and seven-segment styles is softened so the digits stay sharp.

### Picker swatches (swatches)

The twelve colors under the color picker in settings. Without this list the picker offers the theme's app, warning, critical and clock colors (without repeats, filled with in-between colors when there are fewer than twelve); the dark theme keeps the original twelve. This list is not inherited.

### Effects

| Key | Effect | Range |
| --- | --- | --- |
| `surfaceGradient` | A gradient over the surface, top to bottom, 2 to 8 colors | |
| `rimGradient` | A gradient for the rim, left to right; replaces `rim` | |
| `rimWidth` | Rim width | 0–3 |
| `shadowBlur` `shadowDepth` `shadowOpacity` | Blur, offset and opacity of the shadow | 0–40, 0–12, 0–1 |
| `scanlines` | Horizontal scan lines in the primary text color at this opacity | 0–0.12 |
| `pixel` | Pixel art: the outline's curves become steps of this many DIP (rounded to whole device pixels), the panel's rounded corners become square and its text is drawn unsmoothed; with a `rimGradient` the rim becomes a bevel, its first color lighting the edges that face up and left and its last shading those that face down and right | 0, or 1–4 |
| `texture` | With `pixel`: squares of the pixel size a little lighter or darker than the surface, a block's grain. Its lightest and darkest squares are checked for contrast like gradient stops; without `pixel` it has no effect | 0–0.12 |
| `decor` | Quiet pictures drawn into the surface under the content, each value inherited on its own (a night look that names only `sea` keeps the day's `at` and `line`): `horizon` a sea (`sea`) below `at` of the height (0.62 when not set) with a line of light (`line`, any opacity, checked like the rest) along it; `caustics` a net of soft light lines below the horizon (over the whole surface without one); `palm` a palm frond's shadow in the open panel's top-right corner, above the horizon. A fully transparent color turns a part off, e.g. in a night look; a transparent `sea` removes the whole horizon, so the caustics then cover the surface. Nothing is drawn on the small collapsed island, so the horizon never crosses the clock; the motifs fade in as the panel opens. Text is held to its minimums over the sea (at the horizon and at its deepest), the line and the motifs, each over what it is drawn on | `at` 0.3–0.9, caustics alpha ≤ 0.2, palm alpha ≤ 0.12 |

### Day and night looks (night, daylight)

A theme can carry a night look: `night` holds the `colors`, `apps`, `effects` and `clock` that change at night, laid over the day look by the local clock; whatever it leaves out stays as by day. `daylight`'s `day` and `night` are when the day and the night begin (`HH:mm`, 06:30 and 18:30 by default; the day may run past midnight, as 20:00 to 08:00 does). The user can move them in half hours in Settings › Current theme › Daytime; choosing the theme's own hours again saves no setting. At the hour the island's old material fades out over 1.6 seconds, like dusk; with animations off it switches at once. Both looks are held to the readability rules below.

```jsonc
"daylight": { "day": "06:30", "night": "18:30" },
"night": {
  "colors": { "surface": "#0A1A2C", "textPrimary": "#EEF7F8" },
  "effects": { "shadowDepth": 0 }
}
```

A night look belongs to the file that writes it and is not inherited through `base`: a theme based on Island Holiday that should change at night writes its own `night`. "New from current theme", and saving an edit as a new theme, start the new file from the day look and carry the source theme's `night` and `daylight` along.

## Readability

A theme decides how the panel looks, never whether it can be read:

1. Every color with a minimum is checked against the surface, each gradient stop, the lightest and darkest squares of a texture and the pop-up surface before it is used.
2. A color below its minimum is lightened or darkened along its own hue, in whichever direction needs the smaller change, just far enough to pass. A color that passes is used as written. A translucent color that has to move comes back opaque, as it looked over the surface. When the backgrounds are both light and dark and no lightness passes them all, the lightness with the best worst case is used (never worse than the color as written), and the settings page says the minimum could not be met.
3. Colors chosen elsewhere, such as app colors, clock colors and the cover color, get the same treatment against the current surface, so a pale warning color or a lime clock stays clear on a light theme.
4. "Current theme › Readability" on the settings page lists every adjustment. A theme file that cannot be used is listed with the reason (invalid JSON, an unknown color, a duplicate id, …) and cannot be chosen.

Every built-in theme passes these rules as written; `PanelThemeTests` checks each one.

## Design steps

The demo theme was made in these five steps.

1. **Collect references**: frames of the work or brand; note the light and dark, the main and accent colors, and the shapes that recur.
2. **Assign roles**: the surface (the largest area), three text levels, the accent, the semantic colors (warning, critical, error) and the decoration (rim, shadow, gradient, texture).
3. **Surface and text first**: the surface sets every contrast; space the text levels at 7 : 4.5 : 3.
4. **Then accents and app colors**: app colors must be told apart and reach 3:1 on the surface (warning and critical, which are also text, 4.5:1); warning and critical should read as yellow and red.
5. **Check**: look at the collapsed and expanded panel, the notice and the pop-ups, and make sure "Readability" is empty or acceptable; otherwise go back to step 3.

## The newer themes

**Island Holiday**: by day, warm sand (`#FFF6E8`) fading into sea foam, deep-sea text, a lagoon accent, a coral serif clock, a shoreline rim (turquoise, sky, sand) and a faintly sea-green shadow. By night, a moonlit sea from navy into teal, a lantern-gold clock, a moonlit shoreline along the rim and, instead of a shadow, a soft teal glow under the island. Neither look needs any readability adjustment.

**Island Holiday 2**: the same island as a postcard, kept alongside the first. By day, a clear sky (`#A8E1F8`) fading into the warm haze of the horizon, and below a horizon about two thirds down a turquoise lagoon (`decor.horizon`) with a line of sunlight along it, the soft net of light a sunlit sea throws (`decor.caustics`) and a palm frond's shade in the open panel's corner (`decor.palm`); deep-sea text, a lagoon accent, a sunburnt coral clock in the round Fredoka face (the `rounded` style), a mango indicator, hibiscus for liked songs, a shore rim (lagoon, sky, sand, coral) and a faintly sea-green shadow. By night, a deep ocean navy (`#001E3C`) over a dark teal sea under a horizon lit faintly in moon gold, moonlight playing on the water, a lantern-gold clock and, instead of a shadow, a soft teal glow under the island. The small collapsed island keeps the plain sky, so the horizon never crosses the clock. Neither look needs any readability adjustment.

## Pixel Blocks

An unofficial pixel-art look inspired by block sandbox games, using no artwork, fonts, textures or logos from any game: everything is drawn from colors and squares. A deep slate surface with a 5% block grain (`texture`), 2 DIP stepped corners (`pixel`), a bevel rim lit at the top left and shaded at the bottom right, and a hard shadow with no blur; the text on the island and in the panel's pop-ups is drawn unsmoothed and the panel's rounded corners become square. Chat-style colors: white text, a yellow block clock (the `pixel` clock style), a green accent and a green experience-bar indicator; violet menus with a violet frame. The settings window follows Windows' light or dark mode, not the panel theme.

## Demo: Cyberpunk: Edgerunners

An unofficial fan palette. It uses colors only, no artwork, logos or fonts from the series.

**Style.** *Cyberpunk: Edgerunners* by Studio TRIGGER carries the retro-future look of 1980s anime: flat cel color, saturated neon, a violet-black sky over Night City with streets lit by magenta and cyan signs, and the signature yellow of *Cyberpunk 2077* in the opening and the interfaces. Lucy's hair runs from cyan to pink, Rebecca wears mint green and pink; scan lines, chromatic aberration and cut-corner HUD frames recur, and so does the moon.

| Source | Color | Panel role |
| --- | --- | --- |
| Night City's violet sky | `#0A0613` → `#1C0B30` | Surface and its top-to-bottom gradient |
| Moonlight | `#F4F0FF` / `#C9BEEB` / `#8F84BD` | Three text levels (pale lavender) |
| Cyan neon, Lucy's hair | `#00F0FF` | Accent, focus, the tint of switches and tracks |
| Magenta neon | `#FF2A6D` | Collapsed glow, liked song, Indicator progress |
| Cyberpunk yellow | `#FCEE0A` | Clock (seven-segment, like a HUD readout), Indicator, warning |
| Rebecca's mint | `#3DFFB0` | The Codex column |
| Neon gradient | cyan → violet → magenta | Rim |
| CRT screens | 4% scan lines | Surface texture |

**Check.** Primary text is above 15:1 on both gradient stops and tertiary text above 5:1; the yellow clock and warning are about 15:1 on the violet-black; the magenta progress is about 3:1 on the yellow Indicator. Every rule passes the first time, with no adjustment.

The full file is [`cyberpunk-edgerunners.json`](../../src/BrimDeck.Core/Themes/cyberpunk-edgerunners.json). To start your own theme from it, choose it in settings and press "New from current theme".

## Interface font

The interface font is an app setting, not part of a theme: choose it under Settings › General › Appearance and language › Interface font. A theme file cannot name a font. The chosen font goes in front of the default one and Chinese still falls back to Microsoft YaHei, so a missing glyph never shows as a box. The settings window itself keeps the app's own font, so it stays readable while you try fonts. The clock styles that ship their own font (such as Orbitron and Fredoka) and the seven-segment, dot-matrix and pixel styles keep their own letterforms; the other clock styles follow the chosen font.
