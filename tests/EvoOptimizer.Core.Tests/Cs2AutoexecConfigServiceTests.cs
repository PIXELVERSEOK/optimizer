using EvoOptimizer.Infrastructure.GameProfiles;

namespace EvoOptimizer.Core.Tests;

public sealed class Cs2AutoexecConfigServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "EvoOptimizerTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _stateDirectory;
    private readonly string _configDirectory;
    private readonly string _configPath;
    private readonly Cs2AutoexecConfigService _service;

    public Cs2AutoexecConfigServiceTests()
    {
        _stateDirectory = Path.Combine(_root, "state");
        _configDirectory = Path.Combine(_root, "game", "csgo", "cfg");
        Directory.CreateDirectory(_configDirectory);
        _configPath = Path.Combine(_configDirectory, "autoexec.cfg");
        _service = new Cs2AutoexecConfigService(_stateDirectory);
    }

    [Fact]
    public async Task LoadReportsMissingAutoexecWithoutInventingSettings()
    {
        var snapshot = await _service.LoadAsync(_configPath);

        Assert.False(snapshot.FileExists);
        Assert.Empty(snapshot.Settings);
        Assert.False(snapshot.CanRestore);
        Assert.Null(snapshot.BackupPath);
    }

    [Fact]
    public async Task ApplyingEmptyDraftDoesNotCreateAnEmptyAutoexec()
    {
        var snapshot = await _service.ApplyAsync(
            _configPath,
            Array.Empty<Cs2AutoexecSetting>());

        Assert.False(snapshot.FileExists);
        Assert.False(snapshot.CanRestore);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public async Task ApplyPreservesUnmanagedTextAndRestoreReturnsExactOriginal()
    {
        const string original = "// User settings\r\nsensitivity 2.25\r\n";
        await File.WriteAllTextAsync(_configPath, original);

        var applied = await _service.ApplyAsync(
            _configPath,
            new[]
            {
                new Cs2AutoexecSetting("fps_max", "0"),
                new Cs2AutoexecSetting("cl_showfps", "1")
            });

        Assert.True(applied.CanRestore);
        Assert.Equal(2, applied.Settings.Count);
        Assert.Contains("sensitivity 2.25", await File.ReadAllTextAsync(_configPath));
        Assert.NotNull(applied.BackupPath);
        Assert.Equal(original, await File.ReadAllTextAsync(applied.BackupPath!));

        var restored = await _service.RestoreAsync(_configPath);

        Assert.True(restored.FileExists);
        Assert.False(restored.CanRestore);
        Assert.Equal(original, await File.ReadAllTextAsync(_configPath));
    }

    [Fact]
    public async Task ApplyCreatesMissingFileAndRestoreRemovesOnlyUnchangedFile()
    {
        var applied = await _service.ApplyAsync(
            _configPath,
            new[] { new Cs2AutoexecSetting("fps_max_ui", "120") });

        Assert.True(applied.FileExists);
        Assert.True(applied.CanRestore);
        Assert.Null(applied.BackupPath);
        Assert.Contains("fps_max_ui 120", await File.ReadAllTextAsync(_configPath));

        var restored = await _service.RestoreAsync(_configPath);

        Assert.False(restored.FileExists);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public async Task RestoreRefusesToOverwriteExternalEdits()
    {
        await File.WriteAllTextAsync(_configPath, "// Original\r\n");
        await _service.ApplyAsync(
            _configPath,
            new[] { new Cs2AutoexecSetting("cl_showfps", "1") });
        await File.AppendAllTextAsync(_configPath, "// external change\r\n");

        var snapshot = await _service.LoadAsync(_configPath);

        Assert.False(snapshot.CanRestore);
        await Assert.ThrowsAsync<IOException>(
            () => _service.RestoreAsync(_configPath));
        Assert.Contains("external change", await File.ReadAllTextAsync(_configPath));
    }

    [Fact]
    public async Task LoadAndApplyReplaceOnlyTheEvoManagedSection()
    {
        await File.WriteAllTextAsync(
            _configPath,
            "// before\n" +
            "// EVO FPS OPTIMIZER BEGIN\n" +
            "fps_max 180\n" +
            "// EVO FPS OPTIMIZER END\n" +
            "// after\n");

        var loaded = await _service.LoadAsync(_configPath);
        Assert.Equal(new Cs2AutoexecSetting("fps_max", "180"), Assert.Single(loaded.Settings));

        var applied = await _service.ApplyAsync(
            _configPath,
            new[] { new Cs2AutoexecSetting("fps_max", "0") });
        string text = await File.ReadAllTextAsync(_configPath);

        Assert.Contains("// before\n", text);
        Assert.Contains("// after\n", text);
        Assert.Contains("fps_max 0", text);
        Assert.DoesNotContain("fps_max 180", text);
        Assert.True(applied.CanRestore);
    }

    [Theory]
    [InlineData("exec", "user.cfg")]
    [InlineData("fps_max", "0; quit")]
    [InlineData("unknown_cvar", "1")]
    public async Task ApplyRejectsCommandsAndUnsupportedVariables(
        string name,
        string value)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ApplyAsync(
                _configPath,
                new[] { new Cs2AutoexecSetting(name, value) }));

        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public async Task ApplyRejectsDuplicateVariables()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ApplyAsync(
                _configPath,
                new[]
                {
                    new Cs2AutoexecSetting("fps_max", "0"),
                    new Cs2AutoexecSetting("FPS_MAX", "240")
                }));

        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public async Task LoadRejectsFilesOutsideCs2CfgDirectory()
    {
        string wrongPath = Path.Combine(_root, "autoexec.cfg");

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.LoadAsync(wrongPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
