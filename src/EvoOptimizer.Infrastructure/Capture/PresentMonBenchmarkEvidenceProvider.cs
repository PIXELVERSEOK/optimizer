using System.Globalization;
using EvoOptimizer.Core.Benchmark;

namespace EvoOptimizer.Infrastructure.Capture;

/// <summary>
/// Convierte un CSV de PresentMon en evidencia normalizada para el BenchmarkEvidenceGate.
/// No decide si el benchmark es aceptable: esa responsabilidad sigue siendo del gate.
/// </summary>
public sealed class PresentMonBenchmarkEvidenceProvider : ICs2BenchmarkEvidenceProvider
{
    private readonly string _gameId;
    private readonly string _scenario;
    private readonly string _resolution;
    private readonly string _captureSource;

    public PresentMonBenchmarkEvidenceProvider(
        string gameId,
        string scenario,
        string resolution,
        string captureSource = "PresentMon")
    {
        _gameId = RequireValue(gameId, nameof(gameId));
        _scenario = RequireValue(scenario, nameof(scenario));
        _resolution = RequireValue(resolution, nameof(resolution));
        _captureSource = RequireValue(captureSource, nameof(captureSource));
    }

    public async Task<BenchmarkEvidence> ReadEvidenceAsync(
        string csvPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(csvPath))
            throw new ArgumentException("La ruta CSV está vacía.", nameof(csvPath));

        if (!File.Exists(csvPath))
            throw new FileNotFoundException("No se encontró el CSV de PresentMon.", csvPath);

        await using var stream = new FileStream(
            csvPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 65536,
            useAsync: true);

        using var reader = new StreamReader(stream);

        string? headerLine = await reader.ReadLineAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(headerLine))
            throw new InvalidDataException("El CSV no contiene encabezados.");

        char separator = DetectSeparator(headerLine);
        string[] headers = ParseCsvLine(headerLine, separator);

        int frameTimeIndex = FindFrameTimeColumn(headers);
        if (frameTimeIndex < 0)
            throw new InvalidDataException(
                "PresentMon no proporcionó una columna de frametime reconocible.");

        int timestampIndex = FindColumn(headers,
            "TimeInSeconds",
            "TimeInSec",
            "ElapsedTime",
            "Time");

        var frames = new List<BenchmarkFrameObservation>(capacity: 12000);
        double accumulatedSeconds = 0;
        long lineNumber = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] fields = ParseCsvLine(line, separator);

            if (frameTimeIndex >= fields.Length)
                continue;

            if (!TryParseNumber(fields[frameTimeIndex], out double frametime))
                continue;

            frametime = NormalizeFrameTime(frametime, headers[frameTimeIndex]);

            if (!double.IsFinite(frametime) || frametime <= 0)
                continue;

            double timestamp;

            if (timestampIndex >= 0 && timestampIndex < fields.Length &&
                TryParseNumber(fields[timestampIndex], out double explicitTimestamp))
            {
                timestamp = NormalizeTimestamp(explicitTimestamp, headers[timestampIndex]);
            }
            else
            {
                timestamp = accumulatedSeconds;
            }

            if (!double.IsFinite(timestamp) || timestamp < 0)
                continue;

            frames.Add(new BenchmarkFrameObservation(timestamp, frametime));
            accumulatedSeconds = timestamp + frametime / 1000.0;
        }

        if (frames.Count == 0)
            throw new InvalidDataException(
                $"El CSV no contiene frametimes válidos. Líneas examinadas: {lineNumber}.");

        return new BenchmarkEvidence(
            _gameId,
            _scenario,
            _resolution,
            _captureSource,
            DateTimeOffset.UtcNow,
            frames);
    }

    private static int FindFrameTimeColumn(string[] headers)
    {
        string[] preferred =
        {
            "MsBetweenDisplayChange",
            "MsBetweenPresentsDisplayed",
            "MsBetweenPresents",
            "FrameTime",
            "Frametime",
            "FrameTimeMs",
            "FrameTimeUs",
            "FrameTimeNs"
        };

        foreach (string candidate in preferred)
        {
            int index = FindColumn(headers, candidate);
            if (index >= 0)
                return index;
        }

        return -1;
    }

    private static int FindColumn(string[] headers, params string[] candidates)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            string normalized = NormalizeHeader(headers[i]);

            foreach (string candidate in candidates)
            {
                if (normalized.Equals(
                    NormalizeHeader(candidate),
                    StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return -1;
    }

    private static double NormalizeFrameTime(double value, string header)
    {
        string name = NormalizeHeader(header).ToLowerInvariant();

        if (name.Contains("nanosecond") || name.EndsWith("ns"))
            return value / 1_000_000.0;

        if (name.Contains("microsecond") || name.EndsWith("us"))
            return value / 1_000.0;

        // Las columnas MsBetween* y FrameTime habituales de PresentMon están en ms.
        return value;
    }

    private static double NormalizeTimestamp(double value, string header)
    {
        string name = NormalizeHeader(header).ToLowerInvariant();

        if (name.Contains("millisecond") || name.EndsWith("ms"))
            return value / 1000.0;

        if (name.Contains("microsecond") || name.EndsWith("us"))
            return value / 1_000_000.0;

        if (name.Contains("nanosecond") || name.EndsWith("ns"))
            return value / 1_000_000_000.0;

        return value;
    }

    private static bool TryParseNumber(string value, out double result)
    {
        result = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        string cleaned = value.Trim();

        return double.TryParse(
            cleaned,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static string NormalizeHeader(string value)
    {
        return value.Trim().Trim('"').Replace(" ", "").Replace("_", "");
    }

    private static char DetectSeparator(string line)
    {
        char[] candidates = { ',', ';', '\t' };
        char selected = ',';
        int highestCount = -1;

        foreach (char candidate in candidates)
        {
            int count = 0;
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i++;
                        continue;
                    }

                    quoted = !quoted;
                }
                else if (!quoted && line[i] == candidate)
                {
                    count++;
                }
            }

            if (count > highestCount)
            {
                highestCount = count;
                selected = candidate;
            }
        }

        return selected;
    }

    private static string[] ParseCsvLine(string line, char separator)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];

            if (character == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == separator && !quoted)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        fields.Add(current.ToString().Trim());
        return fields.ToArray();
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("El valor no puede estar vacío.", parameterName);

        return value.Trim();
    }
}