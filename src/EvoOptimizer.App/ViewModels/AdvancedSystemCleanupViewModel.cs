using System.Collections.ObjectModel;
using System.ComponentModel;
using EvoOptimizer.App.Mvvm;
using EvoOptimizer.Infrastructure.Cleanup;

namespace EvoOptimizer.App.ViewModels;

public sealed record CleanupRetentionOption(int Days, string Label);

public sealed class CleanupCategoryRow : ObservableObject
{
    private bool _isSelected;

    public CleanupCategoryRow(CleanupCategoryScanResult result)
    {
        Result = result;
    }

    public CleanupCategoryScanResult Result { get; }
    public string Name => Result.Name;
    public string Description => Result.Description;
    public string Location => Result.Location;
    public string Status => Result.Status;
    public bool IsCleanable => Result.IsCleanable;
    public bool CanSelect => IsCleanable && IsAvailable;
    public string SafetyLabel => IsCleanable ? "LIMPIABLE" : "SOLO LECTURA";
    public int FileCount => Result.FileCount;
    public string EstimatedBytesText => AdvancedSystemCleanupViewModel.FormatBytes(Result.EstimatedBytes);
    public int AccessErrorCount => Result.AccessErrorCount;
    public bool IsAvailable => Result.IsAvailable;
    public IReadOnlyList<CleanupFileCandidate> Files => Result.Files;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect || !Set(ref _isSelected, value))
                return;

            Raise(nameof(SelectedFileCount));
            Raise(nameof(SelectedEstimatedBytes));
        }
    }

    public int EligibleFileCount { get; private set; }
    public long EligibleBytes { get; private set; }
    public string EligibleBytesText => AdvancedSystemCleanupViewModel.FormatBytes(EligibleBytes);
    public int SelectedFileCount => IsSelected ? EligibleFileCount : 0;
    public long SelectedEstimatedBytes => IsSelected ? EligibleBytes : 0;

    internal void UpdateEligibility(DateTime cutoffUtc)
    {
        EligibleFileCount = Files.Count(file => file.LastWriteTimeUtc <= cutoffUtc);
        EligibleBytes = Files
            .Where(file => file.LastWriteTimeUtc <= cutoffUtc)
            .Sum(file => file.Length);
        Raise(nameof(EligibleFileCount));
        Raise(nameof(EligibleBytes));
        Raise(nameof(EligibleBytesText));
        Raise(nameof(SelectedFileCount));
        Raise(nameof(SelectedEstimatedBytes));
    }
}

public sealed class AdvancedSystemCleanupViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AdvancedSystemCleanupService _service;
    private CancellationTokenSource? _operationCancellation;
    private TaskCompletionSource? _operationCompletion;
    private CleanupRetentionOption _selectedRetention = RetentionOptions[2];
    private string _statusText = "Analiza solo ubicaciones conocidas. Ningún archivo se elimina sin selección y confirmación.";
    private string _operationSummary = "Sin operaciones de limpieza en esta sesión.";
    private bool _isBusy;
    private int _scannedFileCount;
    private long _scannedBytes;
    private long _selectedEstimatedBytes;
    private int _selectedFileCount;
    private bool _isDisposed;

    public AdvancedSystemCleanupViewModel(AdvancedSystemCleanupService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public static IReadOnlyList<CleanupRetentionOption> RetentionOptions { get; } =
    [
        new(1, "Más de 24 horas"),
        new(3, "Más de 3 días"),
        new(7, "Más de 7 días"),
        new(14, "Más de 14 días"),
        new(30, "Más de 30 días"),
        new(90, "Más de 90 días")
    ];

    public ObservableCollection<CleanupCategoryRow> Categories { get; } = new();
    public ObservableCollection<CleanupHistoryEntry> History { get; } = new();

    public CleanupRetentionOption SelectedRetention
    {
        get => _selectedRetention;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Set(ref _selectedRetention, value))
                return;

            UpdateEligibility();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string OperationSummary
    {
        get => _operationSummary;
        private set => Set(ref _operationSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
                return;
            Raise(nameof(IsIdle));
            Raise(nameof(CanClean));
        }
    }

    public bool IsIdle => !IsBusy;

    public bool CanClean =>
        !IsBusy && Categories.Any(category =>
            category.IsSelected && category.EligibleFileCount > 0);

    public int ScannedFileCount
    {
        get => _scannedFileCount;
        private set => Set(ref _scannedFileCount, value);
    }

    public long ScannedBytes
    {
        get => _scannedBytes;
        private set
        {
            if (Set(ref _scannedBytes, value))
                Raise(nameof(ScannedBytesText));
        }
    }
    public string ScannedBytesText => FormatBytes(ScannedBytes);

    public int SelectedFileCount
    {
        get => _selectedFileCount;
        private set => Set(ref _selectedFileCount, value);
    }

    public long SelectedEstimatedBytes
    {
        get => _selectedEstimatedBytes;
        private set
        {
            if (Set(ref _selectedEstimatedBytes, value))
                Raise(nameof(SelectedEstimatedBytesText));
        }
    }
    public string SelectedEstimatedBytesText => FormatBytes(SelectedEstimatedBytes);

    public async Task AnalyzeAsync()
    {
        if (IsBusy)
            return;

        using CancellationTokenSource cancellation = BeginOperation();
        try
        {
            IsBusy = true;
            StatusText = "Analizando ubicaciones autorizadas...";
            var progress = new Progress<string>(message => StatusText = message);
            CleanupScanReport report = await _service.ScanAsync(cancellation.Token, progress);

            Categories.Clear();
            foreach (CleanupCategoryScanResult category in report.Categories)
            {
                var row = new CleanupCategoryRow(category);
                row.PropertyChanged += HandleCategoryChanged;
                Categories.Add(row);
            }

            ScannedFileCount = report.FileCount;
            ScannedBytes = report.EstimatedBytes;
            UpdateEligibility();
            StatusText =
                $"Análisis terminado: {report.FileCount:N0} archivos, " +
                $"{FormatBytes(report.EstimatedBytes)} estimados; " +
                $"{report.AccessErrorCount:N0} errores de acceso. " +
                "La cifra recuperable se recalcula por antigüedad y selección.";

            await RefreshHistoryCoreAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Análisis cancelado. No se modificó ningún archivo.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error durante el análisis: {ex.Message}";
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task CleanSelectedAsync()
    {
        CleanupCategoryRow[] selected = Categories
            .Where(category => category.IsSelected && category.EligibleFileCount > 0)
            .ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("Selecciona al menos una categoría con archivos elegibles.");
        if (IsBusy)
            return;

        using CancellationTokenSource cancellation = BeginOperation();
        try
        {
            IsBusy = true;
            StatusText = "Limpiando archivos seleccionados y registrando cada resultado...";
            var progress = new Progress<string>(message => StatusText = message);
            CleanupRunReport report = await _service.CleanAsync(
                selected.Select(category => category.Result.Id),
                TimeSpan.FromDays(SelectedRetention.Days),
                cancellation.Token,
                progress);

            OperationSummary =
                $"Eliminados: {report.DeletedFiles:N0} · Liberados: {FormatBytes(report.FreedBytes)} · " +
                $"Omitidos: {report.SkippedFiles:N0} · Errores: {report.ErrorCount:N0}";
            StatusText = report.ErrorCount == 0
                ? $"Limpieza finalizada. Registro detallado: {report.HistoryPath}"
                : $"Limpieza parcial con {report.ErrorCount:N0} errores. Registro detallado: {report.HistoryPath}";
            await RefreshHistoryCoreAsync(cancellation.Token);
            await AnalyzeCoreAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Limpieza cancelada. Revisa el historial para ver los archivos ya procesados.";
            await RefreshHistoryCoreAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"Error durante la limpieza: {ex.Message}";
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    public void CancelOperation() =>
        _operationCancellation?.Cancel();

    public async Task LoadHistoryAsync()
    {
        try
        {
            await RefreshHistoryCoreAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"No se pudo leer el historial de limpieza: {ex.Message}";
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _operationCancellation?.Cancel();
        if (_operationCompletion is not null)
            await _operationCompletion.Task;
    }

    private async Task AnalyzeCoreAsync(CancellationToken cancellationToken)
    {
        CleanupScanReport report = await _service.ScanAsync(cancellationToken);
        Categories.Clear();
        foreach (CleanupCategoryScanResult category in report.Categories)
        {
            var row = new CleanupCategoryRow(category);
            row.PropertyChanged += HandleCategoryChanged;
            Categories.Add(row);
        }

        ScannedFileCount = report.FileCount;
        ScannedBytes = report.EstimatedBytes;
        UpdateEligibility();
    }

    private async Task RefreshHistoryCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CleanupHistoryEntry> entries =
            await _service.ReadLatestHistoryAsync(100, cancellationToken);
        History.Clear();
        foreach (CleanupHistoryEntry entry in entries)
            History.Add(entry);
    }

    private void HandleCategoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanupCategoryRow.IsSelected) or
            nameof(CleanupCategoryRow.SelectedEstimatedBytes) or
            nameof(CleanupCategoryRow.EligibleBytes))
        {
            RecalculateSelection();
        }
    }

    private void UpdateEligibility()
    {
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(SelectedRetention.Days);
        foreach (CleanupCategoryRow category in Categories)
            category.UpdateEligibility(cutoff);
        RecalculateSelection();
    }

    private void RecalculateSelection()
    {
        SelectedFileCount = Categories.Sum(category => category.SelectedFileCount);
        SelectedEstimatedBytes = Categories.Sum(category => category.SelectedEstimatedBytes);
        Raise(nameof(CanClean));
    }

    private CancellationTokenSource BeginOperation()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _operationCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _operationCancellation;
    }

    private void EndOperation()
    {
        IsBusy = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _operationCompletion?.TrySetResult();
        _operationCompletion = null;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
