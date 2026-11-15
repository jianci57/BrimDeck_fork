# System monitor readings

The system monitor page shows the CPU, GPU, memory, network, drives, temperatures, power, battery and frame rate. This page explains where each reading comes from and what its limits are. The settings are under Settings › Display and readings.

Every reading is read and shown only on this PC, never saved or sent. Unless stated otherwise, reading needs no administrator rights and installs no driver.

## Readings at a glance

| Reading | Default | Where to switch it | Notes |
| --- | --- | --- | --- |
| CPU, memory, download, upload | Shown | When expanded › What the page shows | The CPU also shows the usage of each core |
| GPU | Shown | When expanded › What the page shows | With more than one graphics card, choose which one shows |
| Busiest apps | Shown | When expanded › What the page shows › Process list | Sorted by CPU or memory; click an app for its details |
| Drives | Shown | When expanded › What the page shows › Drives | Internal drives only |
| System power and this PC's battery | Shown | When expanded › What the page shows › Power and battery | PCs without a battery show no battery |
| Temperatures | Hidden | When expanded › What the page shows › Temperatures | CPU, GPU and solid-state drives |
| Frame rate | Hidden | When expanded › What the page shows › Frame rate | Needs permission the first time |

The readings shown in the notch and capsule are chosen under "When collapsed".

## Choosing what the expanded page shows

Under "When expanded", "What the page shows" has a chip for each reading, clicked to show or hide it: CPU, GPU, memory, download, upload, frame rate, drives, power and battery, the process list, and temperatures. The temperatures are shown beside the CPU, the GPU and the drives.

"Order on the page" under it is a sketch of the expanded page, with a block for each part of the page: the CPU and GPU are one block, memory, download and upload another, and the frame rate, drives, and power and battery one each. Drag a block, or select it and press Alt+Up/Down, to move it up or down the page. Memory and the network share a block, memory on one line and the network on the next; on a very short panel they share one line, in the same order. The download and the upload sit side by side on the network's line: drag either across the other to change sides, or up or down to move memory, download and upload as one block. Drag memory up or down within the block to change it round with the network; dragged out of the block, it takes the whole block. The process list is drawn with a dashed outline: the panel's size places it, at the side or below, and it cannot be dragged. "Restore the default order" puts the order back without changing what is shown; it is grey while the order is the default.

- The checks here only affect the expanded page, and the checks under "When collapsed" only affect the notch and capsule; neither changes the other.
- The page lays itself out for what is chosen: a single reading fills the page; when room runs short, minor lines such as the frame rate and drives go first, then the network and memory are compressed, and nothing overlaps or leaves the page. Download and upload share one line and can be shown one at a time.
- With nothing chosen, the expanded page shows a note and an "Open settings" button, and asks for no data. A reading that is not chosen is not requested by the expanded page; collapsed rows and the delay-retention window still read what they need.
- Until you change these switches or the order, the expanded page is exactly as before the upgrade; the old switches for the process list, drives, power and battery, and frame rate are now part of "What the page shows".
- With both the CPU and the GPU shown, click the smaller one on the expanded page to choose which one is shown large.

## CPU and GPU

- CPU usage is worked out from the time the processor is busy, as Task Manager does from Windows 11 24H2.
- With more than one graphics card, the discrete one shows by default. Click the large GPU reading on the page to show the next card, or choose one under General › GPU.
- GPU usage comes from the graphics driver by default (NVIDIA, AMD, Intel), which matches nvidia-smi and HWiNFO; where the driver gives no reading, it is worked out as Task Manager does. If the driver's reading is wrong, choose Windows under Sources › GPU usage.

## Core display: columns and dot matrix

By default each of the CPU's logical processors is drawn as a thin column. A machine with many threads (192, for example) cannot fit that many columns, so the cores can be drawn as a dot matrix instead: one small square per logical processor, filled from the top down and then to the right with the fastest kind of core first. The brighter the square, the higher the load; a core at the warning or critical level takes that level's color. The matrix works on the expanded page, in the notch, the capsule and the side column, and on the expanded page pointing at a square shows the core's name, load and frequency (the collapsed readings never took the mouse, so they have no tooltip).

Choose it under "General > Core display":

- Auto (default): columns while there are few cores, exactly as before. The matrix takes over when the columns would be thinner than 2 device pixels (expanded page), or no longer fit the collapsed reading's room (about 37 cores and more). A usual 8 to 32 core PC is not affected.
- Columns: always columns.
- Dot matrix: always the matrix.

