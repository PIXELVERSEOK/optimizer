using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EvoOptimizer.Infrastructure.Analysis;

public sealed record NetworkAdapterEvidence(
    string Name,
    string Description,
    string Type,
    string Status,
    long SpeedBitsPerSecond,
    string? IPv4Address,
    string? GatewayIPv4,
    IReadOnlyList<string> DnsServers)
{
    public string DisplayText =>
        $"{Name} · {Status} · {Type} · " +
        $"{(SpeedBitsPerSecond > 0 ? $"{SpeedBitsPerSecond / 1_000_000} Mbps" : "velocidad N/D")} · " +
        $"IPv4 {IPv4Address ?? "N/D"} · Gateway {GatewayIPv4 ?? "N/D"} · " +
        $"DNS {(DnsServers.Count == 0 ? "N/D" : string.Join(", ", DnsServers))}";
}

public sealed record NetworkPingSample(
    string Destination,
    string Status,
    long? RoundtripTimeMs,
    string? Error);

public sealed record NetworkProbeSummary(
    int Sent,
    int Received,
    int Lost,
    double? PacketLossPercent,
    double? MinimumLatencyMs,
    double? AverageLatencyMs,
    double? MaximumLatencyMs)
{
    public static NetworkProbeSummary FromSamples(
        IReadOnlyCollection<NetworkPingSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        long[] replies = samples
            .Where(sample =>
                string.Equals(sample.Status, IPStatus.Success.ToString(),
                    StringComparison.OrdinalIgnoreCase) &&
                sample.RoundtripTimeMs is >= 0)
            .Select(sample => sample.RoundtripTimeMs!.Value)
            .ToArray();

        int sent = samples.Count;
        int received = replies.Length;

        return new NetworkProbeSummary(
            sent,
            received,
            sent - received,
            sent == 0 ? null : (sent - received) * 100d / sent,
            replies.Length == 0 ? null : replies.Min(),
            replies.Length == 0 ? null : replies.Average(),
            replies.Length == 0 ? null : replies.Max());
    }

    public string DisplayText => Sent == 0
        ? "Sin pruebas ICMP."
        : $"Recibidos {Received}/{Sent} · pérdida " +
          $"{(PacketLossPercent.HasValue ? $"{PacketLossPercent:F1}%" : "N/D")} · " +
          $"RTT mín/prom/máx " +
          $"{(MinimumLatencyMs.HasValue ? $"{MinimumLatencyMs:F0}/{AverageLatencyMs:F1}/{MaximumLatencyMs:F0} ms" : "N/D")}";
}

