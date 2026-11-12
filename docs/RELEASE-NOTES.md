LagLens helps locate gaming and streaming lag by comparing the local router with two independent public internet endpoints while recording Ethernet link state, traffic counters and driver information.

Download **LagLens-1.0.0-win-x64.exe** for an Intel/AMD Windows PC and double-click it. The portable ZIP includes the same EXE and instructions. No separate .NET installation or administrator access is needed.

Run five minutes while idle, then fifteen minutes during Fortnite and streaming. Press **Mark lag** when lag or a stream drop occurs, then export the report. Read the evidence and suggested controlled checks before deciding whether the problem is local or upstream.

This first release is unsigned. Windows may show an unknown-publisher or SmartScreen prompt. Verify the download against SHA256SUMS.txt if needed. Only run files from this repository's releases.

Runtime license notices are embedded in the EXE (`--licenses --output <folder>`), included in the ZIP, and available as a separate release download.

The result is a diagnosis of the observed paths, not proof that a specific Ethernet port, cable, driver, ISP, Fortnite server or streaming service is faulty. Public TCP/ICMP probes do not measure Fortnite UDP gameplay or OBS media delivery directly. Physical x64 hardware and your friend's network still need testing.

Source and full instructions: https://github.com/eMacTh3Creator/LagLens
