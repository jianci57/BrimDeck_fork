# 设备电量支持

设备电量页显示已连接的无线鼠标、键盘、耳机、手柄和触控笔的电量；刘海与胶囊也可以显示。相关设置在“设置 › 显示与设备”中。

BrimDeck 只读取电量：不配对、不断开，也不改动任何设备与 Windows 设置，不需要管理员权限，也不安装驱动。只在设备电量页打开、刘海或胶囊显示设备电量，或“设置 › 显示与设备”打开时读取。

## 支持的连接方式

| 连接方式 | 读取方法 | 说明 |
| --- | --- | --- |
| 蓝牙 | Windows 自己读取的电量，与“Windows 设置 › 蓝牙和设备”中显示的一致；蓝牙低功耗设备另外读取其电池服务 | 只列出已连接的设备 |
| AirPods、Beats | 耳机出盒或开盖时发出的蓝牙广播 | 分别显示左右耳与充电盒 |
| 2.4G 接收器或数据线 | 各品牌设备自己的协议 | 见下方品牌表 |
| 游戏手柄 | Windows 的游戏控制器接口，以及部分品牌自己的协议 | 见下方品牌表 |

带有标准 HID 电量报告的设备也会显示。

## 支持的品牌

“整个品牌”表示按品牌的协议识别，不限于已知型号；其余为已知型号。

**鼠标**

| 品牌 | 范围 |
| --- | --- |
| 罗技、迈从（MCHOSE）、ATK、Endgame Gear、VAXEE、雷柏（Rapoo）、Keychron、Finalmouse | 整个品牌 |
| 雷蛇、华硕 ROG、赛睿（SteelSeries）、海盗船（Corsair）、HyperX | 已知的无线型号 |
| VXE、VGN、Pulsar、LAMZU、G-Wolves、WLmouse、Ninjutso、LINGBAO、NZXT、HECATE、达尔优（Dareu）、英菲克（Inphic）、黑爵（AJAZZ）、Attack Shark、多彩（DELUX）、Glorious、Darmoshark、前行者（EWEADN）、狼蛛（AULA）、雷神 ML903、Scyrox、Cougar、红龙（Redragon）、Hitscan、iRocks、Vancer、Incott、Ryunix、京东京造 JZM5、RIDLAG、摩豹（Motospeed）X6 | 已知型号 |

**键盘**

| 品牌 | 范围 |
| --- | --- |
| 罗技（经接收器或数据线连接） | 整个品牌 |
| 雷蛇、华硕 ROG | 已知的无线型号 |
| 迈从（含 K87S、G87 V2、K99 V3、G75 V2、G98 V3、K87 V3）、狼蛛（含 F87、F87 Pro、F99、F99 Pro、F75、F108 Pro）、凌豹 K98、珂芝（含 K75、I75、I98、Z75）、VGN（含 V108、V98 Pro V3、V87 V2、N75 V2、N87、Neon75）、ATK（含 Z87 Pro、S87、S99、TV88、TV99、TV108、V100、A87 Pro、A98 Pro、A87 Ultra、YOGO 75）、CHERRY PIXIU75、海盗船（K65 PLUS、K57）、Alienware Pro、URX Core75、Lofree Flow Lite84、MonsGeek、Akko、Epomaker（含部分 TH 系列与 G84 PRO）、黑爵 AK870、CAROTMAS Mars68、GANSS、前行者 X87、Teevolution、RK R98Pro V2，以及与 MonsGeek 采用同一平台的其他品牌接收器 | 已知型号 |

**耳机**

| 品牌 | 范围 |
| --- | --- |
| Jabra、FIFINE、西伯利亚 | 整个品牌 |
| 罗技（含 G PRO X 2）、赛睿（含 GameBuds X）、HyperX、海盗船（含 Virtuoso、HS80、Void 系列）、雷蛇、索尼 INZONE 与 PULSE Explore、华硕 ROG、ASTRO A50、Audeze Maxwell、Skullcandy PLYR、AKG N9、迈从（含 V9 PRO、G9 PRO）、狼蛛（G9、A9）、JBL Quantum 910、PULSE 3D、ATK（N9 Pro、N9 Ultra、N9 V2 系列）、联想、寸渊影弑、漫步者 Halo Space | 已知型号 |

**手柄**

| 品牌 | 范围 |
| --- | --- |
| Xbox、北通（BETOP）、飞智（Flydigi）、盖世小鸡（GameSir）、8BitDo | 整个品牌 |
| PlayStation（DualSense、DualSense Edge、DualShock 4）、Steam Controller、NVIDIA SHIELD、Xbox 360 无线接收器 | 已知型号 |

