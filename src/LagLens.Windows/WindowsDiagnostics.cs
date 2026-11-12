using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LagLens.Core;

namespace LagLens.Windows;

public sealed class WindowsDiagnostics : IPlatformDiagnostics
{
    private const int TimeoutMs = 1000;

    public Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<AdapterInfo>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var best = BestInterface(IPAddress.Parse("1.1.1.1"));
        var adapters = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var ip = nic.GetIPProperties();
                var index = ip.GetIPv4Properties()?.Index ?? 0;
                var address = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                var gateway = ip.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))?.Address.ToString();
                var kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" :
                    nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT ? "Ethernet" : "Virtual / VPN / other";
                if (new[] { "virtual", "vpn", "tap-", "tunnel", "hyper-v", "parallels", "vmware" }.Any(s => nic.Description.Contains(s, StringComparison.OrdinalIgnoreCase))) kind = "Virtual / VPN / other";
                adapters.Add(new AdapterInfo(nic.Id, nic.Name, nic.Description, kind,
                    nic.OperationalStatus == OperationalStatus.Up, nic.Speed, index, address, gateway, index == best));
            }
            catch (NetworkInformationException) { /* Adapter may be removed while enumerating. */ }
        }
        return adapters.OrderByDescending(a => a.IsDefaultRoute).ThenByDescending(a => a.IsUp).ThenBy(a => a.Name).ToArray();
    }, cancellationToken);

    public async Task<AdapterDetails> GetAdapterDetailsAsync(AdapterInfo adapter, CancellationToken cancellationToken)
    {
        // Only a numeric interface index enters this fixed command. No user text is interpreted as PowerShell.
        var script = "$ErrorActionPreference='Stop'; $a=Get-NetAdapter -IncludeHidden | Where-Object InterfaceIndex -eq " +
            adapter.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "; if(!$a){throw 'Adapter driver metadata unavailable'}; $a | Select-Object DriverVersion,DriverDate,DriverProvider,DriverFileName,DriverInformation | ConvertTo-Json -Compress";
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) return Unknown("Windows driver query failed. " + error.Trim());
            using var doc = JsonDocument.Parse(output);
            string Value(string key) => doc.RootElement.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString()! : "Unavailable";
            return new AdapterDetails(Value("DriverVersion"), Value("DriverDate"), Value("DriverProvider"), Value("DriverFileName"), Value("DriverInformation"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unknown("Windows driver query timed out; latency testing can continue."); }
        catch (Exception ex) when (ex is Win32Exception or IOException or JsonException or InvalidOperationException) { return Unknown("Driver metadata unavailable: " + ex.Message); }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    private static AdapterDetails Unknown(string reason) => new("Unavailable", "Unavailable", "Unavailable", "Unavailable", reason);

    public Task<AdapterSnapshot> GetSnapshotAsync(AdapterInfo adapter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == adapter.Id);
            if (nic is null) return Task.FromResult(Missing("Selected adapter was removed."));
            var stats = nic.GetIPv4Statistics();
            var ip = nic.GetIPProperties();
            return Task.FromResult(new AdapterSnapshot(DateTimeOffset.UtcNow, nic.OperationalStatus == OperationalStatus.Up, nic.Speed,
                stats.BytesReceived, stats.BytesSent, stats.IncomingPacketsWithErrors, stats.OutgoingPacketsWithErrors,
                stats.IncomingPacketsDiscarded, stats.OutgoingPacketsDiscarded,
                ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString(),
                ip.GatewayAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.Equals(IPAddress.Any))?.Address.ToString()));
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException) { return Task.FromResult(Missing(ex.Message)); }
    }

    private static AdapterSnapshot Missing(string error) => new(DateTimeOffset.UtcNow, false, 0, 0, 0, 0, 0, 0, 0, null, null, error);

    public async Task<ProbeResult> ProbeAsync(ProbeTarget target, AdapterInfo adapter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IPAddress.TryParse(target.Address, out var destination) || destination.AddressFamily != AddressFamily.InterNetwork)
            return new(target.Id, ProbeOutcome.Unavailable, null, "No usable IPv4 target address.");
        if (!IPAddress.TryParse(adapter.Ipv4Address, out var source)) return new(target.Id, ProbeOutcome.Unavailable, null, "Selected adapter has no IPv4 address.");
        var route = BestInterface(destination);
        if (route == 0) return new(target.Id, ProbeOutcome.Unavailable, null, "Windows has no route to this destination.");
        if (route != adapter.InterfaceIndex)
            return new(target.Id, ProbeOutcome.RouteMismatch, null, $"Windows routes this target through interface {route}, not selected interface {adapter.InterfaceIndex}. VPN or another adapter may be active.");
        if (target.Kind is ProbeKind.GatewayIcmp or ProbeKind.InternetIcmp)
            return await Task.Run(() => IcmpProbe(target, source, destination, cancellationToken), cancellationToken);
        var watch = Stopwatch.StartNew();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutMs);
        try
        {
            socket.Bind(new IPEndPoint(source, 0));
            await socket.ConnectAsync(new IPEndPoint(destination, target.Port), timeout.Token);
            return new(target.Id, ProbeOutcome.Success, watch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(target.Id, ProbeOutcome.Timeout, null, "TCP handshake exceeded 1000 ms."); }
        catch (SocketException ex) { return new(target.Id, ProbeOutcome.Error, null, $"TCP {ex.SocketErrorCode}: {ex.Message}"); }
    }

    internal static uint BestInterface(IPAddress address) => GetBestInterface(BitConverter.ToUInt32(address.GetAddressBytes()), out var index) == 0 ? index : 0;

    private static ProbeResult IcmpProbe(ProbeTarget target, IPAddress source, IPAddress destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = IcmpCreateFile();
        if (handle == new IntPtr(-1)) return new(target.Id, ProbeOutcome.Unavailable, null, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        var data = Encoding.ASCII.GetBytes("LagLens1");
        // x64 ICMP uses the *_32 option/reply ABI. Include ICMP error and IO_STATUS_BLOCK headroom.
        var size = Marshal.SizeOf<EchoReply>() + data.Length + 8 + 2 * IntPtr.Size;
        var reply = Marshal.AllocHGlobal(size);
        try
        {
            var options = new IpOptions { Ttl = 128 };
            var count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                BitConverter.ToUInt32(source.GetAddressBytes()), BitConverter.ToUInt32(destination.GetAddressBytes()),
                data, (ushort)data.Length, ref options, reply, (uint)size, TimeoutMs);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0)
            {
                var code = Marshal.GetLastWin32Error();
                var description = Enum.IsDefined(typeof(IPStatus), code) ? ((IPStatus)code).ToString() : new Win32Exception(code).Message;
                return new(target.Id, code == 11010 ? ProbeOutcome.Timeout : ProbeOutcome.Error, null, $"ICMP status {code}: {description}");
            }
            var value = Marshal.PtrToStructure<EchoReply>(reply);
            return value.Status == 0 ? new(target.Id, ProbeOutcome.Success, value.RoundTripTime) :
                new(target.Id, value.Status == 11010 ? ProbeOutcome.Timeout : ProbeOutcome.Error, null, $"ICMP status {value.Status}.");
        }
        finally { Marshal.FreeHGlobal(reply); IcmpCloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IpOptions { public byte Ttl, Tos, Flags, OptionsSize; public uint OptionsData; }
    [StructLayout(LayoutKind.Sequential)]
    private struct EchoReply { public uint Address, Status, RoundTripTime; public ushort DataSize, Reserved; public uint Data; public IpOptions Options; }
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern IntPtr IcmpCreateFile();
    [DllImport("iphlpapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IcmpCloseHandle(IntPtr handle);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint IcmpSendEcho2Ex(IntPtr handle, IntPtr @event, IntPtr apcRoutine, IntPtr apcContext, uint sourceAddress, uint destinationAddress, byte[] requestData, ushort requestSize, ref IpOptions options, IntPtr replyBuffer, uint replySize, uint timeout);
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);
}
