using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace 青阳AI;

/// <summary>
/// 呼吸发光状态点：直径 15px 实心圆点 + 在线时周围每 2 秒扩散一圈
/// 半透明圆环（15px→40px，透明度 100%→0%），无限循环，类似 animate-ping。
/// </summary>
public class PingDot : ContentView
{
    private readonly Border _core;
    private readonly Border _ring;
    // 动画代际令牌：每次 SetMode 自增，旧的动画循环发现代际变了就自行退出，
    // 保证同一时刻最多只有一个循环在跑（否则连续切换状态会无限叠加循环把 UI 线程拖死）
    private int _pingEpoch;

    public PingDot()
    {
        _ring = new Border
        {
            WidthRequest = 15,
            HeightRequest = 15,
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(7.5) },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        _core = new Border
        {
            WidthRequest = 15,
            HeightRequest = 15,
            StrokeThickness = 0,
            BackgroundColor = Color.FromArgb("#AAAAAA"),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(7.5) },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        // 容器比点略大，给扩散环留空间
        Content = new Grid
        {
            WidthRequest = 42,
            HeightRequest = 42,
            Children = { _ring, _core }
        };
    }

    /// <summary>设置状态：online=true 绿点+光晕，false 红点无光晕。</summary>
    public void SetMode(bool? online)
    {
        if (online == true)
        {
            StartPing();
        }
        else
        {
            StopPing();
            _core.BackgroundColor = online == false ? Colors.Red : Color.FromArgb("#AAAAAA");
        }
    }

    private void StopPing()
    {
        _pingEpoch++;
        _ring.CancelAnimations(); // 立刻取消在跑的扩散动画，旧循环下次检查代际即退出
        _ring.Scale = 1;
        _ring.Opacity = 1;
        _ring.BackgroundColor = Colors.Transparent;
    }

    private void StartPing()
    {
        var epoch = ++_pingEpoch;
        _core.BackgroundColor = Colors.LimeGreen;
        _ring.BackgroundColor = Color.FromArgb("#66FF66");
        _ = PingLoopAsync(epoch);
    }

    private async Task PingLoopAsync(int epoch)
    {
        const double scaleTarget = 40.0 / 15.0; // 15px → 40px
        while (epoch == _pingEpoch)
        {
            // 复位
            _ring.Scale = 1;
            _ring.Opacity = 1;
            _ring.BackgroundColor = Color.FromArgb("#66FF66");
            // 2 秒扩散 + 淡出
            await Task.WhenAll(
                _ring.ScaleTo(scaleTarget, 2000, Easing.Linear),
                _ring.FadeTo(0, 2000, Easing.Linear));
        }
    }
}

/// <summary>状态点 + 文字的组合徽章，供设置页模型状态显示。</summary>
public class StatusBadge : ContentView
{
    private readonly PingDot _dot;
    private readonly Label _label;

    public StatusBadge()
    {
        _dot = new PingDot();
        _label = new Label
        {
            FontSize = 12,
            TextColor = Color.FromArgb("#AAAAAA"),
            VerticalOptions = LayoutOptions.Center,
        };
        // 用 Grid 固定点位置：点始终在最左固定列，文字在右侧，不随文字长度左右移动
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            }
        };
        grid.Add(_dot, 0, 0);
        grid.Add(_label, 1, 0);
        Content = grid;
    }

    /// <summary>设置状态：true=在线(绿)，false=离线(红)，null=检测中。</summary>
    public void SetStatus(bool? online)
    {
        _dot.SetMode(online);
        if (online == true)
        {
            _label.Text = "在线";
            _label.TextColor = Colors.LimeGreen;
        }
        else if (online == false)
        {
            _label.Text = "离线";
            _label.TextColor = Colors.Red;
        }
        else
        {
            _label.Text = "检测中";
            _label.TextColor = Color.FromArgb("#AAAAAA");
        }
    }

    /// <summary>应用启动时预热：提前构建控件树，避免首次打开设置页时才初始化卡顿。</summary>
    public static void Prewarm()
    {
        try { _ = new StatusBadge(); } catch { }
    }
}