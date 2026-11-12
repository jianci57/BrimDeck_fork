# 设备电量支持

设备电量页显示已连接的无线鼠标、键盘、耳机、手柄和触控笔的电量；刘海与胶囊也可以显示。相关设置在“设置 › 显示与设备”中。

BrimDeck 只读取电量：不会配对、断开或改动任何设备与 Windows 设置，不需要管理员权限，也不安装驱动。只在设备电量页打开、刘海或胶囊显示设备电量，或“设置 › 显示与设备”打开时读取。

## 支持的连接方式

| 连接方式 | 读取方法 | 说明 |
| --- | --- | --- |
| 蓝牙 | Windows 自己读取的电量，与“Windows 设置 › 蓝牙和设备”中显示的一致；蓝牙低功耗设备另外读取其电池服务 | 只列出已连接的设备。Windows 没有电量的设备显示“未报告电量” |
| AirPods、Beats | 耳机在出盒或开盖时发出的蓝牙广播 | 分别显示左右耳与充电盒。只在耳机已连接且靠近电脑时读取 |
| 2.4G 接收器或数据线 | 各品牌设备自己的协议 | 只支持下表列出的品牌中已知的型号 |
| 游戏手柄 | Windows 的游戏控制器接口；PlayStation 手柄读取其输入报告 | Xbox 手柄（Xbox 无线适配器或数据线）、PlayStation 手柄，以及其他 Windows 能给出电量的手柄 |

带有标准电量报告的 HID 设备也会显示。

## 通过接收器或数据线支持的品牌

| 类型 | 品牌 |
| --- | --- |
| 鼠标、键盘与耳机 | 罗技（Unifying、Bolt、Lightspeed 接收器与数据线）、华硕 ROG |
| 鼠标与键盘 | 雷蛇 |
| 鼠标 | ATK、VXE、VGN、Pulsar、迈从（MCHOSE）、Endgame Gear、VAXEE、英菲克（Inphic）、黑爵（AJAZZ）、WLmouse、G-Wolves、LAMZU、Ninjutso、LINGBAO、NZXT、HECATE、达尔优（Dareu）、赛睿（SteelSeries）、雷柏（Rapoo）、Attack Shark、多彩（DELUX）、海盗船（Corsair） |
| 键盘 | VGN、ATK Z87 Pro、狼蛛 AULA F87 PRO、CHERRY PIXIU75 |
| 耳机 | 赛睿（SteelSeries）、HyperX、迈从（MCHOSE）、海盗船（Corsair）、狼蛛 AULA G9、JBL Quantum 910、PULSE 3D |
| 手柄 | Xbox、PlayStation、Steam Controller |

- 每个品牌只支持已知的型号，同一品牌的其他型号不一定能读到电量。
- 部分型号按公开的协议资料实现，尚未在实物上验证。读不到电量或读数不正确时，欢迎在 [Issues](https://github.com/MoFeng2223/BrimDeck/issues) 中反馈设备型号。
- 设备休眠或超出接收范围时，显示接收器记录的上一次电量。

## 显示与设置

- **电量阈值**：电量不高于预警阈值（默认 25%）时显示为电量偏低，使用预警色；不高于告警阈值（默认 15%）时显示为电量低，使用告警色。充电时显示闪电标记。
- **设备列表**：可以为设备改名、拖动调整顺序、分别设置各状态的颜色，并分别选择是否在展开页和收起时显示。在列表中调整过之后，断开连接的设备仍保留在列表中，不再需要时可以选择“忘记”。
- **收起时**：在“设置 › 收起外观 › 显示内容”中为刘海或胶囊开启“设备电量”后，每个设备显示为一个电量环；可以选择是否同时显示数字。

## 致谢

部分协议移植自或参考了以下开源项目，许可证全文见 [ThirdPartyNotices.txt](../../src/BrimDeck/ThirdPartyNotices.txt)：

- [device-battery-viewer](https://github.com/CharlesGool/device-battery-viewer)（罗技 HID++）
- [razer-battery-report](https://github.com/xzeldon/razer-battery-report)（雷蛇）
- [ATK-A9-Battery](https://github.com/Lkig/ATK-A9-Battery)、[atkctl](https://github.com/cobanov/atkctl)（ATK、VXE）
- [InphicBatteryTray](https://github.com/Tan-TanZi/InphicBatteryTray)（英菲克）
- [aj179-linux](https://github.com/Rockeyxx/aj179-linux)（黑爵）
- [HaloBattery](https://github.com/HeyOkay/HaloBattery)、[vigil-watt](https://github.com/evren-oct/vigil-watt)（多个品牌的鼠标、键盘与耳机）
- [mouse-battery-tray](https://github.com/incconutwo/mouse-battery-tray)（incconutwo）、[mouse-battery-tray](https://github.com/misaka310/mouse-battery-tray)（misaka310）、[mousedock](https://github.com/late-autumn-gif/mousedock)
- [rapoo-tray](https://github.com/Iris-0109/rapoo-tray)、[mousectl](https://github.com/D3m0nZOnFire/mousectl)（雷柏）
- [rivalcfg](https://github.com/flozz/rivalcfg)（赛睿鼠标）
