# ThermoWatch V2 — Hardware Telemetry & Temperature Monitor

A lightweight C# desktop application built with WPF to monitor real-time CPU and GPU temperatures and system diagnostic metrics.

## Key Features
- **Real-Time Sensor Polling:** Reads thermal sensor data and system metrics directly from hardware components.
- **Hardware Integration:** Utilizes `LibreHardwareMonitorLib` and low-level HID communication.
- **System Tray Integration:** Minimizes seamlessly to the Windows notification area (System Tray).
- **Threshold Alerts:** Triggers native Windows toast notifications when critical temperature spikes occur.
- **Modern User Interface:** Built with WPF for a clean, non-intrusive monitoring experience.

## Built With
- **C# / .NET 8.0**
- **WPF (Windows Presentation Foundation)**
- **LibreHardwareMonitorLib**
- **Hardcodet.NotifyIcon.Wpf**
- **Microsoft.Toolkit.Uwp.Notifications**

## How to Run Locally
1. Clone the repository:
   ```bash
   git clone https://github.com/07Mustafaaa/ThermoWatchV2.git