using System.Collections.Generic;

namespace 青阳AI;

/// <summary>
/// 全局主题色管理器（单例静态类，替代原先散落各页的「遍历可视树换色」重复实现）。
///
/// 工作方式：XAML 里给需要跟随主题色的控件打标记（StyleId），本类按标记赋色。
///   StyleId="theme"         按控件类型自动推断该改哪个颜色属性
///   StyleId="theme-bg"      明确只改背景色（Border 等）
///   StyleId="theme-stroke"  明确只改描边色（Border.Stroke）
///
/// 关键点：只处理带 theme* 标记的元素。没打标记的（如聊天气泡、输入栏）自动免疫，
/// 不需要额外的跳过名单 —— 也因此「连续切换 N 次颜色」不会像旧实现那样失效。
///
/// 内存：登记的根元素用 WeakReference 持有，页面被回收后自动失效并在下次刷新时清理，
/// 不会阻止 GC，调用方也无需显式退订。
/// </summary>
internal static class ThemeManager
{
    /// <summary>主题色变更通知（页面若需额外自定义处理可订阅；用完记得退订）。</summary>
    public static event Action? ThemeChanged;

    /// <summary>已登记、需要在主题色变化时刷新的根元素（弱引用，不阻止页面回收）。</summary>
    private static readonly List<WeakReference<VisualElement>> Roots = new();

    /// <summary>
    /// 把当前主题色应用到以 root 为根的子树，并把 root 登记为「主题变化时自动刷新」。
    /// 页面在 OnAppearing 里调一次即可；重复调用安全（内部去重）。
    /// </summary>
    public static void Apply(VisualElement? root)
    {
        if (root is null) return;
        Walk(root, AppSettings.ThemeColor);
        Register(root);
    }

    /// <summary>用户切换主题色后调用：先按标记刷新所有已登记页面，再广播给需要自定义处理的页面。</summary>
    public static void NotifyChanged()
    {
        var color = AppSettings.ThemeColor;
        // 先按 StyleId 标记统一刷（就地清理已被 GC 回收的项）
        Roots.RemoveAll(r =>
        {
            if (!r.TryGetTarget(out var root)) return true;  // 已回收 → 移除
            try { Walk(root, color); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ThemeManager.Walk] {ex.Message}"); }
            return false;
        });

        // 再广播：给那些靠代码动态上色、没打标记的控件一次刷新机会
        try { ThemeChanged?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ThemeManager.ThemeChanged] {ex.Message}"); }
    }

    /// <summary>页面销毁时注销（可选：弱引用已能兜底，显式注销更早释放）。</summary>
    public static void Unregister(VisualElement? root)
    {
        if (root is null) return;
        Roots.RemoveAll(r => r.TryGetTarget(out var t) && ReferenceEquals(t, root));
    }

    /// <summary>登记根元素（去重）。</summary>
    private static void Register(VisualElement root)
    {
        Roots.RemoveAll(r => !r.TryGetTarget(out _));   // 顺手清理死引用
        foreach (var r in Roots)
            if (r.TryGetTarget(out var t) && ReferenceEquals(t, root)) return;  // 已登记
        Roots.Add(new WeakReference<VisualElement>(root));
    }

    /// <summary>递归遍历，按 StyleId 标记给控件上色。没标记的元素原样保留。</summary>
    private static void Walk(VisualElement el, Color color)
    {
        var tag = el.StyleId ?? "";
        if (tag.StartsWith("theme", StringComparison.Ordinal))
            Paint(el, tag, color);

        switch (el)
        {
            case Layout layout:
                foreach (var child in layout.Children)
                    if (child is VisualElement ve) Walk(ve, color);
                break;
            // ⚠️ Border 不继承 ContentView（两者都只 implements IContentView），
            //    必须单独处理，否则 Border 里的控件全部遍历不到。
            case Border border when border.Content is VisualElement bc:
                Walk(bc, color);
                break;
            case ContentView cv when cv.Content is VisualElement cvc:
                Walk(cvc, color);
                break;
            case ScrollView sv when sv.Content is VisualElement sc:
                Walk(sc, color);
                break;
            case ContentPage page when page.Content is VisualElement pc:
                Walk(pc, color);
                break;
        }
    }

    /// <summary>按控件类型 + 标记语义，把主题色写进正确的颜色属性。</summary>
    private static void Paint(VisualElement el, string tag, Color color)
    {
        var bgOnly = tag == "theme-bg";
        var strokeOnly = tag == "theme-stroke";
        var auto = !bgOnly && !strokeOnly;

        switch (el)
        {
            case Label l:
                if (auto) l.TextColor = color;
                break;
            case Button b:
                if (!strokeOnly) b.BackgroundColor = color;
                break;
            case CheckBox c:
                if (auto) c.Color = color;
                break;
            case Switch s:
                if (!strokeOnly) s.OnColor = color;
                break;
            case ActivityIndicator ai:
                if (auto) ai.Color = color;
                break;
            case BoxView bv:
                if (!strokeOnly) bv.Color = color;
                break;
            case MorphIcon mi:
                if (auto) mi.IconColor = color;
                break;
            case Slider sl:
                if (!strokeOnly) sl.MinimumTrackColor = color;
                break;
            case Border bd:
                if (strokeOnly) bd.Stroke = new SolidColorBrush(color);
                else if (bgOnly) bd.BackgroundColor = color;
                else if (bd.BackgroundColor is null) bd.Stroke = new SolidColorBrush(color);
                else bd.BackgroundColor = color;
                break;
        }
    }
}
