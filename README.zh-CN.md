<div align="center">

<a href="README.md"><img src="https://img.shields.io/badge/English-3A3A3C?style=for-the-badge" alt="English"></a>
<a href="README.zh-CN.md"><img src="https://img.shields.io/badge/%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-0A84FF?style=for-the-badge" alt="简体中文"></a>

<img src="docs/icon.png" width="96" alt="BrimDeck 图标">

# BrimDeck - Windows 灵动岛

BrimDeck 是 Windows 11 上的灵动岛，在屏幕顶部集中显示 AI 编程工具的额度与用量，以及正在播放的音乐。<br>
平时收起在屏幕顶部，只在需要时展开。<br>
Windows 原生应用，使用 C# 与 WPF 开发，注重性能与流畅度。

[![下载](https://img.shields.io/badge/下载-Windows%2011%20x64-9CB9FF?style=flat-square)](https://github.com/MoFeng2223/BrimDeck/releases/latest)
[![最新版本](https://img.shields.io/github/v/release/MoFeng2223/BrimDeck?style=flat-square&label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC&color=98DBB0)](https://github.com/MoFeng2223/BrimDeck/releases/latest)
[![许可证](https://img.shields.io/badge/许可证-Apache%202.0-E5A385?style=flat-square)](LICENSE)
![界面语言](https://img.shields.io/badge/界面-简体中文%20%7C%20English-B9A8FF?style=flat-square)

<p><a href="#功能">功能</a> · <a href="#安装">安装</a> · <a href="#快速开始">快速开始</a> · <a href="#隐私">隐私</a> · <a href="#常见问题">常见问题</a> · <a href="#参与开发">参与开发</a> · <a href="README.md">English</a></p>

<img src="docs/zh-CN/images/hero.png" width="100%" alt="BrimDeck 从屏幕顶部的刘海展开为 AI 用量面板，依次切换到音乐页和系统监控页，最后收起">

</div>

## 为什么选择 BrimDeck

- **额度集中显示**：Claude、Codex、Cursor、Antigravity 等工具的额度集中在一个面板上，不必逐个打开网页、命令行或应用查看。
- **简化额度查询操作**：平时收起在屏幕顶部，鼠标移上去即展开，移开即收起。
- **媒体控制**：内联预览与控制正在播放的歌曲，封面、律动条与歌词随音乐变化。律动条可以随声音流动，也可以按音高从低到高显示频谱。
- **系统监控**：CPU、内存、网速等读数常驻在刘海或胶囊中，一眼即可看到。
- **全屏免打扰**：全屏玩游戏或看视频时自动隐藏，避免误触展开；可在设置中调整。
- **开箱即用**：自动识别本机已登录的 Claude、Codex、Cursor 等工具，无需填写密钥或重新登录。
- **Windows 原生**：基于 .NET 与 WPF 构建，以较低的资源占用提供流畅的动画体验。

## 功能

### 收起形态

BrimDeck 平时位于屏幕顶部中央，提供刘海（默认）、胶囊和指示条三种样式。

<table>
  <tr>
    <td align="center"><img src="docs/zh-CN/images/compact-notch.png" width="279" alt="刘海"><br>刘海（默认）</td>
    <td align="center"><img src="docs/zh-CN/images/compact-capsule.png" width="258" alt="胶囊"><br>胶囊</td>
    <td align="center"><img src="docs/zh-CN/images/compact-line.png" width="128" alt="指示条"><br>指示条</td>
  </tr>
</table>

- 刘海与胶囊通过配额环显示各工具的最高配额使用率；在收起设置的“配额环”下可以打开“环内显示首字母”，在环中写出工具显示名称的首个字母或数字（默认关闭）。还支持音乐预览，可显示封面、律动条、歌名或当前歌词。

  <img src="docs/zh-CN/images/compact-notch-lyrics.png" width="279" alt="刘海中显示歌词">

- 刘海与胶囊可以显示时间，与配额环或音乐一起显示时位于右侧，单独显示时居中；可以自定义配色。

  <img src="docs/zh-CN/images/compact-clock.png" width="567" alt="刘海中的时间：简约、数码管、衬线样式位于配额环右侧，单独显示时居中">

- 某个工具的配额使用率升到预警（70%）或告警（90%）时，收起形态会显示提醒 5 秒；可在设置中关闭。

  <img src="docs/zh-CN/images/compact-alert.png" width="388" alt="配额提醒：Claude 5 小时已用 91%">

- 刘海与胶囊可以显示系统读数，与配额环、音乐并排显示。

  <img src="docs/zh-CN/images/compact-monitor.png" width="474" alt="刘海中的系统读数：配额环旁显示核心柱、CPU、内存、GPU 与网速">

- 刘海、胶囊和指示条的尺寸与位置可在设置中调整。

- 窗口最大化时默认切换为指示条，全屏游戏或视频播放时默认隐藏，避免误触展开。

### AI 用量

<img src="docs/zh-CN/images/usage.png" width="100%" alt="AI 用量页：Claude、Codex、Cursor、Antigravity 四列配额">

<img src="docs/zh-CN/images/details.png" width="100%" alt="模型明细页：按应用和模型列出 Token 用量与费用">

- 按工具分列展示套餐、配额使用率与重置倒计时，最多同时显示 7 个工具。
- 每列底部汇总今日和最近 7 天的 Token 用量与费用估算。
- 可查看各模型的用量与费用明细，支持按工具和日期范围统计。

可自定义配额提供商，并分别设置各列的配额与用量统计来源，详见[支持的工具](docs/zh-CN/ai-tools.md)。

### 音乐

<img src="docs/zh-CN/images/music.png" width="63%" alt="音乐页：封面、歌名、歌词、进度条和播放控制">

- 显示歌曲信息、同步歌词与播放进度，支持播放控制、播放模式切换，以及受支持播放器的进度调节。
- 支持同时接入多个播放器，可手动切换或固定显示其中一个。

支持接入 Windows 系统媒体控制的播放器，并对部分播放器做了专门优化，详见[播放器支持](docs/zh-CN/music-players.md)。

### 系统监控

<img src="docs/zh-CN/images/monitor.png" width="100%" alt="系统监控页：CPU 占用与各核心使用率、GPU、内存、网速，以及占用最高的应用">

- 实时显示 CPU、GPU、内存的占用和网络速度，以及每个核心的使用率。有多块显卡时默认显示独显；点击大号 GPU 读数可切换到下一块，也可在“设置 › 系统监控 › 显卡”中指定。
- 按 CPU 或内存列出占用最高的应用，点击应用可查看详情。
- 按物理硬盘逐行显示已用空间，一块硬盘分成多个盘符时按盘符分段；只列内置硬盘，不含 U 盘、SD 卡、移动硬盘和网络盘。
- 显示 CPU、GPU 和固态硬盘的温度（可在设置中关闭；刘海或胶囊中的 CPU、GPU 和硬盘读数也可显示温度）。无需管理员权限，也不安装驱动；读不到的项不显示。GPU 温度与任务管理器相同；CPU 温度来自主板固件的 ACPI 热区，可能是主板而非 CPU 本身的温度，许多电脑不提供。
- 显示整机功耗（单位 W），带最近一分钟的曲线，以及打开页面后的峰值；悬停提示另有平均值和读数的组成（“设置 › 系统监控 › 显示功耗”；刘海或胶囊也可显示）。读数优先取电脑自带的功率计；没有则取笔记本用电池供电时的电池放电功率；再没有就由 CPU 功率和 NVIDIA 显卡的板卡功率估算（悬停提示中注明），估算不含显示器、存储、风扇和电源损耗。只在页面或刘海、胶囊显示它时读取，NVIDIA 显卡只在其有负载时才询问；无需管理员权限，也不安装驱动。
- 笔记本和平板还显示本机电池：电量（按主题风格画的电池，接通电源时电池中间显示插头），系统给出时间时其后显示剩余时间或充满所需时间；电池位于功耗这一行的右端。悬停提示中有充放电功率、Windows 电源模式、节电模式和电池健康度（满充容量与设计容量之比）。刘海或胶囊也可显示电量（“设置 › 系统监控 ›”读数表）。用电池供电时电量降到 25% 及以下用警告色、15% 及以下用临界色；电量正常、充电或开启节电模式时不会用这两种颜色。没有电池的电脑不显示这一项。
- 配色默认跟随面板主题，也提供多种预设配色，并支持自定义。

本页参考并改编自 Nongfsq 的 macOS 系统监控 [ftop](https://github.com/Nongfsq/ftop)（MIT 许可证）。

### 面板主题

<img src="docs/zh-CN/images/themes.png" width="100%" alt="同一个 AI 用量页在深色、浅色、Night Owl、Dracula、赛博朋克：边缘行者主题，以及一个自定义主题示例下的样子">

- 内置深色、浅色、One Dark Pro、GitHub Dark、GitHub Light、Dracula、Night Owl、赛博朋克：边缘行者等主题。
- 可以在设置的“面板主题”页修改背景、前景、强调色等颜色，并保存为自定义主题；也可以按[主题制作指南](docs/zh-CN/themes.md)编写主题文件。

## 安装

需要 Windows 11（x64）。对 Windows 10（2004 及以上版本）提供有限支持。

1. 从 [Releases](https://github.com/MoFeng2223/BrimDeck/releases/latest) 下载以下任一文件：
   - 安装版：`BrimDeck-<版本>-Setup.exe`，运行后按提示安装。
   - 免安装版：`BrimDeck-<版本>-Portable-x64.zip`，解压后运行 `BrimDeck` 文件夹中的 `BrimDeck.exe`。
2. 如出现“Windows 已保护你的电脑”提示，点击“更多信息”→“仍要运行”。

两种版本都可以在应用内更新，设置和数据都保存在 `%LOCALAPPDATA%\BrimDeck`。应用内更新默认只提示正式版；在设置的“关于”页开启“接收测试版更新”后，也会提示 Beta 测试版。免安装版放在需要管理员权限才能修改的文件夹（例如 `C:\Program Files`）中时，更新会请求管理员确认。不再使用免安装版时，先在设置中关闭开机自启动（如已开启），再删除它的文件夹；如需同时删除设置和数据，再删除 `%LOCALAPPDATA%\BrimDeck`。

## 快速开始

1. 安装并启动 BrimDeck 后，面板会出现在主显示器顶部中央，默认显示 Claude 和 Codex。
2. 将鼠标移到面板上即可展开，移开鼠标或按 Esc 收起。
3. 点击展开面板右上角的设置按钮，即可添加工具或调整设置；也可在展开的面板上点击右键，或双击系统托盘图标、通过托盘图标的右键菜单进入设置。

## 卸载

安装版：前往 Windows 设置中的“应用”卸载。免安装版：先在设置中关闭开机自启动（如已开启），再删除它的文件夹。

## 隐私

- 仅读取应用已有的登录状态与用量记录，不会修改、续期或注销它们的登录。
- 读取的登录令牌仅在内存中使用，不写入磁盘；手动填写的 API 密钥使用 Windows 的加密功能保存在本机。
- 仅在获取配额与用量、同步模型单价、获取歌词与封面、检查更新时联网，不会上传会话内容，也不会收集使用数据。

读取了哪些文件、会连接哪些地址，详见[数据与隐私](docs/zh-CN/privacy.md)。

## 常见问题

**某个工具显示"未连接"或"等待更新"怎么办？**
请确认该工具已在这台电脑上登录；例如 Antigravity 读取配额时需要桌面版正在运行。BrimDeck 读取不到数据时，只显示实际状态，不会显示虚假的数值。

**显示的费用是我实际支付的金额吗？**
不是。费用按接口金额或公开的模型单价计算，适合用来了解用量高低；订阅套餐的实际扣费以各服务商的账单为准。

## 参与开发

欢迎通过 [Issues](https://github.com/MoFeng2223/BrimDeck/issues) 反馈问题、提出功能建议，或申请适配更多工具与播放器，也欢迎提交 Pull Request 参与开发。

- Pull Request 不要直接合入 `main`：
  - 新功能和一般修复以 `dev` 分支为目标。
  - 某个版本进入测试阶段后，会有对应的 `release/<版本>` 分支（例如 `release/1.3.0`）。针对这个版本的问题修复以该分支为目标；该分支只接受问题修复，不接受新功能。
- 后续规划见 [BrimDeck 路线图](https://github.com/MoFeng2223/BrimDeck/discussions/57)。功能建议可以在帖子下讨论，想认领其中某项功能时，也可以直接在帖子下留言。其他话题可以在 [Discussions](https://github.com/MoFeng2223/BrimDeck/discussions) 中讨论。

## 许可证

BrimDeck 采用 [Apache License 2.0](LICENSE)。第三方组件的许可证见 [ThirdPartyNotices.txt](src/BrimDeck/ThirdPartyNotices.txt)。

系统监控页参考并改编自 Nongfsq 的 [ftop](https://github.com/Nongfsq/ftop)，ftop 采用 MIT 许可证，感谢原作者。
