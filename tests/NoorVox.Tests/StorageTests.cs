using NoorVox.Core;
using NoorVox.Core.Storage;

namespace NoorVox.Tests;

public class StorageTests
{
    [Fact]
    public void Defaults_live_under_env_home_and_nothing_is_written_on_load()
    {
        using var home = new TestHome();
        var s = home.Storage();
        Assert.Equal(HomeMode.Environment, s.Mode);
        foreach (var kind in Enum.GetValues<StorageKind>())
            Assert.StartsWith(home.Root, s.Resolve(kind).Path, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(s.SettingsPath));
        Assert.All(s.Report(), _ => { });
        Assert.False(Directory.Exists(s.ModelsDir)); // report never creates directories
    }

    [Fact]
    public void Configured_paths_persist_independently_and_reset_to_default()
    {
        using var home = new TestHome();
        var models = Path.Combine(home.Root, "elsewhere", "Models");
        var exports = Path.Combine(home.Root, "other-drive-sim", "Exports");
        var s = home.Storage();
        s.SetPath(StorageKind.Models, models);
        s.SetPath(StorageKind.Exports, exports);

        var reloaded = home.Storage();
        Assert.Equal(models, reloaded.Resolve(StorageKind.Models).Path);
        Assert.Equal("configured", reloaded.Resolve(StorageKind.Models).Source);
        Assert.Equal(exports, reloaded.Resolve(StorageKind.Exports).Path);
        Assert.NotEqual("configured", reloaded.Resolve(StorageKind.Cache).Source);
        Assert.True(Directory.Exists(models)); // set validates by creating + writing a probe

        reloaded.SetPath(StorageKind.Models, null);
        Assert.NotEqual("configured", home.Storage().Resolve(StorageKind.Models).Source);
    }

    [Fact]
    public void C_drive_is_allowed()
    {
        using var home = new TestHome();
        var cPath = Path.Combine(Path.GetTempPath(), "noorvox-tests", "c-allowed-" + Guid.NewGuid().ToString("N")[..6]);
        Assume(cPath.StartsWith("C:", StringComparison.OrdinalIgnoreCase), "temp dir is not on C:");
        var r = home.Storage().SetPath(StorageKind.Temp, cPath);
        Assert.Equal(cPath, r.Path);
        Directory.Delete(cPath, true);
    }

    [Fact]
    public void Corrupt_settings_file_is_a_config_error_not_a_silent_reset()
    {
        using var home = new TestHome();
        File.WriteAllText(Path.Combine(home.Root, "settings.json"), "{ not json");
        var ex = Assert.Throws<NoorVoxException>(() => home.Storage());
        Assert.Equal(ErrorCodes.ConfigError, ex.Code);
        Assert.Equal(ExitCode.ConfigError, ex.ExitCode);
    }

    static void Assume(bool condition, string reason)
    {
        if (!condition) Assert.Skip(reason);
    }
}
