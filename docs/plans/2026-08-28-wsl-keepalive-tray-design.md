# WSL KeepAlive Tray Design

## Goal

Provide a zero-console Windows tray application that starts and keeps a selected WSL distro alive (default: `Ubuntu-24.04`), displays live resource telemetry, exposes operational controls, and delegates in-WSL service recovery to systemd.

## Architecture

The Windows application is a .NET Framework 4.8 WinForms `WinExe` with no third-party dependencies. It starts one hidden, redirected `wsl.exe` child process that runs `/usr/local/sbin/wsl-tray-agent --interval 2`. The persistent child is both the keepalive handle and a newline-delimited JSON telemetry stream. If it exits unexpectedly, the application restarts it with bounded backoff.

The Linux agent samples `/proc` and standard library APIs every two seconds. It reports CPU utilization, 1/5/15-minute load, memory and swap use, root filesystem use, root-device disk throughput, default-interface network throughput, uptime, systemd state, SSH state, and Docker container counts. Expensive service checks are cached for ten seconds.

The repository installs `/usr/local/sbin/wsl-tray-watchdog` as the service repair script. A systemd oneshot service and five-minute timer invoke it inside WSL, eliminating the Windows five-minute PowerShell task. Services and optional container names are configured in `/etc/default/wsl-keepalive-tray`; the old scheduled task is exported before it is disabled.

## User experience

The tray icon is drawn in code at native icon resolution: a dark rounded tile with a white pulse mark and a green, amber, gray, or red status indicator. Hover text contains the distro state and compact CPU, memory, and network figures. The right-click menu begins with live read-only metric rows, followed by commands for the dashboard, immediate health check, terminal, logs, start, restart, stop, autostart, exit, and exit-and-stop.

Double-clicking opens a compact dark dashboard. It contains status and uptime, CPU and load, memory and swap, network and disk throughput, root filesystem and Docker/SSH health, plus short rolling CPU, memory, network, and disk charts. Metrics update every two seconds without opening a console window.

## Safety and recovery

The application never requires administrator rights. Autostart uses HKCU. Installing Linux files uses the existing root-capable WSL command path and preserves backups. The scheduled task is disabled only after the tray process, telemetry stream, systemd timer, Docker containers, ports, and restart recovery have all been verified. An uninstall script restores the previous task state and removes only files installed by this project.
