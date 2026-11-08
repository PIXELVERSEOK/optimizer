using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EvoOptimizer.Core.Audit;
using EvoOptimizer.Infrastructure.History;

namespace EvoOptimizer.App.ViewModels;

public sealed class HistoryDashboardViewModel : INotifyPropertyChanged
{
    private readonly HistoryDashboardService _historyService;
    private HistoryRecord? _selectedRecord;
    private string _statusText = "El historial local aún no se ha cargado.";
    private string _summaryText = "Sin datos cargados.";
    private bool _isBusy;

    public HistoryDashboardViewModel(HistoryDashboardService historyService)
    {
        _historyService = historyService
            ?? throw new ArgumentNullException(nameof(historyService));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<HistoryRecord> RecentRecords { get; } = new();

    public ObservableCollection<HistoryCategorySummary> Categories { get; } = new();

    public HistoryRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (ReferenceEquals(_selectedRecord, value))
                return;

            _selectedRecord = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPayload));
        }
    }

    public string SelectedPayload =>
        SelectedRecord?.Payload ?? "Seleccioná un registro para ver su detalle.";

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
                return;

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set
        {
            if (_summaryText == value)
                return;

            _summaryText = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;

            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRefresh));
        }
    }

    public bool CanRefresh => !IsBusy;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "Cargando historial local…";

        try
        {
            HistoryDashboardSnapshot snapshot =
                await _historyService.LoadAsync(cancellationToken);

            RecentRecords.Clear();
            foreach (HistoryRecord record in snapshot.RecentRecords)
                RecentRecords.Add(record);

            Categories.Clear();
            foreach (HistoryCategorySummary category in snapshot.Categories)
                Categories.Add(category);

            SelectedRecord = null;
            SummaryText =
                $"{snapshot.TotalRecords:N0} registros · " +
                $"{snapshot.Last24Hours:N0} en 24 h · " +
                $"{snapshot.Last7Days:N0} en 7 días";
            StatusText = snapshot.TotalRecords == 0
                ? "No hay operaciones registradas en el historial local."
                : $"Historial actualizado: {snapshot.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Carga del historial cancelada.";
        }
        catch (Exception ex)
        {
            StatusText = $"No se pudo cargar el historial local: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> RecordAuditAsync(
        SystemAuditReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            await _historyService.SaveAuditAsync(report, cancellationToken);
            await LoadAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "No se guardó la auditoría porque se canceló la operación.";
            return false;
        }
        catch (Exception ex)
        {
            StatusText = $"No se pudo guardar la auditoría en el historial local: {ex.Message}";
            return false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
