using Beachtro.Application.Runtime;
using Beachtro.Infrastructure.Runtime;

namespace Beachtro.Infrastructure.Tests.Runtime;

public sealed class RuntimeTests
{
    [Fact]
    public void Default_profile_is_Test()
    {
        Assert.Equal(RuntimeProfile.Test, RuntimeProfilePolicy.Parse([]));
        Assert.Equal(RuntimeProfile.Test, RuntimeProfilePolicy.Parse(["--profile", "test"]));
    }

    [Fact]
    public void Production_profile_is_refused_in_this_build()
    {
        var profile = RuntimeProfilePolicy.Parse(["--profile", "Production"]);
        Assert.Throws<ProfileNotAllowedException>(() => RuntimeProfilePolicy.EnsureAllowed(profile));
    }

    [Fact]
    public void Unknown_profile_is_rejected()
    {
        Assert.Throws<ProfileNotAllowedException>(() => RuntimeProfilePolicy.Parse(["--profile", "Staging"]));
        Assert.Throws<ProfileNotAllowedException>(() => RuntimeProfilePolicy.Parse(["--profile", "7"]));
    }

    [Fact]
    public void Test_and_production_data_roots_differ()
    {
        var test = LocalDataPaths.ForProfile(RuntimeProfile.Test);
        var prod = LocalDataPaths.ForProfile(RuntimeProfile.Production);
        Assert.NotEqual(test.Root, prod.Root);
        Assert.EndsWith("BeachtroPOS-Test", test.Root, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCreated_creates_all_operational_folders()
    {
        using var temp = new TempDirectory();
        var paths = new LocalDataPaths(Path.Combine(temp.Path, "root"), RuntimeProfile.Test);

        paths.EnsureCreated();

        foreach (var dir in paths.AllDirectories)
        {
            Assert.True(Directory.Exists(dir), dir);
        }

        Assert.Equal(6, paths.AllDirectories.Count());
    }

    [Fact]
    public void Second_writer_for_same_data_directory_is_refused()
    {
        using var temp = new TempDirectory();
        var paths = new LocalDataPaths(temp.Path, RuntimeProfile.Test);

        using (var first = SingleInstanceLock.TryAcquire(paths))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstanceLock.TryAcquire(paths));
        }

        using var again = SingleInstanceLock.TryAcquire(paths);
        Assert.NotNull(again);
    }

    [Fact]
    public void Mutex_name_is_stable_and_case_insensitive_per_root()
    {
        Assert.Equal(SingleInstanceLock.MutexName(@"C:\Data\Pos"), SingleInstanceLock.MutexName(@"c:\data\pos\"));
        Assert.NotEqual(SingleInstanceLock.MutexName(@"C:\Data\Pos"), SingleInstanceLock.MutexName(@"C:\Data\Pos2"));
    }
}
