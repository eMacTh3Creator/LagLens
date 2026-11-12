# Release validation — 1.0.0

Checked on 2026-11-12. The release workflow rebuilds the tagged source and requires the checks below before uploading the EXE, ZIP, licenses and SHA-256 checksums.

- **41/41 core regression checks passed.** Scenarios include healthy traces, local error counters, link loss, counter resets, missing telemetry, blocked ICMP, independent endpoint failures, simultaneous internet-path incidents, changed routes, sparse observations, scheduling stalls, mixed evidence and cancellation.
- **Windows x64 executable checks passed.** Native source-bound ICMP and TCP loopback, wrong-adapter route detection, real Windows adapter counters, driver inventory, recording, JSON/CSV/HTML exports, HTML escaping, unavailable telemetry reporting and embedded license extraction were exercised.
- **GUI lifecycle passed.** Windows Forms discovered adapters, started a real session, recorded two rounds and a manual lag marker, stopped, saved the report automatically and exported it. Synthetic screenshots were rendered at 1140×850 and 1000×760; scroll areas keep longer evidence and instructions reachable.
- **A 25-second live Windows VM test passed.** All five targets returned observations and the session finalized with its report, raw samples and marker. This validates plumbing on a virtual network, not a physical Ethernet chipset.
- **Hosted Windows x64 build and smoke checks passed** in the [initial CI run](https://github.com/eMacTh3Creator/LagLens/actions/runs/43274309064). The release tag runs the same checks again, including the expanded GUI lifecycle. See the [release workflow runs](https://github.com/eMacTh3Creator/LagLens/actions/workflows/release.yml) for the exact tag and commit.

The Windows VM used an ARM64 host running the x64 executable under Windows emulation; GitHub's Windows x64 runner provides the native x64 gate. Physical Intel/AMD Ethernet hardware, the friend's Fortnite/streaming workload and long-duration fault reproduction remain untested. The executable is unsigned. These checks establish software operation and conservative diagnosis behavior, not that a particular cable, port, driver or ISP is fault-free.

Session data from local validation is excluded from source control and release packages. The shipped ZIP contains only the application, instructions and licenses.
