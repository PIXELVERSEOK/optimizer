using System.Globalization;

namespace EvoOptimizer.Infrastructure.Analysis;

public sealed record PresentMonFrame(
    double TimeSeconds,
    double FrametimeMs,
    bool Dropped,
    string PresentMode);

public sealed record PresentMonReadResult(
    IReadOnlyList<PresentMonFrame> Frames,
    int InvalidRows,
    string FrametimeColumn,
    IReadOnlyList<string> Warnings);

public sealed class PresentMonCsvReader
{
    private static readonly string[] PreferredColumns =
    {
        "MsBetweenDisplayChange",
        "MsBetweenPresents",
        "FrameTime",
        "Frametime",
        "MsBetweenPresentsDisplayed"
    };

    public PresentMonReadResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("No se encontró el CSV.", path);

        using var reader = new StreamReader(path);

        var headerLine = reader.ReadLine();

        if (string.IsNullOrWhiteSpace(headerLine))
            throw new InvalidDataException("El CSV está vacío.");

        var headers = ParseCsvLine(headerLine)
            .Select((name, index) => new
            {
                Name = Normalize(name),
                Index = index
            })
            .ToArray();

        var frameColumn = PreferredColumns
            .Select(column => headers.FirstOrDefault(
                h => h.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(x => x is not null);

        if (frameColumn is null)
            throw new InvalidDataException(
                "No se encontró una columna de frametime compatible.");

        var timeColumn = headers.FirstOrDefault(
            h => h.Name.Equals("TimeInSeconds", StringComparison.OrdinalIgnoreCase));

        var droppedColumn = headers.FirstOrDefault(
            h => h.Name.Equals("Dropped", StringComparison.OrdinalIgnoreCase));

        var modeColumn = headers.FirstOrDefault(
            h => h.Name.Equals("PresentMode", StringComparison.OrdinalIgnoreCase));

        var frames = new List<PresentMonFrame>();
        var warnings = new List<string>();
        var invalidRows = 0;
        var rowNumber = 1;

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            rowNumber++;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var fields = ParseCsvLine(line);

                if (frameColumn.Index >= fields.Count)
                {
                    invalidRows++;
                    continue;
                }

                if (!double.TryParse(
                    fields[frameColumn.Index],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var frameTime) ||
                    !double.IsFinite(frameTime) ||
                    frameTime <= 0 ||
                    frameTime > 10000)
                {
                    invalidRows++;
                    continue;
                }

                var timestamp = 0d;

                if (timeColumn is not null &&
                    timeColumn.Index < fields.Count)
                {
                    double.TryParse(
                        fields[timeColumn.Index],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out timestamp);
                }

                var dropped = false;

                if (droppedColumn is not null &&
                    droppedColumn.Index < fields.Count)
                {
                    dropped = fields[droppedColumn.Index].Equals(
                                  "true", StringComparison.OrdinalIgnoreCase) ||
                              fields[droppedColumn.Index] == "1";
                }

                var mode = modeColumn is not null &&
                           modeColumn.Index < fields.Count
                    ? fields[modeColumn.Index]
                    : string.Empty;

                frames.Add(new PresentMonFrame(
                    timestamp,
                    frameTime,
                    dropped,
                    mode));
            }
            catch (FormatException)
            {
                invalidRows++;
            }
        }

        if (frames.Count == 0)
            throw new InvalidDataException(
                "No se encontraron muestras válidas de frametime.");

        if (invalidRows > 0)
            warnings.Add($"{invalidRows} filas se descartaron por datos inválidos.");

        return new PresentMonReadResult(
            frames,
            invalidRows,
            frameColumn.Name,
            warnings);
    }

    private static string Normalize(string value) =>
        value.Trim().Trim('\uFEFF');

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var insideQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];

            if (character == '"')
            {
                if (insideQuotes &&
                    i + 1 < line.Length &&
                    line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    insideQuotes = !insideQuotes;
                }
            }
            else if (character == ',' && !insideQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (insideQuotes)
            throw new FormatException("Campo CSV con comillas sin cerrar.");

        fields.Add(current.ToString().Trim());
        return fields;
    }
}
