using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using EvoOptimizer.Infrastructure.Analysis;

namespace EvoOptimizer.App.Views;

public partial class NetworkDiagnosisPanel : UserControl
{
    private readonly NetworkDiagnosisService _service = new();
    private CancellationTokenSource? _analysisCancellation;

    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.Register(
            nameof(IsBusy),
            typeof(bool),
            typeof(NetworkDiagnosisPanel),
            new PropertyMetadata(false));

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(
            nameof(StatusText),
            typeof(string),
            typeof(NetworkDiagnosisPanel),
            new PropertyMetadata("Presioná ANALIZAR RED para capturar el estado actual."));

    public static readonly DependencyProperty ProbeSummaryTextProperty =
        DependencyProperty.Register(
            nameof(ProbeSummaryText),
            typeof(string),
            typeof(NetworkDiagnosisPanel),
            new PropertyMetadata("Sin mediciones."));

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        private set
        {
            SetValue(IsBusyProperty, value);
            if (IsInitialized)
                AnalyzeButton.IsEnabled = !value;
        }
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        private set => SetValue(StatusTextProperty, value);
    }

    public string ProbeSummaryText
    {
        get => (string)GetValue(ProbeSummaryTextProperty);
        private set => SetValue(ProbeSummaryTextProperty, value);
    }

    public ObservableCollection<string> AdapterRows { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();

    public NetworkDiagnosisPanel()
    {
        InitializeComponent();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
            return;

        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "Leyendo adaptadores y midiendo gateway, ICMP y DNS...";
        AdapterRows.Clear();
        Warnings.Clear();
        ProbeSummaryText = "Midiendo...";

        try
        {
            NetworkDiagnosisReport report = await _service.CaptureAsync(
                _analysisCancellation.Token);

            AdapterRows.Clear();
            foreach (NetworkAdapterEvidence adapter in report.Adapters)
                AdapterRows.Add(adapter.DisplayText);

            Warnings.Clear();
            foreach (string warning in report.Warnings)
                Warnings.Add(warning);

            NetworkProbeSummary gateway =
                NetworkProbeSummary.FromSamples(report.GatewaySamples);
            NetworkProbeSummary internet =
                NetworkProbeSummary.FromSamples(report.InternetSamples);

            ProbeSummaryText =
                $"Gateway {report.GatewayProbeAddress ?? "N/D"}: {gateway.DisplayText}{Environment.NewLine}" +
                $"1.1.1.1: {internet.DisplayText}{Environment.NewLine}" +
                $"DNS {NetworkDiagnosisService.DnsProbeHost}: " +
                (report.DnsResolutionSucceeded
                    ? $"resuelto en {report.DnsResolutionDurationMs} ms"
                    : $"falló ({report.DnsError ?? "error desconocido"})");
            StatusText =
                $"Medido {report.CapturedAt:yyyy-MM-dd HH:mm:ss}; " +
                $"{report.Adapters.Count} adaptador(es) inventariados.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Análisis cancelado.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error en diagnóstico de red: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