public sealed record NetworkDiagnosisReport(
    DateTimeOffset CapturedAt,
    IReadOnlyList<NetworkAdapterEvidence> Adapters,
    string? GatewayProbeAddress,
    IReadOnlyList<NetworkPingSample> GatewaySamples,
    IReadOnlyList<NetworkPingSample> InternetSamples,
    bool DnsResolutionSucceeded,
    long? DnsResolutionDurationMs,
    string? DnsError,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Diagnóstico de solo lectura de adaptadores, conectividad ICMP y resolución DNS.
/// No cambia opciones de adaptador, DNS, rutas ni configuración TCP/IP.
/// </summary>
public sealed class NetworkDiagnosisService
{
    public const string InternetProbeAddress = "1.1.1.1";
    public const string DnsProbeHost = "example.com";
    private const int ProbeCount = 3;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    public async Task<NetworkDiagnosisReport> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<NetworkAdapterEvidence> adapters = ReadAdapters();
        var warnings = new List<string>();
        NetworkAdapterEvidence? activeAdapter = adapters.FirstOrDefault(adapter =>
            string.Equals(adapter.Status, OperationalStatus.Up.ToString(),
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(adapter.GatewayIPv4));
        int activeGatewayCount = adapters.Count(adapter =>
            string.Equals(adapter.Status, OperationalStatus.Up.ToString(),
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(adapter.GatewayIPv4));

        if (activeAdapter is null)
            warnings.Add("No se encontró un adaptador activo con gateway IPv4; no se ejecutó la prueba al gateway.");
        else if (activeGatewayCount > 1)
            warnings.Add(
                $"Hay {activeGatewayCount} adaptadores activos con gateway; la prueba de gateway corresponde a '{activeAdapter.Name}'.");

        IReadOnlyList<NetworkPingSample> gatewaySamples =
            activeAdapter?.GatewayIPv4 is { } gateway
                ? await ProbeAsync(gateway, cancellationToken)
                : Array.Empty<NetworkPingSample>();

        IReadOnlyList<NetworkPingSample> internetSamples =
            await ProbeAsync(InternetProbeAddress, cancellationToken);

        bool dnsSucceeded = false;
        long? dnsDurationMs = null;
        string? dnsError = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            IPAddress[] resolved = await Dns.GetHostAddressesAsync(
                DnsProbeHost,
                cancellationToken);
            dnsSucceeded = resolved.Length > 0;
            if (!dnsSucceeded)
                dnsError = "La consulta no devolvió direcciones IP.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SocketException ex)
        {
            dnsError = $"{ex.SocketErrorCode}: {ex.Message}";
        }
        finally
        {
            stopwatch.Stop();
            dnsDurationMs = stopwatch.ElapsedMilliseconds;
        }

        if (!dnsSucceeded)
            warnings.Add($"Falló la resolución DNS de {DnsProbeHost}: {dnsError}");

        if (NetworkProbeSummary.FromSamples(internetSamples).Received == 0)
            warnings.Add("No hubo respuestas ICMP de 1.1.1.1; ICMP puede estar filtrado y esto no prueba por sí solo una caída de Internet.");

        return new NetworkDiagnosisReport(
            DateTimeOffset.Now,
            adapters,
            activeAdapter?.GatewayIPv4,
            gatewaySamples,
            internetSamples,
            dnsSucceeded,
            dnsDurationMs,
            dnsError,
            warnings);
    }

    private static IReadOnlyList<NetworkAdapterEvidence> ReadAdapters()
    {
        var results = new List<NetworkAdapterEvidence>();

        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                string? address = properties.UnicastAddresses
                    .FirstOrDefault(item =>
                        item.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(item.Address))
                    ?.Address.ToString();
                string? gateway = properties.GatewayAddresses
                    .Select(item => item.Address)
                    .FirstOrDefault(item =>
                        item.AddressFamily == AddressFamily.InterNetwork &&
                        !item.Equals(IPAddress.Any))
                    ?.ToString();

                results.Add(new NetworkAdapterEvidence(
                    adapter.Name,
                    adapter.Description,
                    adapter.NetworkInterfaceType.ToString(),
                    adapter.OperationalStatus.ToString(),
                    adapter.Speed,
                    address,
                    gateway,
                    properties.DnsAddresses
                        .Select(item => item.ToString())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()));
            }
            catch (NetworkInformationException)
            {
                results.Add(new NetworkAdapterEvidence(
                    adapter.Name,
                    adapter.Description,
                    adapter.NetworkInterfaceType.ToString(),
                    adapter.OperationalStatus.ToString(),
                    adapter.Speed,
                    null,
                    null,
                    Array.Empty<string>()));
            }
        }

        return results;
    }

    private static async Task<IReadOnlyList<NetworkPingSample>> ProbeAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        var samples = new List<NetworkPingSample>(ProbeCount);
        IPAddress address = IPAddress.Parse(destination);

        using var ping = new Ping();

        for (int index = 0; index < ProbeCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                PingReply reply = await ping.SendPingAsync(
                    address,
                    (int)ProbeTimeout.TotalMilliseconds)
                    .WaitAsync(cancellationToken);
                samples.Add(new NetworkPingSample(
                    destination,
                    reply.Status.ToString(),
                    reply.Status == IPStatus.Success ? reply.RoundtripTime : null,
                    null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PingException ex)
            {
                samples.Add(new NetworkPingSample(
                    destination,
                    "Error",
                    null,
                    ex.InnerException?.Message ?? ex.Message));
            }
        }

        return samples;
    }
}
