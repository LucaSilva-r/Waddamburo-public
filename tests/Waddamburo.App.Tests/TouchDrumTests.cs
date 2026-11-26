using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Tests;

public sealed class TouchDrumTests
{
    // Split layout: inside a circle is that side's don, anywhere else ka by the screen's side; dragging moves and resizes.
    [Fact]
    public void SplitCirclesAreDonsAndDragAround()
    {
        var (split, dons) = (TouchDrum.Split, TouchDrum.Dons);
        try
        {
            TouchDrum.Split = true;
            TouchDrum.Dons = [(.3f, .7f, .2f), (.7f, .7f, .2f)];
            Assert.Equal(1, TouchDrum.Hit(.3f, .7f));
            Assert.Equal(2, TouchDrum.Hit(.7f, .7f));
            Assert.Equal(0, TouchDrum.Hit(.45f, .3f));
            Assert.Equal(3, TouchDrum.Hit(.55f, .3f));

            TouchDrum.Grab(1, .3f, .7f);
            TouchDrum.Drag(1, .2f, .5f);
            TouchDrum.Release(1);
            Assert.Equal((.2f, .5f), (TouchDrum.Dons[0].X, TouchDrum.Dons[0].Y), new ToleranceComparer());

            var (kx, ky) = TouchDrum.Knob(1);
            TouchDrum.Grab(2, kx, ky);
            TouchDrum.Drag(2, .7f, .4f);
            Assert.Equal(.3f, TouchDrum.Dons[1].R, .001f);
        }
        finally
        {
            (TouchDrum.Split, TouchDrum.Dons) = (split, dons);
        }
    }

    private sealed class ToleranceComparer : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) a, (float, float) b) => MathF.Abs(a.Item1 - b.Item1) < .001f && MathF.Abs(a.Item2 - b.Item2) < .001f;
        public int GetHashCode((float, float) value) => 0;
    }
}
