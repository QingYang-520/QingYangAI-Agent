using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace 青阳AI
{
    public class MorphIcon : ContentView
    {
        static readonly Dictionary<string, string> Paths = new()
        {
            ["ChevronRight"] = "M9 18 L15 12 L9 6",
            ["ChevronLeft"]  = "M15 18 L9 12 L15 6",
            ["ChevronDown"]  = "M6 9 L12 15 L18 9",
            ["ChevronUp"]    = "M18 15 L12 9 L6 15",
            ["X"]            = "M18 6 L6 18 M6 6 L18 18",
            ["Menu"]         = "M4 6 H20 M4 12 H20 M4 18 H20",
            ["Mic"]          = "M12 2 A3 3 0 0 0 9 5 V11 A3 3 0 0 0 15 11 V5 A3 3 0 0 0 12 2 Z M19 10 V12 A7 7 0 0 1 5 12 V10 M12 19 V22",
            ["Image"]        = "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M9 7 A2 2 0 1 0 9 11 A2 2 0 0 0 9 7 Z M21 15 L17.914 11.914 A2 2 0 0 0 15.086 11.914 L6 21",
            ["Send"]         = "M22 2 L11 13 M22 2 L15 22 L11 13 L2 9 Z",
            ["Stop"]         = "M9 7 H15 A2 2 0 0 1 17 9 V15 A2 2 0 0 1 15 17 H9 A2 2 0 0 1 7 15 V9 A2 2 0 0 1 9 7 Z",
            ["Trash"]        = "M3 6 H21 M8 6 V4 A2 2 0 0 1 10 2 H14 A2 2 0 0 1 16 4 V6 M19 6 L18 20 A2 2 0 0 1 16 22 H8 A2 2 0 0 1 6 20 L5 6 M10 11 V17 M14 11 V17",
            ["Speaker"]      = "M11 5 L6 9 L2 9 L2 15 L6 15 L11 19 Z M15.5 8.5 A5 5 0 0 1 15.5 15.5 M18.8 5.6 A9 9 0 0 1 18.8 18.4",
            ["Book"]         = "M5 3 L17 3 A2 2 0 0 1 19 5 L19 19 A2 2 0 0 1 17 21 L5 21 Z M9 3 L9 21 M12.5 8 L16 8 M12.5 12 L16 12",
            ["Settings"]     = "M12.22 2 H11.78 A2 2 0 0 0 9.78 4 V4.18 A2 2 0 0 1 8.78 5.91 L8.35 6.16 A2 2 0 0 1 6.35 6.16 L6.2 6.08 A2 2 0 0 0 3.47 6.81 L3.25 7.19 A2 2 0 0 0 3.98 9.92 L4.13 10.02 A2 2 0 0 1 5.13 11.74 V12.25 A2 2 0 0 1 4.13 13.97 L3.98 14.06 A2 2 0 0 0 3.25 16.79 L3.47 17.17 A2 2 0 0 0 6.2 17.9 L6.35 17.82 A2 2 0 0 1 8.35 17.82 L8.78 18.07 A2 2 0 0 1 9.78 19.8 V20 A2 2 0 0 0 11.78 22 H12.22 A2 2 0 0 0 14.22 20 V19.82 A2 2 0 0 1 15.22 18.09 L15.65 17.84 A2 2 0 0 1 17.65 17.84 L17.8 17.92 A2 2 0 0 0 20.53 17.19 L20.75 16.81 A2 2 0 0 0 20.02 14.08 L19.87 13.98 A2 2 0 0 1 18.87 12.26 V11.75 A2 2 0 0 1 19.87 10.03 L20.02 9.94 A2 2 0 0 0 20.75 7.21 L20.53 6.83 A2 2 0 0 0 17.8 6.1 L17.65 6.18 A2 2 0 0 1 15.65 6.18 L15.22 5.93 A2 2 0 0 1 14.22 4.2 V4 A2 2 0 0 0 12.22 2 Z M12 9 A3 3 0 1 0 12 15 A3 3 0 0 0 12 9 Z",
            ["Globe"]        = "M12 3 A9 9 0 1 0 12 21 A9 9 0 0 0 12 3 Z M3 12 H21 M12 3 A15 15 0 0 1 12 21 M12 3 A15 15 0 0 0 12 21",
            ["Wifi"]         = "M5 12 A11 11 0 0 1 19 12 M8.5 15.5 A6.5 6.5 0 0 1 15.5 15.5 M12 19 H12.01",
            ["ArrowLeft"]    = "M19 12 H5 M12 19 L5 12 L12 5",
            ["ArrowRight"]   = "M5 12 H19 M12 5 L19 12 L12 19",
            ["RotateCw"]     = "M21 12 A9 9 0 1 1 12 3 M21 3 V9 H15",
            ["MessageCircle"]= "M21 11.5 A8.5 8.5 0 0 1 5.7 17.5 L3 20 L3 13 A8.5 8.5 0 0 1 21 11.5 Z",
            ["MoreVertical"] = "M12 5 A1.5 1.5 0 1 0 12 8 A1.5 1.5 0 0 0 12 5 Z M12 11 A1.5 1.5 0 1 0 12 14 A1.5 1.5 0 0 0 12 11 Z M12 17 A1.5 1.5 0 1 0 12 20 A1.5 1.5 0 0 0 12 17 Z",
            ["Home"]         = "M3 11 L12 3 L21 11 M5 10 V20 A1 1 0 0 0 6 21 H10 V14 H14 V21 H18 A1 1 0 0 0 19 20 V10",
            ["Search"]       = "M10 4 A6 6 0 1 0 10 16 A6 6 0 0 0 10 4 Z M15 15 L20 20",
            ["Shield"]       = "M12 2 L20 6 V12 C20 17 16.5 20.5 12 22 C7.5 20.5 4 17 4 12 V6 Z",
            ["ShieldCheck"]  = "M12 2 L20 6 V12 C20 17 16.5 20.5 12 22 C7.5 20.5 4 17 4 12 V6 Z M8.5 12 L11 14.5 L15.5 10",
            ["File"]         = "M14 2 H6 A2 2 0 0 0 4 4 V20 A2 2 0 0 0 6 22 H18 A2 2 0 0 0 20 20 V8 Z M14 2 V8 H20",
            ["ShieldAlert"]  = "M12 2 L20 6 V12 C20 17 16.5 20.5 12 22 C7.5 20.5 4 17 4 12 V6 Z M12 8 V13 M12 16 H12.01",
        };

        readonly Microsoft.Maui.Controls.Shapes.Path _path;
        string _currentIcon = "";
        bool _animating;

        public static readonly BindableProperty IconProperty = BindableProperty.Create(
            nameof(Icon), typeof(string), typeof(MorphIcon), "ChevronRight",
            propertyChanged: async (b, o, n) =>
            {
                if (b is MorphIcon m && n is string s && s != m._currentIcon)
                    await m.AnimateTo(s);
            });

        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly BindableProperty IconColorProperty = BindableProperty.Create(
            nameof(IconColor), typeof(Color), typeof(MorphIcon), Colors.White,
            propertyChanged: (b, o, n) =>
            {
                if (b is MorphIcon m && n is Color c) m._path.Stroke = c;
            });

        public Color IconColor
        {
            get => (Color)GetValue(IconColorProperty);
            set => SetValue(IconColorProperty, value);
        }

        public static readonly BindableProperty IconSizeProperty = BindableProperty.Create(
            nameof(IconSize), typeof(double), typeof(MorphIcon), 20.0,
            propertyChanged: (b, o, n) =>
            {
                if (b is MorphIcon m && n is double d)
                {
                    m.WidthRequest = d;
                    m.HeightRequest = d;
                    m._path.WidthRequest = d;
                    m._path.HeightRequest = d;
                }
            });

        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public static readonly BindableProperty StrokeWidthProperty = BindableProperty.Create(
            nameof(StrokeWidth), typeof(double), typeof(MorphIcon), 2.0,
            propertyChanged: (b, o, n) =>
            {
                if (b is MorphIcon m && n is double d) m._path.StrokeThickness = d;
            });

        public double StrokeWidth
        {
            get => (double)GetValue(StrokeWidthProperty);
            set => SetValue(StrokeWidthProperty, value);
        }

        public MorphIcon()
        {
            _path = new Microsoft.Maui.Controls.Shapes.Path
            {
                Stroke = IconColor,
                StrokeThickness = StrokeWidth,
                StrokeLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Aspect = Stretch.Uniform,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                WidthRequest = IconSize,
                HeightRequest = IconSize,
            };
            WidthRequest = IconSize;
            HeightRequest = IconSize;
            Content = _path;
            SetPathData(Icon);
        }

        void SetPathData(string icon)
        {
            if (Paths.TryGetValue(icon, out var data))
            {
                _path.Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(data)!;
                _currentIcon = icon;
            }
        }

        async Task AnimateTo(string newIcon)
        {
            if (_animating)
            {
                _path.CancelAnimations();
                _path.Opacity = 1;
                _path.Rotation = 0;
            }
            _animating = true;

            try
            {
                await Task.WhenAll(
                    _path.FadeTo(0, 120, Easing.CubicIn),
                    _path.RotateTo(90, 120, Easing.CubicIn));

                _path.Rotation = -90;
                SetPathData(newIcon);

                await Task.WhenAll(
                    _path.FadeTo(1, 240, Easing.CubicOut),
                    _path.RotateTo(0, 240, Easing.CubicOut));
            }
            catch
            {
                _path.Opacity = 1;
                _path.Rotation = 0;
                SetPathData(newIcon);
            }
            finally
            {
                _animating = false;
            }
        }

    /// <summary>应用启动时预热：提前解析全部图标路径，避免首次打开设置页时才解析卡顿。</summary>
    public static void Prewarm()
    {
        try
        {
            foreach (var kv in Paths)
            {
                _ = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(kv.Value)!;
            }
        }
        catch { /* 预热失败不影响运行 */ }
    }
}
}
