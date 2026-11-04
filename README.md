# DLPPC Controller

Standalone Windows utility for reading and toggling NVIDIA's `DLPPC_1X` PERF_CF policy.

- Windows 10/11 x64
- .NET 10
- WinForms GUI and command-line interface
- No MechControl dependency
- Uses the NVIDIA driver's system `nvapi64.dll`
- No registry tuning of GPU parameters, VBIOS modification, driver patching, or third-party tuning tools

## Important compatibility notice

The PERF_CF functions used here are private/undocumented NVAPI interfaces. The structure layouts and offsets were reverse-engineered and observed on a limited driver/GPU configuration (the source commit documents an RTX 5090 Laptop with driver 617.14). NVIDIA may change these interfaces without notice.

This project does **not** assume that every RTX 5070 Ti or driver exposes DLPPC. It discovers the `DLPPC_1X` label dynamically, validates structure versions, policy identity, buffer bounds and the readback after a write. If any check fails, it stops rather than guessing.

The tool does not whitelist a specific PCI device ID. It reports the detected GPU name and device ID, because laptop OEMs and driver branches may expose different IDs. Verify the detected GPU is your RTX 5070 Ti Laptop before changing the policy.

## Build

Install the .NET 10 SDK on Windows, then run:

```powershell
dotnet restore .\DLPPCController\DLPPCController.csproj
dotnet build .\DLPPCController\DLPPCController.csproj -c Release
```

## Publish a self-contained single-file EXE

```powershell
dotnet publish .\DLPPCController\DLPPCController.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

Output:

`DLPPCController\bin\Release\net10.0-windows\win-x64\publish\DLPPCController.exe`

The .NET runtime is bundled. The NVIDIA display driver is still required.

## GUI

Run `DLPPCController.exe` without arguments.

- **Enable** activates the DLPPC policy.
- **Disable** deactivates the DLPPC policy.
- **Refresh** reads the current state.
- **Apply saved state at startup** applies the saved disabled/enabled preference when the GUI starts.
- **Watchdog** polls the driver every five seconds and disables DLPPC again if it becomes active.
- **Start with Windows** adds/removes the current-user Run entry.

The watchdog is opt-in. NVAPI polling can wake the discrete GPU and may prevent it from remaining in a low-power/off state. This standalone version does not include MechControl's Windows device-power-state detection, so do not enable the watchdog if avoiding GPU wake-ups is more important than automatic reapplication.

## CLI

```powershell
DLPPCController.exe help
DLPPCController.exe status
DLPPCController.exe disable
DLPPCController.exe enable
DLPPCController.exe toggle
DLPPCController.exe watch
DLPPCController.exe watch --interval 10
DLPPCController.exe startup enable
DLPPCController.exe startup disable
```

`watch` persists `DisableDlppc=true` and `WatchdogEnabled=true` in the settings file. Stop it with Ctrl+C.

## Settings

Settings are stored at:

`%LOCALAPPDATA%\DLPPCController\settings.json`

Example:

```json
{
  "DisableDlppc": true,
  "WatchdogEnabled": false
}
```

## Implementation notes

The implementation follows the DLPPC code added in MechControl-RE commit `6fa66cda2218fb3b6fda0b8b415544dd9f1b0596`:

- `PerfCfPolicyGetInfo(V1)` — `0x1CAC3865`
- `PerfCfPolicyGetControl(V1)` — `0x69DE2C54`
- `PerfCfPolicySetControl` — `0x4D1C7D6E`
- Label `DLPPC_1X` — `7`
- Policy index is read from the label-to-index map; it is not hard-coded to 7.
- The SET operation starts with the full GET structure and changes only `bActivate` for the selected policy.
- A second GET verifies the requested state.

The tool does not claim that disabling DLPPC guarantees higher FPS. It only disables this particular driver policy; other power, thermal, current and clock limits remain active.

## Safety

Use at your own risk. Test `status` first. If the policy is unavailable, the utility will not attempt a write. Do not run multiple instances that change the policy simultaneously.
