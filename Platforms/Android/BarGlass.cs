using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;

namespace 青阳AI;

/// <summary>
/// 栏玻璃（Android 10 友好）：定时用 PixelCopy 截取该栏所在窗口区域的真实画面，
/// 缩小再放大得到模糊位图，直接设为栏原生视图的背景——栏本身就绘制这张模糊图，
/// 不依赖额外垫层的 z 序/测量，MAUI 布局不会覆盖它，模糊必然可见。
/// </summary>
public sealed class BarGlass
{
    private global::Android.Views.View _bar;      // 要玻璃化的栏（原生视图）
    private readonly global::Android.Views.View _source;   // 窗口内容源（用于拿 Window）
    private readonly Handler _handler = new Handler(Looper.MainLooper!);
    private readonly Java.Lang.Runnable _tick;
    private bool _running;
    private bool _enabled;
    private float _scale = 0.35f;
    private Bitmap? _full;    // 区域原图
    private Bitmap? _small;   // 缩小模糊图
    private readonly global::Android.Graphics.Paint _paint = new global::Android.Graphics.Paint { FilterBitmap = true };

    public BarGlass(global::Android.Views.View bar, global::Android.Views.View source)
    {
        _bar = bar;
        _source = source;
        _tick = new Java.Lang.Runnable(() => TickOnce());
    }

    /// <summary>切换目标栏（MAUI 重建原生视图后刷新引用用）。</summary>
    public void RestartTarget(global::Android.Views.View bar) => _bar = bar;

    /// <summary>开始玻璃化（pct：0=纯透明不模糊，1=重度模糊）。</summary>
    public void Start(double pct)
    {
        _scale = (float)Math.Max(0.05, 0.45 - pct * 0.5);
        _enabled = pct > 0;
        if (!_enabled)
        {
            Stop();
            _bar.Background = new ColorDrawable(global::Android.Graphics.Color.Transparent);
            return;
        }
        if (_running) return;
        _running = true;
        _handler.PostDelayed(_tick, 80);
    }

    public void Stop()
    {
        _running = false;
        _handler.RemoveCallbacks(_tick);
        _full = null; _small = null;
    }

    private void TickOnce()
    {
        try
        {
            if (_running && _enabled) Capture();
        }
        catch { }
        finally
        {
            if (_running) _handler.PostDelayed(_tick, 300);
        }
    }

    private void Capture()
    {
        var source = _source;
        if (source == null || !_bar.IsAttachedToWindow) return;

        var window = (source.Context as global::Android.App.Activity)?.Window;
        if (window?.DecorView == null) return;

        // 栏在窗口坐标系中的区域
        int l = _bar.Left, t = _bar.Top, r = _bar.Right, b = _bar.Bottom;
        if (r - l < 2 || b - t < 2) return;

        int w = r - l, h = b - t;
        if (_full == null || _full.Width != w || _full.Height != h)
        {
            _full?.Recycle();
            _full = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!);
        }

        var rect = new global::Android.Graphics.Rect(l, t, r, b);
        PixelCopy.Request(window, rect, _full!, new CopyListener(this), _handler);
    }

    private void OnCopied()
    {
        var full = _full;
        if (full == null) return;

        int sw = Math.Max(1, (int)(full.Width * _scale));
        int sh = Math.Max(1, (int)(full.Height * _scale));
        if (_small == null || _small.Width != sw || _small.Height != sh)
        {
            _small?.Recycle();
            _small = Bitmap.CreateBitmap(sw, sh, Bitmap.Config.Argb8888!);
        }
        var c = new Canvas(_small!);
        c.DrawColor(global::Android.Graphics.Color.Transparent, PorterDuff.Mode.Clear!);
        c.DrawBitmap(full, null, new global::Android.Graphics.RectF(0, 0, sw, sh), _paint);

        // 模糊结果直接设为栏的背景（栏本身绘制这张图，必可见）
        try
        {
            ((global::Android.Views.View)_bar).Background =
                new global::Android.Graphics.Drawables.BitmapDrawable(_bar.Context!.Resources!, _small!);
        }
        catch { }
    }

    private sealed class CopyListener : Java.Lang.Object, PixelCopy.IOnPixelCopyFinishedListener
    {
        private readonly BarGlass _owner;
        public CopyListener(BarGlass owner) => _owner = owner;
        public void OnPixelCopyFinished(int copyResult)
        {
            if (copyResult == 0) _owner.OnCopied(); // PixelCopy.Success == 0
        }
    }
}
