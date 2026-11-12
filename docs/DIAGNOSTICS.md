# Diagnosing gaming and streaming lag with LagLens

## The comparison

LagLens measures the router and two public internet destinations during the same sample rounds. A router reply crosses the local connection. Internet replies cross that connection and the WAN path. Comparing them helps identify where a delay begins. A suspected location is not a component-level diagnosis: a router can queue traffic, a PC can be overloaded, and an endpoint can handle probes differently from game traffic.

The app collects IPv4 probes from the selected network path. Public controls are Cloudflare `1.1.1.1` and Google `8.8.8.8`, using ICMP echo and a TCP connection to port 443. The TCP connection closes after timing the connection; the app does not log in, download a file, or saturate bandwidth. An optional custom endpoint is tested over TCP only, at the port you choose.

Default public addresses are fixed and do not rely on DNS. A custom hostname is resolved before sampling. The app does not test DNS performance, IPv6, the full game protocol, an OBS ingest session, or end-to-end upload capacity. If public controls are healthy while Fortnite/OBS fails, the game/service route, application, encoder load, CPU/GPU load and streaming settings still need investigation.

## Capture a useful session

1. Connect the PC by Ethernet and select that adapter. Keep the normal configuration initially so the first session captures the actual problem.
2. Capture five minutes while idle. Keep downloads and speed tests out of the baseline.
3. Capture fifteen minutes while gaming and streaming at the usual bitrate. Press **Mark lag** as soon as symptoms appear. Make separate markers for a game freeze and an OBS disconnect if they occur separately.
4. Stop and export both sessions. Compare the symptom timestamps with router latency, public TCP latency and adapter events.
5. Repeat after changing just one suspected component or condition. Record what changed outside the app or in a marker.

Manual markers are observations, not automated detection of a Fortnite or OBS error. Someone may press the button after the event, so inspect nearby samples as well. A short healthy session cannot rule out an intermittent issue.

## How conclusions are limited

The app waits for at least 20 rounds. A local latency problem needs supporting simultaneous internet degradation; a router ping spike alone may be router control-plane scheduling. Adapter error increments or link changes provide separate local-path evidence. A reset counter is treated as a reset rather than a huge increase.

Internet-path suspicion needs degradation at both public TCP controls in matching rounds while the gateway remains healthy. One bad public endpoint can reflect destination-specific behavior. ICMP-only public failures do not establish an outage while TCP controls remain healthy.

If the gateway never answers, the app cannot establish a healthy local reference. It reports an inconclusive localization rather than treating silence as a bad Ethernet port. VPNs, virtual adapters, multiple active interfaces, changed IPv4/gateway information, unsupported paths and route mismatches can also make the result inconclusive. Start a new session after deliberately changing the network path. Do not interpret a Wi-Fi session as a test of the Ethernet jack.

Latency values are round-trip/control-connection timing, not one-way latency. Jitter summarizes absolute changes between adjacent successful observations; a gap is not interpolated. Failed-probe rate counts attempted probes that time out or fail, while unavailable or route-mismatched probes are kept separate. P95 describes the slower end of the observed timings; it is not a guarantee of gameplay performance.

Adapter errors and discards are evidence to investigate. Drivers may expose incomplete counters; discards can reflect load or queue pressure rather than a cable fault. Reported link speed is the negotiated adapter link, not the subscription's internet speed. Driver version/date information helps compare systems and vendor releases, but an older date does not prove the driver is faulty.

## Distinguish the cable, port, adapter and driver

When the local path is suspected, repeat the same gaming/streaming session after one change at a time:

- Replace the Ethernet cable with a known-good cable.
- Move to another LAN port on the router/switch, keeping the new cable.
- If possible, try a known-good Ethernet adapter on the same PC and cable/port. A second PC on the same path can provide another comparison.
- Compare the adapter's driver version with the PC/motherboard or adapter manufacturer's supported driver. Use the manufacturer's installer and keep a rollback path. LagLens does not install or alter drivers.

If the public TCP controls degrade together only while streaming and the router stays responsive, compare a session at a lower OBS bitrate and a session with the stream stopped. Improvement can suggest upload congestion/queueing, but it does not identify whether the responsible queue is in the router, modem/ONT or ISP. Use the report's timestamps when discussing the problem with the ISP. An ISP-side diagnosis still requires its telemetry or a controlled comparison.

Avoid changing offload settings, energy-saving settings, drivers and cables all at once: the result would not identify which change mattered.

## Reading and sharing evidence

Exported HTML is intended for reading, CSV for deeper analysis, and JSON/JSONL for preserving the observations. Review the adapter name, local IP/gateway, driver details, custom endpoint and marker text before sending a report to someone else. There is no automatic upload.

Local logs are under `%LOCALAPPDATA%\LagLens\Sessions`. A completed round is written while the session runs, so a forced close can still leave useful partial evidence. Use **Stop** for a clean final assessment. Sessions are capped at four hours to keep a single report manageable; start a new session for another recording period.

## Implementation references

- [Microsoft: IPv4 interface error counters](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.ipv4interfacestatistics.incomingpacketswitherrors?view=net-10.0) describes the counter source used for adapter evidence.
- [Microsoft: TCP/IP communication troubleshooting](https://learn.microsoft.com/troubleshoot/windows-server/networking/troubleshoot-tcp-ip-communication-guidance) explains why ICMP timeout results need context and why a gateway may not answer.
- [Microsoft: single-file .NET deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) documents self-contained, platform-specific packaging.
- [Microsoft: supported Windows versions for .NET](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions) gives the current OS support scope.
