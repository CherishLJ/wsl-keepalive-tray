# WSL KeepAlive Tray Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use executing-plans to implement this plan task-by-task.

**Goal:** Build, install, and verify a console-free WSL keepalive tray application with live resource telemetry and an in-WSL systemd watchdog timer.

**Architecture:** A .NET Framework 4.8 WinForms `WinExe` owns one persistent hidden `wsl.exe` telemetry process. A Python standard-library agent emits JSON metrics, while a systemd timer runs the existing root watchdog every five minutes.

**Tech Stack:** C# 5, WinForms, System.Drawing, JavaScriptSerializer, Python 3 standard library, systemd, PowerShell installer.

---

### Task 1: Linux telemetry agent

**Files:**
- Create: `linux/wsl-tray-agent`
- Create: `linux/wsl-tray-watchdog.service`
- Create: `linux/wsl-tray-watchdog.timer`

**Steps:**
1. Implement `/proc` sampling and newline-delimited JSON output.
2. Add `--once`, `--interval`, and schema-version support.
3. Run `python3 linux/wsl-tray-agent --once`; expect valid JSON with all required metrics.
4. Run for three samples and confirm non-negative throughput deltas.

### Task 2: Telemetry model and process supervision

**Files:**
- Create: `src/TelemetrySnapshot.cs`
- Create: `src/WslAgentSupervisor.cs`
- Create: `src/AppLog.cs`

**Steps:**
1. Implement tolerant JSON parsing and user-facing format helpers.
2. Launch `wsl.exe` with `UseShellExecute=false`, `CreateNoWindow=true`, and redirected handles.
3. Add restart backoff and intentional-stop state.
4. Add `--self-test`; expect parser, formatter, and state tests to pass.

### Task 3: Tray interaction

**Files:**
- Create: `src/Program.cs`
- Create: `src/TrayApplicationContext.cs`
- Create: `src/IconFactory.cs`
- Create: `src/AutostartManager.cs`

**Steps:**
1. Implement single-instance startup and HKCU autostart.
2. Implement dynamic status icons and bounded hover text.
3. Add live metric menu rows and operational commands.
4. Verify the process subsystem is Windows GUI and no console is created.

### Task 4: Dashboard UI

**Files:**
- Create: `src/DashboardForm.cs`
- Create: `src/SparklineControl.cs`

**Steps:**
1. Build the compact dark dashboard and metric cards.
2. Maintain 120 telemetry points and draw four rolling charts.
3. Add accessible labels and high-DPI layout behavior.
4. Launch the dashboard, capture it, and inspect the rendered image.

### Task 5: Build and packaging

**Files:**
- Create: `scripts/build.ps1`
- Create: `scripts/generate-icon.ps1`
- Create: `assets/app.ico`
- Create: `app.manifest`

**Steps:**
1. Generate the deterministic executable icon.
2. Compile with the inbox .NET Framework 4.8 `csc.exe` using `/target:winexe`.
3. Run `WSLKeepAliveTray.exe --self-test`; expect exit code 0.
4. Inspect PE subsystem; expect Windows GUI.

### Task 6: Installation and migration

**Files:**
- Create: `scripts/install.ps1`
- Create: `scripts/uninstall.ps1`
- Create: `README.md`

**Steps:**
1. Back up the existing scheduled task XML and Linux watchdog.
2. Install the agent and systemd units; enable and start the timer.
3. Install the application to a stable user path and register autostart.
4. Start the tray and confirm fresh telemetry.
5. Disable the old five-minute task only after all checks pass.

### Task 7: End-to-end verification

**Steps:**
1. Verify hover/menu/dashboard values update every two seconds.
2. Verify Docker, SSH, containers, and ports remain healthy.
3. Terminate the distro and confirm the tray restores it without a visible console.
4. Observe foreground ownership during two telemetry intervals; expect no focus-stealing console.
5. Verify systemd timer history and watchdog journal entries.
6. Publish the source, executable, install scripts, README, checksum manifest, and screenshot to `outputs/WSL-KeepAlive-Tray`.

