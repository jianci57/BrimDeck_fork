using BrimDeck.Core;

// The letter in the middle of a collapsed quota ring (CompactMusicLayout.RingLetter): the first letter or digit of the application's
// name, upper case, so rings are told apart by more than their color.
internal static class RingLetterTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        // The setting (DeckSettings.QuotaRingLetters): off at first, saved and read back, and off for a file that does not have it.
        var store = new SettingsStore(Path.Combine(root, "ring-letters"), "0.1.0");
        var fresh = store.Load();
        bool offAtFirst = !fresh.QuotaRingLetters;
        fresh.QuotaRingLetters = true; store.Save(fresh);
        bool savedOn = new SettingsStore(store.DirectoryPath, "0.1.0").Load().QuotaRingLetters;
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","NotchSummary":true}""");
        bool oldFileOff = !new SettingsStore(store.DirectoryPath, "0.1.0").Load().QuotaRingLetters;
        check("Ring letter setting: off by default, saved and read back, and off for a file without it", offAtFirst && savedOn && oldFileOff);

        // Off, no ring gets a letter whatever the name; on, the letter is the application's display name's (AppEntry.Name), so a
        // renamed application's letter follows its new name and an unnamed one keeps its source's.
        var entry = new AppEntry { QuotaSource = ProviderId.Claude };
        string sourceLetter = CompactMusicLayout.RingLetter(entry.Name);
        entry.DisplayName = "Zeta";
        string renamedLetter = CompactMusicLayout.RingLetterFor(true, entry.Name);
        entry.DisplayName = "工作用量";
        check("Ring letter is empty with the setting off, whatever the name",
            CompactMusicLayout.RingLetterFor(false, entry.Name) == "" && CompactMusicLayout.RingLetterFor(false, "Claude") == "" && CompactMusicLayout.RingLetterFor(false, null) == "");
        check("Ring letter follows the display name when the application is renamed",
            sourceLetter.Length > 0 && renamedLetter == "Z" && CompactMusicLayout.RingLetterFor(true, entry.Name) == "工");

        // Placing the letter: a box of whole pixels is symmetric about the ring's centre only if its width and height have the ring's
        // parity, so the ink takes the nearest such size (a 5 pixel letter in a 12 pixel ring becomes 4 or 6, not half a pixel off), never
        // more than the ring's inside, and the box is centred exactly on the centre.
        bool exact = true, parity = true, inside = true, wrongParityOff = false;
        foreach (int cell in new[] { 10, 12, 14, 15, 16, 20, 21, 25 })
            foreach (double w in new[] { 1.2, 2.4, 3.9, 4.6, 5.0, 5.3, 6.4, 7.7 })
                foreach (double h in new[] { 4.1, 4.9, 5.3, 6.2, 7.0 })
                {
                    var box = CompactMusicLayout.RingLetterBox(w, h, cell, .5, .5, cell - 5);
                    exact &= box.Left * 2 + box.Width == cell && box.Top * 2 + box.Height == cell;
                    parity &= (box.Width & 1) == (cell & 1) && (box.Height & 1) == (cell & 1);
                    inside &= box.Width <= Math.Max(cell - 5, 2) && box.Height <= Math.Max(cell - 5, 2);
                    // The box the ink as it is (rounded to whole pixels, the parity left alone) would be centred at best half a pixel off.
                    int plain = (int)Math.Round(w);
                    if (cell % 2 == 0 && plain % 2 == 1 && plain <= cell) wrongParityOff = true;
                }
        check("Ring letter box is centred on the ring's centre exactly, with the ring's parity, inside the ring", exact && parity && inside);
        check("Ring letter: an ink of the other parity (the 5 pixels of a 12 pixel ring) could only sit half a pixel off, which the eye sees", wrongParityOff);
        var tall = CompactMusicLayout.RingLetterBox(5.3, 5.3, 12, .5, .5, 7);
        check("Ring letter in a 12 pixel ring with 5.3 pixels of ink is 6 by 6 at 3,3", tall == (3, 3, 6, 6));
        // The mass of a letter (an A's below the middle, a V's above it) moves the box by half its distance from the centre, to whole
        // pixels: nothing within a pixel (small letters stay exactly centred), a pixel for two.
        var small = CompactMusicLayout.RingLetterBox(5.3, 5.3, 12, .5, .6, 7);
        var big = CompactMusicLayout.RingLetterBox(13.3, 13.3, 30, .5, .65, 20);
        var up = CompactMusicLayout.RingLetterBox(13.3, 13.3, 30, .5, .35, 20);
        check("Ring letter keeps its box centred when the mass is within a pixel of the middle, moves it toward the mass beyond",
            small.Top * 2 + small.Height == 12 && big.Top * 2 + big.Height == 30 - 2 && up.Top * 2 + up.Height == 30 + 2 && big.Left * 2 + big.Width == 30);
        check("Ring letter box ignores a non-number",
            CompactMusicLayout.RingLetterBox(double.NaN, 5, 12, double.NaN, .5, 7) is { Width: >= 1, Height: >= 1 });

        check("Ring letter is the upper-case first letter", CompactMusicLayout.RingLetter("claude") == "C" && CompactMusicLayout.RingLetter("Codex") == "C");
        check("Ring letter skips leading punctuation and spaces", CompactMusicLayout.RingLetter("  [dsh]") == "D" && CompactMusicLayout.RingLetter("-- kimi") == "K");
        check("Ring letter takes a leading digit", CompactMusicLayout.RingLetter("1Password") == "1");
        check("Ring letter keeps a CJK character as it is", CompactMusicLayout.RingLetter("\u6587\u5fc3") == "\u6587");
        // U+20BB7 (a CJK ideograph outside the BMP) is two UTF-16 units; the letter must be the whole character, not the
        // second ideograph after the surrogate pair was skipped, and not half of a pair.
        string astralName = "\U00020BB7野", astralLetter = CompactMusicLayout.RingLetter(astralName);
        check("Ring letter keeps a supplementary-plane character whole", astralLetter == "\U00020BB7" && astralLetter.Length == 2 && char.IsSurrogatePair(astralLetter, 0));
        check("Ring letter upper-cases a supplementary-plane letter", CompactMusicLayout.RingLetter("\U00010428x") == "\U00010400");
        check("Ring letter skips punctuation before a supplementary-plane character", CompactMusicLayout.RingLetter("[ \U00020BB7]") == "\U00020BB7");
        check("Ring letter ignores a lone surrogate", CompactMusicLayout.RingLetter("\ud842x") == "X");
        check("Ring letter is empty without a letter or digit", CompactMusicLayout.RingLetter("") == "" && CompactMusicLayout.RingLetter("  -_ ") == "" && CompactMusicLayout.RingLetter(null) == "");
    }
}
