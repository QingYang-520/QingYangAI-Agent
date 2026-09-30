using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// Ta 的「真浏览器」：一个藏在聊天页里的 WebView，由 <c>{web:"动作 参数"}</c> 指令驱动。
///
/// 为什么需要它 —— BrowserTool 走的是 HttpClient + 正则剥文本：
///   · JS 渲染的页面（SPA / 知乎 / 掘金 / 淘宝那类）剥出来是空壳；
///   · 点按钮、翻页、填表单、登录，压根做不到。
/// 这些只有真浏览器内核能给，所以给它一个 WebView 当「手」。
///
/// AI 侧可用动作：
///   {web:"open https://xxx"}   打开网页 → 返回渲染后的正文
///   {web:"text"}               再读一次当前页正文
///   {web:"links"}              列出页面链接（文字 + 地址）
///   {web:"click 登录"}         按可见文字点（也支持 CSS 选择器，如 click #submit）
///   {web:"type #kw|要填的字"}  往输入框填字（竖线分隔）
///   {web:"scroll bottom"}      滚到底（触发懒加载）；也支持 scroll top / scroll 800
///   {web:"back"}               后退
///   {web:"url"}                看当前地址
///
/// 全部 try/catch 兜住，任何一步失败都返回人话说明，绝不抛异常。
/// </summary>
public static class WebAgent
{
    private static WebView? _view;
    private static TaskCompletionSource<bool>? _nav;

    /// <summary>把页面里那个隐藏 WebView 挂上来（ChatPage.OnAppearing 调用）。</summary>
    public static void Attach(WebView view)
    {
        if (ReferenceEquals(_view, view)) return;
        if (_view != null) _view.Navigated -= OnNavigated;
        _view = view;
        _view.Navigated += OnNavigated;
        ApplySettings();
    }

    public static bool Ready => _view != null;

