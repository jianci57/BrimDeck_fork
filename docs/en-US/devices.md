# Supported devices

The device batteries page shows the battery of the connected wireless mice, keyboards, headsets, controllers and pens; the notch and capsule can show them too. The settings are under Settings › Display and devices.

BrimDeck only reads batteries: it never pairs, disconnects or changes any device or Windows setting, needs no administrator rights and installs no driver. Devices are read only while the device batteries page is open, the notch or capsule shows device batteries, or Settings › Display and devices is open.

## Supported connections

| Connection | How it is read | Notes |
| --- | --- | --- |
| Bluetooth | The battery Windows itself reads, as Windows Settings › Bluetooth & devices shows it; a Bluetooth LE device's own battery service is read as well | Only connected devices are listed |
| AirPods, Beats | The Bluetooth advertisement the earbuds send while out of the case or with its lid open | Each bud and the case |
| 2.4 GHz receiver or cable | Each brand's own protocol | See the brands below |
| Game controllers | Windows' game controller interface, and some brands' own protocols | See the brands below |

HID devices that carry the standard battery report are shown too.

## Supported brands

"Whole brand" means devices are recognized by the brand's protocol, not only known models; the others are known models.

**Mice**

| Brand | Coverage |
| --- | --- |
| Logitech, MCHOSE, ATK, Endgame Gear, VAXEE, Rapoo, Keychron, Finalmouse | Whole brand |
| Razer, ASUS ROG, SteelSeries, Corsair, HyperX | Known wireless models |
| VXE, VGN, Pulsar, LAMZU, G-Wolves, WLmouse, Ninjutso, LINGBAO, NZXT, HECATE, Dareu, Inphic, AJAZZ, Attack Shark, DELUX, Glorious, Darmoshark, EWEADN, AULA, Thunderobot ML903, Scyrox, Cougar, Redragon, Hitscan, iRocks, Vancer, Incott, Ryunix, JD Jingzao JZM5, RIDLAG, Motospeed X6 | Known models |

**Keyboards**

| Brand | Coverage |
| --- | --- |
| Logitech (through its receivers or cable) | Whole brand |
| Razer, ASUS ROG | Known wireless models |
| MCHOSE (K87S, G87 V2, K99 V3, G75 V2, G98 V3 and K87 V3 included), AULA (F87, F87 Pro, F99, F99 Pro, F75 and F108 Pro included), LINGBAO K98, KZZI (K75, I75, I98 and Z75 included), VGN (V108, V98 Pro V3, V87 V2, N75 V2, N87 and Neon75 included), ATK (Z87 Pro, S87, S99, TV88, TV99, TV108, V100, A87 Pro, A98 Pro, A87 Ultra and YOGO 75 included), CHERRY PIXIU75, Corsair (K65 PLUS, K57), Alienware Pro, URX Core75, Lofree Flow Lite84, MonsGeek, Akko, Epomaker (some TH series models and the G84 PRO included), AJAZZ AK870, CAROTMAS Mars68, GANSS, EWEADN X87, Teevolution, RK R98Pro V2, and other brands' receivers on the same platform as MonsGeek's | Known models |

**Headsets**

| Brand | Coverage |
| --- | --- |
| Jabra, FIFINE, XIBERIA | Whole brand |
| Logitech (G PRO X 2 included), SteelSeries (GameBuds X included), HyperX, Corsair (Virtuoso, HS80 and Void series included), Razer, Sony INZONE and PULSE Explore, ASUS ROG, ASTRO A50, Audeze Maxwell, Skullcandy PLYR, AKG N9, MCHOSE (V9 PRO and G9 PRO included), AULA (G9, A9), JBL Quantum 910, PULSE 3D, ATK (N9 Pro, N9 Ultra, N9 V2 series), Lenovo, DARKRIM, Edifier Halo Space | Known models |

**Controllers**

| Brand | Coverage |
| --- | --- |
| Xbox, BETOP, Flydigi, GameSir, 8BitDo | Whole brand |
| PlayStation (DualSense, DualSense Edge, DualShock 4), Steam Controller, NVIDIA SHIELD, Xbox 360 wireless receiver | Known models |

- Many models are implemented from public protocol notes and have not been checked on real hardware. If a battery does not show or reads wrong, please report the model, and the receiver's hardware ID from Device Manager, in [Issues](https://github.com/MoFeng2223/BrimDeck/issues).
- Flydigi controllers, the Xbox 360 wireless receiver and some headsets report only a few steps; their level shows as an estimate.
- While a device sleeps or is out of range, the last level is shown.

