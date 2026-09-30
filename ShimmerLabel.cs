using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;

namespace 青阳AI;

/// <summary>
/// 文字流光 Label：高光光带沿 45° 对角斜向扫描文字，只作用于文字本体（TextView Paint 渐变着色器）。
/// 动效：左上→右下→左上 来回滚动，位移用二次方 EaseInOut 曲线（慢→快→慢）。
/// 通过 IsActive 控制启停（如思考中/终端运行中才流光，完成即停）。
/// </summary>
public class ShimmerLabel : Label
{
    private IDispatcherTimer? _timer;
    private DateTime _start;

    /// <summary>是否开启流光（true 启动，false 停止并清除）。</summary>
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(ShimmerLabel), false,
            propertyChanged: (b, o, n) =>
            {
                var label = (ShimmerLabel)b;
                if (n is true) label.StartShimmer();
                else label.StopShimmer();
            });

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>高光色（可配置）。未设置时自动向白色提亮。</summary>
    public static readonly BindableProperty HighlightColorProperty =
        BindableProperty.Create(nameof(HighlightColor), typeof(Color), typeof(ShimmerLabel), null);

    public Color? HighlightColor
    {
        get => (Color?)GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (IsActive && Handler?.PlatformView != null)
            StartShimmer();
    }

    /// <summary>启动流光动画（无碍重复调用）。</summary>
    public void StartShimmer()
    {
        if (_timer != null) return;
#if ANDROID
        _start = DateTime.Now;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnTick;
        _timer.Start();
#endif
    }

    /// <summary>停止流光动画并清除着色器。</summary>
    public void StopShimmer()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
#if ANDROID
        if (Handler?.PlatformView is Android.Widget.TextView tv)
        {
            tv.Paint.SetShader(null);
            tv.Invalidate();
        }
#endif
    }

    /// <summary>二次方缓出（先快后慢）：速度比例 = 1-(1-t)^2，无阻尼系数。</summary>
    private static double QuadEaseOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - (1 - t) * (1 - t);
    }

    private void OnTick(object? sender, EventArgs e)
    {
#if ANDROID
        if (Handler?.PlatformView is not Android.Widget.TextView tv) return;
        var c = TextColor;
        var text = tv.Text ?? Text ?? "";
        float textW = Math.Max(1f, tv.Paint.MeasureText(text));
        var fm = new Android.Graphics.Paint.FontMetrics();
        tv.Paint.GetFontMetrics(fm);
        float textH = Math.Max(1f, fm.Bottom - fm.Top);
        const float bandW = 90f;           // 高光光带宽
        const double cycleMs = 2400;       // 单程时长 2.4s（来回 4.8s）

        // 0..2 来回相位：0→1 正向（左上→右下），1→2 反向（右下→左上）
        double phase = (DateTime.Now - _start).TotalMilliseconds / cycleMs % 2;
        double p = phase <= 1 ? phase : 2 - phase;
        // 每段单独用二次方缓出：先快后慢（正向、反向都一样）
        float pos = (float)(QuadEaseOut(p) * (textW + textH)); // 对角扫描位置

        // 高光色：配置优先，否则向白色提亮
        Color hl = HighlightColor ?? Color.FromRgba(
            Math.Min(1.0, c.Red + 0.5),
            Math.Min(1.0, c.Green + 0.5),
            Math.Min(1.0, c.Blue + 0.5), 1);

        int baseArgb = c.ToInt();
        int hlArgb = hl.ToInt();

        // 45° 对角渐变：光带沿左上→右下方向
        using var shader = new Android.Graphics.LinearGradient(
            pos - bandW, pos - bandW, pos, pos,
            new[] { baseArgb, hlArgb, baseArgb },
            new[] { 0f, 0.5f, 1f },
            Android.Graphics.Shader.TileMode.Clamp!);

        tv.Paint.SetShader(shader);
        tv.Invalidate();
#endif
    }
}