using LichessBridge.Status;

namespace LichessBridge.Tests;

public sealed class AutoChallengeControllerTests
{
    [Test]
    public void TogglesStateAndPublishesChanges()
    {
        var controller = new AutoChallengeController(true);
        var changes = new List<bool>();
        controller.Changed += changes.Add;

        controller.Toggle();
        var disabled = controller.IsEnabled is false;
        controller.Toggle();

        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.True);
            Assert.That(controller.IsEnabled, Is.True);
            Assert.That(changes, Is.EqualTo(new[] { false, true }));
        });
    }
}
