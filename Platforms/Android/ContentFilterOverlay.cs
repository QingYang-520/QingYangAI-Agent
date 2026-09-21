using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;

namespace 青阳AI;

/// <summary>
/// 玻璃滤镜层（PixelCopy 版，API 26+）：定时从窗口渲染结果按本层区域截取真实画面，
/// 缩到极小再放大回绘 = 真模糊。之前的 source.Draw(软件画布) 在部分设备上捕获不到
/// 硬件加速的列表内容（画出来是空的），PixelCopy 直接复制窗口像素，必有画面。
/// 不消费触摸；位置镜像目标栏视图或钉在父容器底部导航条区。
/// </summary>
public class ContentFilterOverlay : global::Android.Views.View
{
    private global::Android.Views.View? _source;
    private global::Android.Views.View? _mirror;
    private int _navInset;
    private global::Android.Views.View? _layoutWatch;
    private float _scale = 0.35f;
    private bool _enabled;
    private bool _running;
    private readonly Handler _handler = new Handler(Looper.MainLooper!);
    private readonly Java.Lang.Runnable _tick;
    private global::Android.Graphics.Bitmap? _full;   // 区域原始截屏
    private global::Android.Graphics.Bitmap? _small;  // 缩小版（模糊源）
    private readonly global::Android.Graphics.Paint _paint = new global::Android.Graphics.Paint { FilterBitmap = true };

    public ContentFilterOverlay(global::Android.Content.Context context) : base(context)
    {
        _tick = new Java.Lang.Runnable(() => TickOnce());
    }

    /// <summary>挂载：source=窗口内容源视图（用于拿 Window）；pct=模糊百分比；mirror=位置镜像栏；navInset&gt;0 钉在父容器底部。</summary>
    public void Attach(global::Android.Views.View source, double pct, global::Android.Views.View? mirror = null, int navInset = 0)
    {
        DetachInternal();
        _source = source;
        _mirror = mirror;
        _navInset = navInset;
        _scale = (float)Math.Max(0.05, 0.45 - pct * 0.5);
        _enabled = pct > 0;

        if (_mirror != null)
            _mirror.LayoutChange += OnMirrorLayoutChange;

        PositionSelf();
        Visibility = ViewStates.Visible;

        if (_enabled) StartLoop();
    }

    public void Detach() => DetachInternal();

    private void DetachInternal()
    {
        StopLoop();
        if (_mirror != null) _mirror.LayoutChange -= OnMirrorLayoutChange;
        if (_layoutWatch != null) _layoutWatch.LayoutChange -= OnMirrorLayoutChange;
        _source = null; _mirror = null; _layoutWatch = null;
        _full = null; _small = null;
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        if (_navInset > 0) WatchParentLayout();
        PositionSelf();
        if (_enabled) StartLoop(); // 重新挂回窗口时恢复截取循环
    }

    protected override void OnDetachedFromWindow()
    {
        base.OnDetachedFromWindow();
        StopLoop(); // 页面销毁/切后台离开窗口时停止 PixelCopy 循环（防耗电）
    }

    private void WatchParentLayout()
    {
        if (_layoutWatch != null) return;
        if (Parent is global::Android.Views.View p)
        {
            _layoutWatch = p;
            p.LayoutChange += OnMirrorLayoutChange;
        }
    }

    private void PositionSelf()
    {
        if (_mirror != null)
        {
            Layout(_mirror.Left, _mirror.Top, _mirror.Right, _mirror.Bottom);
        }
        else if (_navInset > 0 && Parent is global::Android.Views.View p && p.Height > 0)
        {
            Layout(0, p.Height - _navInset, p.Width, p.Height);
        }
    }

    private void OnMirrorLayoutChange(object? sender, LayoutChangeEventArgs e) => PositionSelf();

    // ─────────── 截取循环（~150ms 一次，滚不滚动都新鲜） ───────────

    private void StartLoop()
    {
        if (_running) return;
        _running = true;
        _handler.PostDelayed(_tick, 150);
    }

    private void StopLoop()
    {
        _running = false;
        _handler.RemoveCallbacks(_tick);
    }

    private void TickOnce()
    {
        try
        {
            if (!_running || !_enabled) return;
            PositionSelf();
            CaptureOnce();
        }
        catch { }
        finally
        {
            if (_running) _handler.PostDelayed(_tick, 150);
        }
    }

    private void CaptureOnce()
    {
        var source = _source;
        if (source == null || !_enabled || Width <= 0 || Height <= 0 || !IsAttachedToWindow) return;

        var window = (source.Context as global::Android.App.Activity)?.Window;
        if (window == null) return;

        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        if (_full == null || _full.Width != w || _full.Height != h)
        {
            _full?.Recycle();
            _full = global::Android.Graphics.Bitmap.CreateBitmap(w, h, global::Android.Graphics.Bitmap.Config.Argb8888!);
        }

        // 本层在窗口坐标系中的区域（父容器铺满窗口内容区）
        int l = Math.Max(0, Left), t = Math.Max(0, Top);
        int r = Math.Min(window.DecorView!.Width, Right), b = Math.Min(window.DecorView.Height, Bottom);
        if (r - l < 2 || b - t < 2) return;

        var rect = new global::Android.Graphics.Rect(l, t, r, b);
        var bm = _full!;
        global::Android.Views.PixelCopy.Request(window, rect, bm, new CopyListener(this), _handler);
    }

    private sealed class CopyListener : Java.Lang.Object, global::Android.Views.PixelCopy.IOnPixelCopyFinishedListener
    {
        private readonly ContentFilterOverlay _owner;
        public CopyListener(ContentFilterOverlay owner) => _owner = owner;
        public void OnPixelCopyFinished(int copyResult)
        {
            // PixelCopy.Success == 0（该常量被标记过时，直接数值比较）
            if (copyResult == 0) _owner.OnCopied();
        }
    }

    /// <summary>截屏成功：缩进小位图（产生模糊源）并重绘。</summary>
    private void OnCopied()
    {
        var full = _full;
        if (full == null) return;

        int sw = Math.Max(1, (int)(full.Width * _scale));
        int sh = Math.Max(1, (int)(full.Height * _scale));
        if (_small == null || _small.Width != sw || _small.Height != sh)
        {
            _small?.Recycle();
            _small = global::Android.Graphics.Bitmap.CreateBitmap(sw, sh, global::Android.Graphics.Bitmap.Config.Argb8888!);
        }
        var canvas = new global::Android.Graphics.Canvas(_small!);
        canvas.DrawColor(global::Android.Graphics.Color.Transparent, global::Android.Graphics.PorterDuff.Mode.Clear!);
        canvas.DrawBitmap(full, null, new global::Android.Graphics.RectF(0, 0, sw, sh), _paint);
        Invalidate();
    }

    protected override void OnDraw(global::Android.Graphics.Canvas? canvas)
    {
        var small = _small;
        if (small == null || canvas == null || !_enabled) return;
        canvas.DrawBitmap(small, null, new global::Android.Graphics.RectF(0, 0, Width, Height), _paint);
    }

    // 触摸穿透：本层只负责垫玻璃，不拦点击
    public override bool OnTouchEvent(global::Android.Views.MotionEvent? e) => false;
}
