# StrafeLab

StrafeLab is a local Windows timing analyzer for A/D counter-strafe practice. It records only A, D, left mouse, right mouse, and raw mouse movement while a visible session is running, then saves local CSV/JSON session files for single-session and long-term analysis.

## What it measures

- `A -> D` and `D -> A` transitions
- Release-to-opposite-key timing in milliseconds
  - Negative = overlap: opposite key was pressed before the old key was released
  - Positive = gap: old key was released before the opposite key was pressed
- Left-click timing relative to the counter-strafe
- Raw mouse movement from the counter-strafe key press until first left click
- Mouse trace overlay in the UI
  - Unique trace lines on/off
  - Average trace on/off
  - Click endpoint dots on/off
  - Last N traces selector
- Calibrated aim metrics using user-entered game values
  - DPI
  - sensitivity
  - yaw degrees-per-count at sensitivity 1.0
  - pitch degrees-per-count at sensitivity 1.0
  - optional multiplier, for scoped or special modes
- Session stats and 7-day aggregate stats
- Coaching tips based on the latest attempts

## Game calibration

The calibration panel converts raw mouse counts to estimated in-game degrees:

```text
horizontal_degrees = raw_x_counts * sensitivity * yaw * multiplier
vertical_degrees   = raw_y_counts * sensitivity * pitch * multiplier
counts_per_360     = 360 / (sensitivity * yaw * multiplier)
cm_per_360         = counts_per_360 / DPI * 2.54
```

The default yaw/pitch values are `0.022`, which match common Source/Counter-Strike style defaults. If your game uses different yaw/pitch conversion values, enter those instead. If you are not sure, leave multiplier at `1.0`.

## Saved files

Each session is saved under:

```text
%LOCALAPPDATA%\StrafeLab\sessions\<session-id>
```

Files:

- `summary.json` - session totals, calibration, timing averages, and mouse-trace aggregates
- `events.csv` - all captured A/D/M1/M2/mouse-move events with raw deltas
- `attempts.csv` - one row per counter-strafe attempt, including calibrated aim/path metrics
- `mouse_traces.csv` - one row per mouse trace point, linked by attempt index

## Safety and privacy design

- No network code
- No startup persistence
- No hidden/background mode
- No typed text logging
- Filters keyboard storage to A and D only
- Mouse movement is stored only during visible user-started sessions
- Saves to `%LOCALAPPDATA%\StrafeLab\sessions`

Use this for offline/practice analysis. Do not use it to bypass anti-cheat systems or competitive-game rules. Some games and anti-cheat products may block or dislike external input tools even when they are benign.

## Requirements

- Windows 10/11
- .NET 8 SDK for building, or publish a self-contained executable
- Visual Studio 2022 is optional but convenient

## Build

From the project folder on Windows:

```powershell
dotnet restore
dotnet build -c Release
```

## Publish a single EXE

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The executable will be under:

```text
bin\Release\net8.0-windows\win-x64\publish\StrafeLab.exe
```

## How to use

1. Launch StrafeLab.
2. Enter your DPI, sensitivity, yaw, pitch, and multiplier in **Game calibration**.
3. Click **Start Session**.
4. Practice A/D strafes, counter-strafes, mouse correction, and clicks.
5. Watch the **Mouse trace overlay** to compare lines from counter key to click.
6. Click **Stop + Save**.
7. Use **Open Data Folder** to inspect `summary.json`, `events.csv`, `attempts.csv`, and `mouse_traces.csv`.

## Default timing windows

- Counter delay: 0 to 80 ms after release
- Click delay: 0 to 160 ms after opposite key press

You can tune these in the UI before pressing **Start Session**.

## Accuracy notes

The app uses Windows Raw Input and timestamps the message as soon as it is received in the WPF message loop. This is suitable for personal timing analysis, but it is not a hardware logic analyzer. Absolute latency is affected by OS scheduling, USB polling rate, system load, and whether another high-priority application is saturating the CPU/GPU.

Mouse movement is stored as raw relative counts. The calibrated degree values are estimates based on the game values you enter; they are only as accurate as those values and the game's actual input pipeline.

For higher accuracy later, the next upgrade would be a native C++ event collector using `GetRawInputBuffer`, QueryPerformanceCounter timestamps, and a separate UI process.
