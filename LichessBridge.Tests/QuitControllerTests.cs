using LichessBridge.Status;

namespace LichessBridge.Tests;

public sealed class QuitControllerTests
{
    [Test]
    public void TogglesQuitRequestAndPublishesChanges()
    {
        var controller = new QuitController();
        var changes = new List<bool>();
        controller.Changed += changes.Add;

        controller.Toggle();
        var requested = controller.IsRequested;
        controller.Toggle();

        Assert.Multiple(() =>
        {
            Assert.That(requested, Is.True);
            Assert.That(controller.IsRequested, Is.False);
            Assert.That(changes, Is.EqualTo(new[] { true, false }));
        });
    }
}
