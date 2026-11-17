using Android.Content;
using Android.Runtime;
using Android.Views;
using Org.Libsdl.App;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.Android;

/// <summary>SDL's surface view, letting the renderer finish with the surface before Android destroys it.</summary>
internal sealed class GameSurface : SDLSurface
{
    public GameSurface(Context? context)
        : base(context)
    {
    }

    private GameSurface(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void SurfaceChanged(ISurfaceHolder? holder, global::Android.Graphics.Format format, int width, int height)
    {
        base.SurfaceChanged(holder, format, width, height);
        SdlApplication.SurfaceReady();
    }

    public override void SurfaceDestroyed(ISurfaceHolder? holder)
    {
        SdlApplication.SurfaceDestroying();
        base.SurfaceDestroyed(holder);
    }
}
