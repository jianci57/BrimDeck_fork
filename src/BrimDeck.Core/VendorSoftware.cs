namespace BrimDeck.Core;

// The brands whose battery query is two steps, a feature report written and then its reply read back, and the vendor programs
// that talk to the same devices. Nothing makes the two steps atomic: while the vendor's own program runs, it can write its
// request between ours and the reply, and each side reads the other's answer (and a mouse can be left unresponsive until its
// receiver is replugged, as razer-mouse-battery-windows' troubleshooting says of Razer Synapse). So BrimDeck stands aside from
// such a brand while its software runs, and reads it again once that has closed. Whether it runs is looked up in one process
// snapshot per pass, and only when a device of such a brand is plugged in (HidBatteries.Scan).
public enum DeviceBrand { Razer, EndgameGear, Vaxee, Inphic }

public static class VendorSoftware
{
    // The brand a USB vendor id belongs to, for the brands above; null for the rest (HID++ and the one-step brands).
    public static DeviceBrand? BrandOf(int vendor) => vendor switch
    {
        Razer.Vendor => DeviceBrand.Razer,
        Dongles.EndgameVendor => DeviceBrand.EndgameGear,
        Dongles.VaxeeVendor => DeviceBrand.Vaxee,
        Dongles.InphicVendor or Dongles.InphicWireless => DeviceBrand.Inphic,
        _ => null
    };

    // The executable names of each brand's software, as fragments of the file name, case ignored (the version, "4", or a
    // "Service" suffix does not matter). The one list, here, and Note is the only thing that reads it. The names are those the
    // programs are known by, not checked on a machine with each installed. The background parts are listed with the windows:
    // Razer's service and app engine run whenever Synapse is installed, and they are what talks to the mouse. Web tools in a
    // browser (WebHID) cannot be seen by a process name, and are not covered.
    private static readonly (DeviceBrand Brand, string[] Names)[] Programs =
    [
        (DeviceBrand.Razer, ["Razer Synapse", "RazerSynapse", "RazerCentral", "Razer Central", "RazerAppEngine"]),
        (DeviceBrand.EndgameGear, ["EndgameGear", "Endgame Gear", "Endgame_Gear"]),
        (DeviceBrand.Vaxee, ["VAXEE"]),
        (DeviceBrand.Inphic, ["Inphic", "英菲克"]),
    ];

    // Adds the brand a running process's executable file name ("Razer Synapse Service.exe") belongs to, if any, to running. No
    // string is made: it is called for every process in the snapshot.
    public static void Note(ReadOnlySpan<char> file, ISet<DeviceBrand> running)
    {
        foreach (var (brand, names) in Programs)
        {
            if (running.Contains(brand)) continue;
            foreach (var name in names)
                if (file.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { running.Add(brand); break; }
        }
    }

    // The brands whose software is among these executable file names.
    public static HashSet<DeviceBrand> Running(IEnumerable<string> files)
    {
        var running = new HashSet<DeviceBrand>();
        foreach (var file in files) Note(file, running);
        return running;
    }
}