    private static void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        ApplySettings();     // handler 刚建好时也要把 UA / 图片开关打进去
        FlushCookies();      // 把 Cookie 落盘，别等进程被杀才丢
        _nav?.TrySetResult(e.Result == WebNavigationResult.Success);
    }

    // ───────────── 设置联动（设置页「🌐 AI 浏览器」）─────────────

    /// <summary>把设置里的 UA / 是否加载图片 / 是否收 Cookie 应用到隐藏 WebView。</summary>
    public static void ApplySettings()
    {
#if ANDROID
        try
        {
            var wv = _view?.Handler?.PlatformView as Android.Webkit.WebView;
            if (wv == null) return;

            var s = wv.Settings;
            if (s != null)
            {
                s.UserAgentString = AppSettings.EffectiveUserAgent;
                s.LoadsImagesAutomatically = AppSettings.BrowserLoadImages;
                s.BlockNetworkImage = !AppSettings.BrowserLoadImages;
                s.JavaScriptEnabled = true;          // 关掉 JS 等于废掉这个功能，不给开关
                s.DomStorageEnabled = true;
                s.DatabaseEnabled = true;
            }

            var cm = Android.Webkit.CookieManager.Instance;
            if (cm != null)
            {
                cm.SetAcceptCookie(AppSettings.BrowserSaveCookies);
                cm.SetAcceptThirdPartyCookies(wv, AppSettings.BrowserSaveCookies);
            }
        }
        catch { }
#endif
    }

    /// <summary>Cookie 落盘（否则进程一被杀登录态就没了）。</summary>
    private static void FlushCookies()
    {
#if ANDROID
        try
        {
            if (!AppSettings.BrowserSaveCookies) return;
            Android.Webkit.CookieManager.Instance?.Flush();
        }
        catch { }
#endif
    }

    /// <summary>清掉浏览器里的全部 Cookie（退出所有登录）。</summary>
    public static void ClearCookies()
    {
#if ANDROID
        try
        {
            var cm = Android.Webkit.CookieManager.Instance;
            if (cm == null) return;
            cm.RemoveAllCookies(null);
            cm.Flush();
        }
        catch { }
#endif
    }

    // ───────────────── 总入口 ─────────────────

    /// <summary>执行一条浏览器动作，返回给模型看的文字结果。</summary>
    public static async Task<string> RunAsync(string command)
    {
        if (_view == null)
            return "浏览器还没准备好（聊天页还没挂上 WebView）。这种情况先用 {browse:} 顶一下。";
        if (!AppSettings.AiBrowserEnabled)
            return "AI 浏览器被关掉了（设置 → 🌐 AI 浏览器 → 开启）。现在只能用 {browse:} 读静态网页。";
        if (string.IsNullOrWhiteSpace(command))
            return "没给动作。";

        var cmd = command.Trim();
        var sp = cmd.IndexOf(' ');
        var action = (sp < 0 ? cmd : cmd[..sp]).ToLowerInvariant();
        var arg = sp < 0 ? "" : cmd[(sp + 1)..].Trim();

        try
        {
            return action switch
            {
                "open" or "goto" or "navigate" => await OpenAsync(arg),
                "text" => await TextAsync(),
                "links" => await LinksAsync(),
                "click" => await ClickAsync(arg),
                "type" => await TypeAsync(arg),
                "scroll" => await ScrollAsync(arg),
                "back" => await BackAsync(),
                "url" => "当前地址：" + await EvalAsync("location.href"),
                _ => $"不认识的浏览器动作「{action}」。可用：open / text / links / click / type / scroll / back / url"
            };
        }
        catch (Exception ex)
        {
            return "浏览器操作失败：" + ex.Message;
        }
    }

    // ───────────────── 各个动作 ─────────────────

    private static async Task<string> OpenAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "没给网址。";
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url.TrimStart('/');

        var tcs = new TaskCompletionSource<bool>();
        _nav = tcs;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _view!.Source = new UrlWebViewSource { Url = url };
        });

        var done = await Task.WhenAny(tcs.Task, Task.Delay(25000));
        _nav = null;
        if (done != tcs.Task)
            return $"打开超时（25 秒）：{url}。可能网太慢或这个站不让直接访问。";

        // 再等一拍，让页面自己的 JS 把内容渲染出来
        await Task.Delay(1200);
        var text = await TextAsync();
        try
        {
            var href = (await EvalAsync("location.href")).Trim();
            var title = (await EvalAsync("document.title")).Trim();
            AppSettings.AddBrowserHistory("浏览 " + (string.IsNullOrEmpty(title) ? href : title + " — " + href));
        }
        catch { }
        return text;
    }

    private static async Task<string> TextAsync()
    {
        var title = await EvalAsync("document.title");
        var href = await EvalAsync("location.href");
        var body = (await EvalAsync(
            "(function(){try{return document.body?document.body.innerText:'';}catch(e){return '';}})()")).Trim();

        if (body.Length == 0)
            return $"标题：{title}\n地址：{href}\n\n（正文是空的——可能还在加载，或内容在 iframe 里、需要先交互才显示）";

        if (body.Length > 6000) body = body[..6000] + "…（截断）";
        return $"标题：{title}\n地址：{href}\n\n正文：\n{body}";
    }

    private static async Task<string> LinksAsync()
    {
        var js =
            "(function(){try{var out=[];var as=document.querySelectorAll('a[href]');" +
            "for(var i=0;i<as.length&&out.length<60;i++){var a=as[i];" +
            "var t=(a.innerText||a.textContent||'').trim().replace(/\\s+/g,' ').slice(0,50);" +
            "var h=a.href;if(!h||h.indexOf('javascript:')===0)continue;" +
            "out.push((t||'(无文字)')+' -> '+h);}return out.join('\\n');}catch(e){return '';}})()";

        var s = (await EvalAsync(js)).Trim();
        return string.IsNullOrEmpty(s) ? "（这个页面上没找到链接）" : "页面链接（最多 60 条）：\n" + s;
    }

    private static async Task<string> ClickAsync(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "没给要点的东西。";

        var lit = JsonSerializer.Serialize(target);
        var js =
            "(function(){try{var want=" + lit + ";var wantL=want.toLowerCase();" +
            "var el=null;try{el=document.querySelector(want);}catch(e){}" +
            "if(!el){var all=document.querySelectorAll('a,button,input[type=submit],[role=button],li,div,span');" +
            "for(var i=0;i<all.length;i++){var s=(all[i].innerText||all[i].value||'').trim().toLowerCase();" +
            "if(s===wantL){el=all[i];break;}}" +
            "if(!el){for(var i=0;i<all.length;i++){var s=(all[i].innerText||all[i].value||'').trim().toLowerCase();" +
            "if(s&&s.indexOf(wantL)>=0){el=all[i];break;}}}}" +
            "if(!el)return 'NOTFOUND';" +
            "el.scrollIntoView({block:'center'});el.click();return 'OK';}catch(e){return 'ERR:'+e.message;}})()";

        var r = (await EvalAsync(js)).Trim();
        if (r == "NOTFOUND") return $"没找到能点的「{target}」。先用 {{web:\"links\"}} 看看页面上有什么。";
        if (r.StartsWith("ERR")) return "点击失败：" + r;

        await Task.Delay(1500);
        return $"已点击「{target}」。\n\n" + await TextAsync();
    }

    private static async Task<string> TypeAsync(string arg)
    {
        var bar = arg.IndexOf('|');
        if (bar < 0) return "格式是 {web:\"type 选择器|要填的字\"}，中间用竖线隔开。";

        var sel = arg[..bar].Trim();
        var val = arg[(bar + 1)..];

        var js =
            "(function(){try{var el=document.querySelector(" + JsonSerializer.Serialize(sel) + ");" +
            "if(!el)return 'NOTFOUND';el.focus();el.value=" + JsonSerializer.Serialize(val) + ";" +
            "el.dispatchEvent(new Event('input',{bubbles:true}));" +
            "el.dispatchEvent(new Event('change',{bubbles:true}));return 'OK';}catch(e){return 'ERR:'+e.message;}})()";

        var r = (await EvalAsync(js)).Trim();
        if (r == "NOTFOUND") return $"没找到输入框「{sel}」。";
        if (r.StartsWith("ERR")) return "填字失败：" + r;
        return $"已往「{sel}」填入内容。";
    }

    private static async Task<string> ScrollAsync(string arg)
    {
        var a = arg.Trim().ToLowerInvariant();
        string js;
        if (a.Length == 0 || a == "bottom" || a == "end")
            js = "window.scrollTo(0, document.body.scrollHeight);'OK'";
        else if (a == "top")
            js = "window.scrollTo(0,0);'OK'";
        else
        {
            if (!int.TryParse(a, out var px)) px = 800;
            js = $"window.scrollBy(0, {px});'OK'";
        }

        await EvalAsync(js);
        await Task.Delay(900);
        return "已滚动。\n\n" + await TextAsync();
    }

    private static async Task<string> BackAsync()
    {
        await EvalAsync("(function(){try{history.back();return 'OK';}catch(e){return 'ERR';}})()");
        await Task.Delay(1500);
        return "已后退。\n\n" + await TextAsync();
    }

    // ───────────────── 底层 ─────────────────

    /// <summary>在主线程上跑一段 JS，拿回字符串结果（失败返回空串，绝不抛）。</summary>
    private static async Task<string> EvalAsync(string js)
    {
        var v = _view;
        if (v == null) return "";
        try
        {
            var raw = await MainThread.InvokeOnMainThreadAsync(() => v.EvaluateJavaScriptAsync(js));
            raw ??= "";
            // MAUI 对字符串结果会返回带引号的 JSON，剥掉
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                try { raw = JsonSerializer.Deserialize<string>(raw) ?? raw; } catch { }
            }
            return raw;
        }
        catch { return ""; }
    }
}
