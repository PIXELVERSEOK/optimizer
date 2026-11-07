using System.Text.Json;
using EvoOptimizer.Core.GameProfiles;

namespace EvoOptimizer.Infrastructure.GameProfiles;

public sealed class GameProfileStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public GameProfileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(
        GameProfileDefinition profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!GameProfileRules.IsValid(profile))
            throw new ArgumentException(
                "El perfil contiene datos inválidos.",
                nameof(profile));

        await _lock.WaitAsync(cancellationToken);

        try
        {
            var destination = GetProfilePath(profile.Id);
            var temporary = destination + ".tmp";

            var json = JsonSerializer.Serialize(profile, JsonOptions);

            await File.WriteAllTextAsync(
                temporary,
                json,
                cancellationToken);

            if (File.Exists(destination))
            {
                var backup = destination + ".backup-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

                File.Copy(destination, backup, overwrite: false);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameProfileDefinition?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var path = GetProfilePath(id);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(
                path,
                cancellationToken);

            var profile =
                JsonSerializer.Deserialize<GameProfileDefinition>(
                    json,
                    JsonOptions);

            return profile is not null &&
                   GameProfileRules.IsValid(profile)
                ? profile
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<GameProfileSummary>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var results = new List<GameProfileSummary>();

            foreach (var path in Directory.EnumerateFiles(
                _directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var json = await File.ReadAllTextAsync(
                        path,
                        cancellationToken);

                    var profile =
                        JsonSerializer.Deserialize<GameProfileDefinition>(
                            json,
                            JsonOptions);

                    if (profile is not null &&
                        GameProfileRules.IsValid(profile))
                    {
                        results.Add(GameProfileRules.ToSummary(profile));
                    }
                }
                catch (JsonException)
                {
                    // Un perfil dañado no impide cargar los demás.
                }
                catch (IOException)
                {
                    // Se omite el archivo temporalmente inaccesible.
                }
            }

            return results
                .OrderBy(x => x.Name)
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var path = GetProfilePath(id);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(path))
                return false;

            var backup = path + ".deleted-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

            File.Move(path, backup);

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddBenchmarkAsync(
        string profileId,
        GameProfileBenchmark benchmark,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No existe el perfil {profileId}.");

        var updated = profile with
        {
            Benchmarks = profile.Benchmarks
                .Append(benchmark)
                .OrderBy(x => x.CapturedAt)
                .ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = GameProfileStatus.NeedsBenchmark
        };

        await SaveAsync(updated, cancellationToken);
    }

    private string GetProfilePath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            id.Length > 80 ||
            id.Any(c => !char.IsLetterOrDigit(c) &&
                        c != '-' && c != '_'))
        {
            throw new ArgumentException(
                "Identificador de perfil inválido.",
                nameof(id));
        }

        return Path.Combine(_directory, id + ".json");
    }
}
