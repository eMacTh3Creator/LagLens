# LagLens

A Windows x64 app that helps locate gaming and streaming lag: between the PC and router, or farther out on the internet. It compares simultaneous router and public internet probes, watches the selected adapter's counters and link state, and saves timestamped evidence.

[Download the latest EXE](https://github.com/eMacTh3Creator/LagLens/releases/latest) · [How the diagnosis works](docs/DIAGNOSTICS.md)

## Run it

1. Download **LagLens-1.0.0-win-x64.exe** from Releases and double-click it. Intel/AMD Windows PCs use x64. The ZIP contains the same app and instructions.
2. Select the Ethernet adapter that the PC uses for internet access. The default adapter is selected initially; check that it says Ethernet rather than Wi-Fi or a VPN.
3. Choose **5 minutes**, start a session, and leave the PC idle. This establishes a comparison without a game or stream running.
4. Run another **15-minute** session while playing Fortnite and streaming with the usual settings. Press **Mark lag** when the game lags or the stream drops.
5. Stop the session and **Export report**. Share the HTML report and CSV with the person helping diagnose the connection.

Windows 11 x64 is recommended. Supported Windows 10 LTSC/Enterprise editions follow [Microsoft's .NET 10 Windows support list](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions). The EXE includes .NET: there is no separate runtime to install. It runs without administrator access and does not change network settings or install/update drivers.

This first release is **unsigned**. Windows can show an unknown-publisher or SmartScreen prompt. Download from this repository's releases and compare the SHA-256 checksum if you want to verify the file:

```powershell
Get-FileHash .\LagLens-1.0.0-win-x64.exe -Algorithm SHA256
```

Compare the displayed hash with `SHA256SUMS.txt` from the same release.

## Read the result

| Result | What the evidence suggests |
| --- | --- |
| Local network suspected | Adapter errors, link changes, or correlated router/internet problems warrant checking the PC-to-router path. The cable, Ethernet port, router/switch port and driver remain candidates. |
| Internet path suspected | Both independent public TCP endpoints degrade together while the router stays responsive. Investigate WAN congestion, router internet-side queues, modem/ONT, ISP routing or upstream service paths. |
| Mixed | Separate local and internet-path evidence occurred in the same session. |
| No issue observed | The measured paths stayed healthy during this session. An intermittent fault, game-specific route or streaming problem can still exist. |
| Inconclusive / collecting data | More observations or a usable comparison are needed. Blocked router pings, VPN routing and unreachable probe endpoints can prevent localization. |

The app needs at least 20 rounds before drawing a conclusion. Run for several minutes and try to capture the actual problem. It cannot certify that an Ethernet port or driver is healthy; a controlled cable/port/adapter swap is the way to distinguish those components. Zero reported adapter errors alone does not clear the hardware or driver.

## What it measures

- Roughly once a second: IPv4 ICMP echo to the selected adapter's gateway, plus ICMP and TCP port 443 connection timing to `1.1.1.1` (Cloudflare) and `8.8.8.8` (Google).
- Adapter link state and speed, received/sent bytes, packet errors and discards, and available driver information.
- Average, median, 95th percentile, maximum, jitter and failed-probe rate, with timestamps and manual lag markers.
- An optional custom IPv4/hostname and TCP port. Use a known reachable service; a Fortnite server may not accept TCP on the selected port.

These small probes do not fill the connection like a speed test. The public endpoints are controls, not the actual Fortnite match server or OBS ingest server. TCP connection timing does not measure Fortnite's UDP gameplay or the full streaming protocol. ICMP can be filtered or deprioritized, so an unanswered ping by itself is not an internet outage.

## Saved evidence and privacy

Session logs stay on the PC in `%LOCALAPPDATA%\LagLens\Sessions`. The app records locally and sends only the network probes described above. It does not upload a report automatically. The tested endpoints can see the connection's public source IP, as with any outbound connection.

Reports include local adapter and IP/gateway information, driver details, timestamps, the optional custom endpoint and any marker text. Review them before sharing. They do not intentionally collect passwords, stream keys, browser history, game account names or packet payloads. Closing/stopping a session preserves completed observations; the live JSONL log provides recovery evidence if a session ends unexpectedly.

## Build and validation

Install the .NET 10 SDK, clone this repository, and run on Windows:

```powershell
git clone https://github.com/eMacTh3Creator/LagLens.git
cd LagLens
.\scripts\build.ps1
```

The script runs synthetic diagnostic regression tests, publishes a self-contained single-file x64 EXE, checks the PE architecture, launches headless app and UI smoke modes, then creates the EXE, portable ZIP and checksum file in `artifacts/release`.

Core tests can also run on macOS/Linux:

```sh
dotnet run --project tests/LagLens.Tests -c Release
```

GitHub Actions builds and runs the x64 app on Windows for every main-branch push and pull request. A `v*` tag publishes release assets only after the build and smoke checks pass. CI validates software behavior; testing a virtual adapter or hosted runner cannot qualify every physical Ethernet chipset or reproduce your friend's connection.

The project is MIT licensed. See [LICENSE](LICENSE).

The EXE embeds its runtime license notices. To extract them without starting a test:

```powershell
.\LagLens-1.0.0-win-x64.exe --licenses --output .\LagLens-licenses
```

The ZIP includes the same notices in its `licenses` folder, and release downloads include a combined `THIRD-PARTY-NOTICES.txt`. The build script verifies repository notices against the exact restored runtime packages before packaging; SDK updates that change those notices require updating `docs/licenses` and rebuilding.
