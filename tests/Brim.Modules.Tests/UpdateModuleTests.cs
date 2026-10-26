using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Modules.Tests;

public class UpdateModuleTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brim-update-" + Guid.NewGuid().ToString("N"));
    private readonly UpdateModule _module;
    private int _installs;

    public UpdateModuleTests()
    {
        _hub = new ActivityHub(_time);
        _module = new UpdateModule(_dir, "2.4.0") { InstallRequested = () => _installs++ };
        _module.StartAsync(new ModuleContext(UpdateModule.ModuleId, _hub, new BrimSettings()), CancellationToken.None).Wait();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private ActiveActivity? Find(string id) => _hub.Snapshot().FirstOrDefault(a => a.Key == "update/" + id);

    private void Press(string action) => _module.OnAction(new ActionInvocation(UpdateModule.ModuleId, action));

    [Fact]
    public void New_update_alerts_at_once_and_stays_with_its_version()
    {
        _module.ShowAvailable(new UpdateNotes("2.5.0", "- Neu"));

        Assert.Equal(ActivityPriority.Critical, Find("available-alert")?.Priority);
        Assert.Equal("Update 2.5.0", ((TextWidget)Find("available")!.Activity.Trailing!).Text);

        // Der Hinweis klappt nur kurz auf, die kompakte Anzeige bleibt.
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(Find("available-alert"));
        Assert.NotNull(Find("available"));
    }

    [Fact]
    public void Install_asks_once_and_offers_the_button_again_on_failure()
    {
        _module.ShowAvailable(new UpdateNotes("2.5.0", ""));

        Press(UpdateModule.Install);
        Press(UpdateModule.Install);
        Assert.Equal(1, _installs);
        Assert.Null(Find("available-alert"));

        _module.InstallFailed();
        Press(UpdateModule.Install);
        Assert.Equal(2, _installs);
    }

    [Fact]
    public void Later_hides_the_notice()
    {
        _module.ShowAvailable(new UpdateNotes("2.5.0", ""));

        Press(UpdateModule.Later);
        Assert.Null(Find("available-alert"));
        Assert.Null(Find("available"));

        Press(UpdateModule.Install);
        Assert.Equal(0, _installs);
    }
}
