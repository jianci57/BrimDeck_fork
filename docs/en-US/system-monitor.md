# System monitor readings

The system monitor page shows the CPU, GPU, memory, network, drives, temperatures, power, battery and frame rate. This page explains where each reading comes from and what its limits are. The settings are under Settings › Display and readings.

Every reading is read and shown only on this PC, never saved or sent. Unless stated otherwise, reading needs no administrator rights and installs no driver.

## Readings at a glance

| Reading | Default | Where to switch it | Notes |
| --- | --- | --- | --- |
| CPU, memory, network speed | Shown | — | The CPU also shows the usage of each core |
| GPU | Shown | General › GPU | With more than one graphics card, choose which one shows |
| Busiest apps | Shown | When expanded › Show the process list | Sorted by CPU or memory; click an app for its details |
| Drives | Shown | When expanded › Show drives | Internal drives only |
| System power and this PC's battery | Shown | When expanded › Show power and battery | PCs without a battery show no battery |
| Temperatures | Hidden | When expanded › Show temperatures | CPU, GPU and solid-state drives |
| Frame rate | Hidden | When expanded › Show the frame rate | Needs permission the first time |

The readings shown in the notch and capsule are chosen under "When collapsed".

## CPU and GPU

- CPU usage is worked out from the time the processor is busy, as Task Manager does from Windows 11 24H2.
- With more than one graphics card, the discrete one shows by default. Click the large GPU reading on the page to show the next card, or choose one under General › GPU.
- GPU usage comes from the graphics driver by default (NVIDIA, AMD, Intel), which matches nvidia-smi and HWiNFO; where the driver gives no reading, it is worked out as Task Manager does. If the driver's reading is wrong, choose Windows under Sources › GPU usage.

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