- 不少型号按公开的协议资料实现，尚未在实物上验证。读不到或读数不对时，欢迎在 [Issues](https://github.com/MoFeng2223/BrimDeck/issues) 中反馈型号，以及设备管理器中接收器的硬件 ID。
- 飞智手柄、Xbox 360 无线接收器与部分耳机只报告档位，电量显示为估算值。
- 设备休眠或超出接收范围时，显示上一次的电量。

## 读取方式与安全

- 只发送读取电量的请求，不发送任何修改设置的命令。
- 同一品牌有几种读取方式时，只在设备第一次出现时依次尝试，最多三轮；找到能用的方式后记录在本机，之后只用这一种，升级后继续有效。这种方式长期没有回应时，会再尝试一次其他方式。
- 读取时设备出现异常（设备随即断开，或 BrimDeck 在等待回应时退出），该读取方式不再发送；两种方式出现异常，该设备停止读取。
- 识别到但读不到电量的设备显示“未报告电量”。在设备列表中关闭“读取”后，不再向该设备发送任何请求。

## 显示与设置

- **电量阈值**：电量不高于预警阈值（默认 25%）时显示为偏低，使用预警色；不高于告警阈值（默认 15%）时显示为低电量，使用告警色。充电时显示闪电标记。
- **设备列表**：可以改名、拖动排序、设置各状态的颜色，分别选择是否在展开页和收起时显示，以及是否读取电量。调整过列表后，断开的设备仍保留在列表中，不再需要时可以“忘记”。
- **收起时**：在“设置 › 收起外观 › 显示内容”中为刘海或胶囊开启“设备电量”后，每个设备显示为一个电量环，可以选择是否同时显示数字。

## 致谢

部分协议移植自或参考了以下开源项目，许可证全文见 [ThirdPartyNotices.txt](../../src/BrimDeck/ThirdPartyNotices.txt)：

- [device-battery-viewer](https://github.com/CharlesGool/device-battery-viewer)（罗技 HID++）
- [razer-battery-report](https://github.com/xzeldon/razer-battery-report)（雷蛇）
- [ATK-A9-Battery](https://github.com/Lkig/ATK-A9-Battery)、[atkctl](https://github.com/cobanov/atkctl)（ATK、VXE）
- [InphicBatteryTray](https://github.com/Tan-TanZi/InphicBatteryTray)（英菲克）
- [aj179-linux](https://github.com/Rockeyxx/aj179-linux)（黑爵）
- [HaloBattery](https://github.com/HeyOkay/HaloBattery)、[vigil-watt](https://github.com/evren-oct/vigil-watt)、[mousedock](https://github.com/late-autumn-gif/mousedock)、[Voltide](https://github.com/sdparklab/Voltide)（多个品牌的鼠标、键盘、耳机与手柄）
- [mouse-battery-tray](https://github.com/incconutwo/mouse-battery-tray)（incconutwo）、[mouse-battery-tray](https://github.com/misaka310/mouse-battery-tray)（misaka310）
- [rapoo-tray](https://github.com/Iris-0109/rapoo-tray)、[mousectl](https://github.com/D3m0nZOnFire/mousectl)（雷柏）
- [rivalcfg](https://github.com/flozz/rivalcfg)（赛睿鼠标）
- [SDL](https://github.com/libsdl-org/SDL)（飞智、8BitDo、NVIDIA SHIELD、Xbox 360 手柄）
- [betop-battery](https://github.com/oitsukiii/betop-battery)（北通）
- [GamesirBatteryTray](https://github.com/WWWyyy452/GamesirBatteryTray)、[gamesir-battery-tray](https://github.com/agnostos-t/gamesir-battery-tray)（盖世小鸡）
- [darmoshark-m3-configurator](https://github.com/Barba2k2/darmoshark-m3-configurator)（Darmoshark）
- [Jingzao-JZM5-Battery](https://github.com/LANMIN-X/Jingzao-JZM5-Battery)（京东京造）
- [pulsar-x3-python](https://github.com/jonkristian/pulsar-x3-python)（Pulsar）
- [YesCharge](https://github.com/Yesid-Robayo/YesCharge)（CAROTMAS）
- [aula-keyboard-battery-tray](https://github.com/KhromotozzDevOut/aula-keyboard-battery-tray)、[Aula-F75-Max-Driver](https://github.com/VitalyArt/Aula-F75-Max-Driver)（狼蛛键盘）
- [mchose-macos](https://github.com/Klegus/mchose-macos)（迈从）
- [ShowMeTheConnection](https://github.com/NaoCoding/ShowMeTheConnection)（珂芝）
- [Headroom](https://github.com/patalbansishashank/Headroom)（Skullcandy、Audeze）、[eh-fifty](https://github.com/tdryer/eh-fifty)（ASTRO A50）