## How devices are read, and safety

- Only battery reads are sent, never a command that changes a setting.
- Where a brand has several reads, they are tried in turn only when the device is first seen, three rounds at most. The read that answers is kept on this computer and is the only one sent from then on, across updates. If it stops answering for a long time, the others are tried once more.
- A read during which the device misbehaves (it disconnects right after, or BrimDeck ends while waiting for the answer) is never sent to it again; after two such reads the device is no longer read.
- A device recognized without a reading shows "No battery reported". Turning "Read" off for it in the device list stops every request to it.

## Display and settings

- **Battery thresholds**: at or under the warning threshold (25% by default) a battery shows as getting low, in the warning color; at or under the critical threshold (15% by default) it shows as low, in the critical color. A charging device shows a bolt.
- **Device list**: rename a device, drag to reorder, set the color of each state, and choose separately whether it shows on the page and when collapsed, and whether its battery is read. Once you have changed the list, disconnected devices stay in it; choose "Forget" for one you no longer need.
- **When collapsed**: turn on "Device batteries" for the notch or capsule under Settings › Collapsed view › Content, and each device shows as a ring; the figures beside the rings are optional.

## Acknowledgements

Parts of the protocols are ported from or follow these open-source projects; the full licenses are in [ThirdPartyNotices.txt](../../src/BrimDeck/ThirdPartyNotices.txt):

- [device-battery-viewer](https://github.com/CharlesGool/device-battery-viewer) (Logitech HID++)
- [razer-battery-report](https://github.com/xzeldon/razer-battery-report) (Razer)
- [ATK-A9-Battery](https://github.com/Lkig/ATK-A9-Battery), [atkctl](https://github.com/cobanov/atkctl) (ATK, VXE)
- [InphicBatteryTray](https://github.com/Tan-TanZi/InphicBatteryTray) (Inphic)
- [aj179-linux](https://github.com/Rockeyxx/aj179-linux) (AJAZZ)
- [HaloBattery](https://github.com/HeyOkay/HaloBattery), [vigil-watt](https://github.com/evren-oct/vigil-watt), [mousedock](https://github.com/late-autumn-gif/mousedock), [Voltide](https://github.com/sdparklab/Voltide) (mice, keyboards, headsets and controllers of many brands)
- [mouse-battery-tray](https://github.com/incconutwo/mouse-battery-tray) (incconutwo), [mouse-battery-tray](https://github.com/misaka310/mouse-battery-tray) (misaka310)
- [rapoo-tray](https://github.com/Iris-0109/rapoo-tray), [mousectl](https://github.com/D3m0nZOnFire/mousectl) (Rapoo)
- [rivalcfg](https://github.com/flozz/rivalcfg) (SteelSeries mice)
- [SDL](https://github.com/libsdl-org/SDL) (Flydigi, 8BitDo, NVIDIA SHIELD, Xbox 360 controllers)
- [betop-battery](https://github.com/oitsukiii/betop-battery) (BETOP)
- [GamesirBatteryTray](https://github.com/WWWyyy452/GamesirBatteryTray), [gamesir-battery-tray](https://github.com/agnostos-t/gamesir-battery-tray) (GameSir)
- [darmoshark-m3-configurator](https://github.com/Barba2k2/darmoshark-m3-configurator) (Darmoshark)
- [Jingzao-JZM5-Battery](https://github.com/LANMIN-X/Jingzao-JZM5-Battery) (JD Jingzao)
- [pulsar-x3-python](https://github.com/jonkristian/pulsar-x3-python) (Pulsar)
- [YesCharge](https://github.com/Yesid-Robayo/YesCharge) (CAROTMAS)
- [aula-keyboard-battery-tray](https://github.com/KhromotozzDevOut/aula-keyboard-battery-tray), [Aula-F75-Max-Driver](https://github.com/VitalyArt/Aula-F75-Max-Driver) (AULA keyboards)
- [mchose-macos](https://github.com/Klegus/mchose-macos) (MCHOSE)
- [ShowMeTheConnection](https://github.com/NaoCoding/ShowMeTheConnection) (KZZI)
- [Headroom](https://github.com/patalbansishashank/Headroom) (Skullcandy, Audeze), [eh-fifty](https://github.com/tdryer/eh-fifty) (ASTRO A50)
