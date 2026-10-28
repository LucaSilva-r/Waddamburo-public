using Waddamburo.App.Home;
using Waddamburo.Game.Scores;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Tests;

public sealed class PlayerSetupFlowTests
{
    [Theory]
    [InlineData(0, SdlKeyboardKey.F)]
    [InlineData(1, SdlKeyboardKey.C)]
    public void OneDrumConfirmsBothVisibleGuests(int starter, SdlKeyboardKey confirm)
    {
        var root = Path.Combine(Path.GetTempPath(), "waddamburo-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var flow = new PlayerSetupFlow(AccountBook.Load(root), null, false, _ => null, root);
            bool[]? playing = null;
            flow.Confirmed += (_, sides) => playing = sides;
            flow.Open(starter);
            Assert.All(flow.Columns(), column => Assert.True(column.Choice.HasDon));
            flow.InputEnabled = true;
            flow.Tick(new SdlKeyboardSnapshot([confirm]));
            Assert.NotNull(playing);
            Assert.Equal([true, true], playing);
            Assert.False(flow.IsOpen);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SoloPlayIsAnExplicitChoiceRatherThanDroppingAnIdleGuest()
    {
        var root = Path.Combine(Path.GetTempPath(), "waddamburo-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var flow = new PlayerSetupFlow(AccountBook.Load(root), null, false, _ => null, root);
            bool[]? playing = null;
            flow.Confirmed += (_, sides) => playing = sides;
            flow.Open(0);
            flow.InputEnabled = true;
            flow.Tick(new SdlKeyboardSnapshot([SdlKeyboardKey.Z]));
            Assert.Equal(SetupChoiceKind.NotPlaying, flow.Columns()[1].Choice.Kind);
            flow.Tick(new SdlKeyboardSnapshot([SdlKeyboardKey.F]));
            Assert.NotNull(playing);
            Assert.Equal([true, false], playing);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
