namespace 青阳AI;

/// <summary>
/// 点阵加载动画：4×4 圆角方块，波浪式脉动（原"转圈"加载条的替代品）。
///
/// 来源：用户给的一段 Web Component（canvas 版 `DotMotionLoader`）。那份实现里
/// 每个格子带着 **120 个采样点 × 3 个通道 × 16 格 ≈ 5800 个数**的原始数据，
/// 照抄进 C# 不现实（又长又容易抄错），所以这里按它的**视觉特征**参数化重建：
///
///   · 4×4 网格、圆角方块、深色底块 + 亮色前块（带辉光）
///   · 周期 1.111 秒、无限循环
///   · 前块大小走 **0→1 的梯形波**（先升、停、再降、停）—— 原数据里正是这种"线性斜坡 + 平台"
///   · 底块透明度 0.58~0.90 同步脉动；前块透明度 0.84~1.00
///   · 相位按 (行+列) 沿对角线传播
///   · 几何与配色照抄原数据（392 画布 / 格子 73 / 步长 93 / 边距 20 / 圆角 16.06；
///     主色 rgb(173,228,255)、底色 rgb(45,55,67)）
///
/// 绘制交给内嵌的 <see cref="Painter"/> —— `GraphicsView` 自己就实现了 `IDrawable`，
/// 直接 `Drawable = this` 会撞上方法隐藏（CS0108），所以不这么干。
/// </summary>
public class DotMotionLoader : GraphicsView
{
    private readonly Painter _painter = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IDispatcherTimer? _timer;

    public DotMotionLoader()
    {
        Drawable = _painter;
        BackgroundColor = Colors.Transparent;
    }

    // ───────────── 生命周期：只在**可见**时跑计时器 ─────────────
    // 气泡列表会复用模板，隐藏的实例要是也在跑 30fps 计时器，纯属白耗电。

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        SyncTimer();
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == IsVisibleProperty.PropertyName) SyncTimer();
    }

    private void SyncTimer()
    {
        try
        {
            if (IsVisible && Handler != null)
            {
                _timer ??= CreateTimer();
                if (!_timer.IsRunning) { _clock.Restart(); _timer.Start(); }
            }
            else
            {
                _timer?.Stop();
            }
        }
        catch { }
    }

    private IDispatcherTimer CreateTimer()
    {
        var t = Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(33);   // ~30fps：加载动画够顺，也不费电
        t.Tick += (_, _) =>
        {
            try
            {
                _painter.Phase = (_clock.Elapsed.TotalSeconds % Painter.DurationSec) / Painter.DurationSec;
                Invalidate();
            }
            catch { }
        };
        return t;
    }

    // ───────────── 实际绘制 ─────────────

    private sealed class Painter : IDrawable
    {
        // ── 几何（原数据的 392 画布坐标系，绘制时整体缩放到控件大小）──
        private const float CanvasSize = 392f;
        private const float CellSize = 73f;
        private const float CellStep = 93f;
        private const float CellMargin = 20f;
        private const float CornerRadius = 16.06f;
        private const int Grid = 4;

        /// <summary>相邻格子的相位差（对角线方向传播）。</summary>
        private const double PhaseStep = 0.16;

        // ── 配色（取自原数据）──
        // primary    = [0.67843, 0.89412, 1.0]
        // background = [0.17647, 0.21569, 0.26275]
        private static readonly Color PrimaryColor = Color.FromRgb(173, 228, 255);
        private static readonly Color BackColor = Color.FromRgb(45, 55, 67);

        /// <summary>一个完整周期的秒数（原数据 duration = 1.1111）。</summary>
        public const double DurationSec = 1.1111;

        /// <summary>当前相位 0..1，由外层计时器每帧写进来。</summary>
        public double Phase { get; set; }

        public void Draw(ICanvas canvas, RectF rect)
        {
            try
            {
                float size = Math.Min(rect.Width, rect.Height);
                if (size <= 2) return;

                float scale = size / CanvasSize;

                canvas.SaveState();
                canvas.Translate(rect.X, rect.Y);
                canvas.Scale(scale, scale);

                for (int row = 0; row < Grid; row++)
                {
                    for (int col = 0; col < Grid; col++)
                    {
                        // 沿对角线传播：越靠右下，相位越靠后
                        double p = Phase - (col + row) * PhaseStep;

                        float grow = Trapezoid(p);                               // 前块大小比例 0..1
                        float backAlpha = 0.58f + 0.32f * Trapezoid(p + 0.18);   // 底块 0.58~0.90
                        float frontAlpha = 0.84f + 0.16f * Trapezoid(p + 0.10);  // 前块 0.84~1.00

                        float x = CellMargin + col * CellStep;
                        float y = CellMargin + row * CellStep;

                        // ① 底块：深色，常驻
                        canvas.FillColor = BackColor.WithAlpha(backAlpha);
                        canvas.FillRoundedRectangle(x, y, CellSize, CellSize, CornerRadius);

                        // ② 前块：亮色，从中心长大/缩小，带辉光
                        float front = CellSize * grow;
                        if (front > 0.5f)
                        {
                            float offset = (CellSize - front) / 2f;
                            float radius = Math.Min(front / 2f, CornerRadius * front / CellSize);

                            // 辉光的模糊半径要跟着缩放走，否则控件很小时会糊成一圈光晕
                            canvas.SetShadow(new SizeF(0, 0), 8f * scale, PrimaryColor.WithAlpha(frontAlpha));
                            canvas.FillColor = PrimaryColor.WithAlpha(frontAlpha);
                            canvas.FillRoundedRectangle(x + offset, y + offset, front, front, radius);
                            canvas.SetShadow(SizeF.Zero, 0, Colors.Transparent);
                        }
                    }
                }

                canvas.RestoreState();
            }
            catch { /* 画崩了也不该影响聊天 */ }
        }

        /// <summary>
        /// 梯形波：先线性升、停、再线性降、停，返回 0..1。
        /// 原数据的采样序列正是"线性斜坡 + 平台"（斜坡约占周期 21%），所以用梯形而不是正弦。
        /// </summary>
        private static float Trapezoid(double x)
        {
            x -= Math.Floor(x);              // 取小数部分 → 0..1
            const double ramp = 0.21;        // 升降各占 21%，其余是平台

            if (x < ramp) return (float)(x / ramp);
            if (x < 0.5) return 1f;
            if (x < 0.5 + ramp) return (float)(1 - (x - 0.5) / ramp);
            return 0f;
        }
    }
}
