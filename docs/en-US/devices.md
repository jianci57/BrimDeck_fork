# Supported devices

The device batteries page shows the battery of the connected wireless mice, keyboards, headsets, controllers and pens; the notch and capsule can show them too. The settings are under Settings › Display and devices.

BrimDeck only reads batteries: it never pairs, disconnects or changes any device or Windows setting, needs no administrator rights and installs no driver. Devices are read only while the device batteries page is open, the notch or capsule shows device batteries, or Settings › Display and devices is open.

## Supported connections

| Connection | How it is read | Notes |
| --- | --- | --- |
| Bluetooth | The battery Windows itself reads, as Windows Settings › Bluetooth & devices shows it; a Bluetooth LE device's own battery service is read as well | Only connected devices are listed. A device Windows has no battery for shows "No battery reported" |
| AirPods, Beats | The Bluetooth advertisement the earbuds send while out of the case or with its lid open | Shows each bud and the case. Read only while the earbuds are connected and next to the PC |
| 2.4 GHz receiver or cable | Each brand's own protocol | Only known models of the brands listed below |
| Game controllers | Windows' game controller interface; PlayStation controllers through their input reports | Xbox controllers (Xbox Wireless Adapter or cable), PlayStation controllers, and others Windows gives a battery for |

HID devices that carry the standard battery report are shown too.

## Brands supported through a receiver or cable

| Kind | Brands |
| --- | --- |
| Mice, keyboards and headsets | Logitech (Unifying, Bolt and Lightspeed receivers, and cable), ASUS ROG |
| Mice and keyboards | Razer |
| Mice | ATK, VXE, VGN, Pulsar, MCHOSE, Endgame Gear, VAXEE, Inphic, AJAZZ, WLmouse, G-Wolves, LAMZU, Ninjutso, LINGBAO, NZXT, HECATE, Dareu, SteelSeries, Rapoo, Attack Shark, DELUX, Corsair |
| Keyboards | VGN, ATK Z87 Pro, AULA F87 PRO, CHERRY PIXIU75 |
| Headsets | SteelSeries, HyperX, MCHOSE, Corsair, AULA G9, JBL Quantum 910, PULSE 3D |
| Controllers | Xbox, PlayStation, Steam Controller |

- Only known models of each brand are supported; other models of the same brand may not report a battery.
- Some models are implemented from public protocol documentation and have not been checked on real hardware. If a battery does not show or reads wrong, please report the model in [Issues](https://github.com/MoFeng2223/BrimDeck/issues).
- While a device sleeps or is out of range, the last charge its receiver recorded is shown.

## Display and settings

- **Battery thresholds**: at or under the warning threshold (25% by default) a battery shows as getting low, in the warning color; at or under the critical threshold (15% by default) it shows as low, in the critical color. A charging device shows a bolt.
- **Device list**: rename a device, drag to reorder, set the color of each state, and choose separately whether it shows on the page and when collapsed. Once you have changed the list, disconnected devices stay in it; choose "Forget" for one you no longer need.
- **When collapsed**: turn on "Device batteries" for the notch or capsule under Settings › Collapsed view › Content, and each device shows as a ring; the figures beside the rings are optional.

## Acknowledgements

Parts of the protocols are ported from or follow these open-source projects; the full licenses are in [ThirdPartyNotices.txt](../../src/BrimDeck/ThirdPartyNotices.txt):

- [device-battery-viewer](https://github.com/CharlesGool/device-battery-viewer) (Logitech HID++)
- [razer-battery-report](https://github.com/xzeldon/razer-battery-report) (Razer)
- [ATK-A9-Battery](https://github.com/Lkig/ATK-A9-Battery), [atkctl](https://github.com/cobanov/atkctl) (ATK, VXE)
- [InphicBatteryTray](https://github.com/Tan-TanZi/InphicBatteryTray) (Inphic)
- [aj179-linux](https://github.com/Rockeyxx/aj179-linux) (AJAZZ)
- [HaloBattery](https://github.com/HeyOkay/HaloBattery), [vigil-watt](https://github.com/evren-oct/vigil-watt) (mice, keyboards and headsets of several brands)
- [mouse-battery-tray](https://github.com/incconutwo/mouse-battery-tray) (incconutwo), [mouse-battery-tray](https://github.com/misaka310/mouse-battery-tray) (misaka310), [mousedock](https://github.com/late-autumn-gif/mousedock)
- [rapoo-tray](https://github.com/Iris-0109/rapoo-tray), [mousectl](https://github.com/D3m0nZOnFire/mousectl) (Rapoo)
- [rivalcfg](https://github.com/flozz/rivalcfg) (SteelSeries mice)
