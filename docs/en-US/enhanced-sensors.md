# Enhanced sensors

On many PCs Windows gives no CPU temperature or CPU power: desktops often have no usable ACPI thermal zone, and some AMD PCs have no energy meter. Reading them takes the processor's own registers, which only a kernel driver can reach. Enhanced sensors use the open-source driver [PawnIO](https://pawnio.eu): BrimDeck's sensor service reads them from the processor itself.

Where: Settings › System monitor › Enhanced sensors.

## What it adds

| Reading | Intel | AMD (Zen, family 17h–1Ah) |
| --- | --- | --- |
| CPU temperature | Package temperature | Tctl/Tdie |
| Per-part temperature | Each core | Each CCD |
| CPU power | Package power; memory power | Package power |

The CPU temperature's tooltip on the monitor page names its source, such as "(processor sensors)". Which readings a PC gives depends on its processor. Discrete graphics and drives are not affected: they never needed PawnIO.

## Turning it on

1. **Install PawnIO**: click Install. BrimDeck opens a command window running `winget install --id namazso.PawnIO --exact`; the installer asks an administrator to confirm. Without winget, PawnIO's own download page opens. A PC that already has PawnIO (installed with FanControl or LibreHardwareMonitor, for example) shows it as installed and needs nothing more.
2. **Turn on the sensor service**: click Turn on. BrimDeck downloads the sensor service and PawnIO's official modules, and Windows asks an administrator to confirm once; never again after that.

## Safety

- The sensor service is installed in `C:\Program Files\BrimDeck Sensors`, which only administrators can change. It runs as the system account, as PawnIO opens only for the system and administrators.
- The service reads a fixed few temperature and energy registers. It writes none, and offers no way to read any other.
- The downloaded service must match the SHA-256 recorded when this BrimDeck version was released; PawnIO's modules come from PawnIO's own release page and are checked against their SHA-256 too, and the PawnIO driver itself loads only modules signed by its author.
- The service runs only while the monitor page or the collapsed row needs the CPU's temperature or power, and stops itself after about a minute idle. BrimDeck reads it over a local named pipe that only users signed in to this PC can open.

## Turning it off and uninstalling

- **Turn off the sensor service**: click Turn off on the Enhanced sensors page. This removes the service and `C:\Program Files\BrimDeck Sensors`, with one administrator prompt. Uninstalling BrimDeck removes them too.
- **Uninstall PawnIO**: click Uninstall beside PawnIO on the same page, or uninstall it from Windows Settings › Apps. Software such as FanControl or LibreHardwareMonitor may use PawnIO as well, and will be affected.

## Without it

BrimDeck still reads the CPU temperature (ACPI thermal zones) and power (energy meter) Windows gives. A running copy of either of these is read for the CPU's temperature and power as well:

- **HWiNFO**: with Shared Memory Support turned on in its settings. The free version keeps it on for 12 hours at a time.
- **LibreHardwareMonitor**: with Options › Remote Web Server turned on, on the default port 8085. BrimDeck connects to this PC only.

BrimDeck looks for them when it starts and when the monitor page opens, and a few more times in the minutes after when they are not found.