The squares are as large as the room allows, on whole device pixels at any display scale, with a one-pixel gap where there is room and touching where there is not. The frequency line of a column has no equivalent in the matrix; point at a square to see it.

## Traffic

On the expanded page, the network line shows the traffic over a period after the download and upload rates, such as "Today 2.31 GB"; with less room it shows the figure alone, and with less still only in the tooltip. In the collapsed row, the readings table has "Downloaded" and "Uploaded" rows of their own, shown with a double arrow; turn either on alongside the rates or without them.

Choose the period under General › Traffic period:

- Today: from midnight local time, starting over each midnight.
- 1 day, 7 days, 30 days: that long back from now.

The traffic comes from Windows' own record, the one the Data usage page in Windows Settings shows, so it includes the time BrimDeck was not running and does not start over when it restarts. Wi-Fi, Ethernet and cellular networks are counted; a VPN or another virtual network is not counted again, as its traffic is already counted on the network it runs over. Windows updates its record a little later, so traffic that has just happened may be counted some time afterwards.

## Drives

- Shows the space in use for each drive letter; when the page has no room for them all, the remaining letters share one line.
- Only internal drives are listed, not USB sticks, SD cards, external, network or optical drives.

## Temperatures

"Show temperatures" is off by default. Temperatures that cannot be read are not shown.

| Item | Source |
| --- | --- |
| CPU | By default, the ACPI thermal zones the motherboard firmware provides. Many PCs do not provide them, and on some the figure is the motherboard's rather than the CPU's own |
| GPU | The temperature the graphics driver reports to Windows, as Task Manager shows it; where there is none, the interfaces that NVIDIA's, AMD's and Intel's drivers ship |
| Drives | The temperature a solid-state drive reports. Drives in RAID mode (for example Intel Rapid Storage Technology) are not asked for a temperature |

When the CPU temperature cannot be read, there are two ways to get it:

- Turn on [enhanced sensors](enhanced-sensors.md), so BrimDeck's sensor service reads it from the processor.
- Run HWiNFO (with shared memory on) or LibreHardwareMonitor (with its remote web server on); BrimDeck reads the figure from them.

The tooltip of a temperature names its source.

If the PC freezes while a temperature or a power figure is being read, BrimDeck stops reading that item the next time it starts. Stopped items are listed under "When expanded" and can be turned back on one by one.

## System power

The page shows the whole machine's power draw in watts with the last minute as a curve; the tooltip lists what the figure is made of and where it was read. The figure is taken in this order:

1. **The PC's own power meter**: the whole machine's draw, measured. Only a few tablets and servers have one.
2. **The battery's discharge**: while a laptop runs on battery, what the battery supplies is what the machine draws.
3. **The sum of the components**: the processor's power plus the board power of discrete graphics cards (NVIDIA, AMD, Intel). This is an estimate that leaves out the display, drives, fans and power-supply loss, and the tooltip says so.

Power is read only while the system monitor page is open or the notch or capsule shows it. An NVIDIA card's power is read through nvidia-smi, which ships with its driver, and only while the card is working, so a sleeping discrete card is never woken.

PCs whose CPU power cannot be read (some AMD PCs, for example) can turn on [enhanced sensors](enhanced-sensors.md) for that too.

## This PC's battery

Laptops and tablets show their own battery at the right end of the power line:

- The charge, followed by the time left or the time to full where Windows gives one. While the PC is plugged in, the battery icon shows a plug.
- The tooltip has the charge or discharge rate, the Windows power mode, Battery saver and the battery's health (full-charge capacity against design capacity).
- While the PC runs on battery, a charge of 25% or less takes the warning color and 15% or less the critical color; a plugged-in battery never takes either.

This is the PC's own battery. For wireless mice, keyboards, headsets and other devices, see [Supported devices](devices.md).

## Frame rate

With "Show the frame rate" on, the page shows the frame rate of the program in front under the network rates, with its name and its display's refresh rate; while that program draws nothing, it shows the last program that did.

Reading frame rates needs the current user in Windows' Performance Log Users group. The first time you turn it on, Windows asks an administrator to confirm once, and it works from the next sign-in. The frame rate is read only while the system monitor page is open or the notch or capsule shows it.

## Colors

The readings' colors follow the panel theme by default; presets and your own colors are under "General".
