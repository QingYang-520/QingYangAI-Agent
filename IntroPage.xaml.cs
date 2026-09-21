using Microsoft.Maui.Controls;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// 启动动画页：氛围光斑背景 + 固定「与AI一起」+ 循环打字动态文本（流动渐变、玻璃卡片）；
/// 打字行与「立即体验」之间为三行厂商 Logo 无限滚动墙（1/3 行向左、2 行向右，恒速无缝）；
/// 左侧渐变欢迎语 + 底部「立即体验」发光按钮跳转协议页。
/// </summary>
public partial class IntroPage : ContentPage
{
    public IntroPage()
    {
        InitializeComponent();
        webIntro.Navigating += OnNavigating;
        _ = LoadAsync();
    }

    /// <summary>枚举打包进 Assets 的厂商 Logo，生成页面并设置 Android 资产根目录。</summary>
    private async Task LoadAsync()
    {
        var logos = await GetLogoListAsync();
#if ANDROID
        webIntro.Source = new HtmlWebViewSource
        {
            BaseUrl = "file:///android_asset/",
            Html = BuildHtml(logos)
        };
#else
        webIntro.Source = new HtmlWebViewSource { Html = BuildHtml(logos) };
#endif
    }

    /// <summary>
    /// 枚举打包进 Assets 的厂商 Logo（Assets 实际路径带 Resources/Raw 前缀：
    /// Resources/Raw/ailogos/*.svg）。构建时按厂商文件修改时间倒序编号（ai_001 最新），
    /// 新增厂商自动排在最前。
    /// </summary>
    private static async Task<List<string>> GetLogoListAsync()
    {
        var names = new List<string>();
#if ANDROID
        try
        {
            const string baseDir = "Resources/Raw/ailogos";
            var assets = Microsoft.Maui.ApplicationModel.Platform.AppContext.Assets;
            foreach (var n in assets!.List(baseDir) ?? Array.Empty<string>())
                if (n.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    names.Add(baseDir + "/" + n);
        }
        catch { }
#endif
        return await Task.FromResult(names);
    }

    /// <summary>拦截 WebView 内按钮跳转：qingyang://next → 进入协议页。
    /// 裸窗口不能用 PushAsync；改为替换窗口页面——但必须在 WebView 导航回调
    /// 完全结束之后再换（延迟一拍），否则会在回调栈里拆页面直接闪退。</summary>
    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url.StartsWith("qingyang://next", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true; // 先阻止 WebView 实际导航
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), () =>
            {
                try
                {
                    Application.Current?.Windows[0].Page = new AgreementPage();
                }
                catch { /* 替换失败不崩溃（理论上不会发生） */ }
            });
        }
    }

    private static string BuildHtml(List<string> logos)
    {
        // __LOGOS__ 占位符在方法末尾替换为 JSON 数组（原始字符串里的 CSS/JS 花括号无需转义）
        var html = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no">
        <style>
          @font-face {
            font-family:'MapleMono';
            src:url('https://cdn.jsdelivr.net/npm/@fontsource/maple-mono@5.2.6/files/maple-mono-latin-400-normal.woff2') format('woff2');
            font-display:swap;
          }
          *{margin:0;padding:0;box-sizing:border-box}
          html,body{height:100%}
          body{
            background:#111827;
            color:#fff;
            font-family:'MapleMono','Maple Mono',ui-monospace,SFMono-Regular,Menlo,Consolas,'Courier New',monospace;
            display:flex;flex-direction:column;
            overflow:hidden;
            position:relative;
          }
          /* 点阵纹理：给深底加一层细腻质感 */
          body::before{
            content:'';
            position:absolute;inset:0;
            background-image:radial-gradient(rgba(255,255,255,.045) 1px, transparent 1px);
            background-size:22px 22px;
            pointer-events:none;
          }
          /* 氛围光斑：紫 / 青 / 蓝三团柔光，缓慢漂移 */
          .glow{position:absolute;border-radius:50%;pointer-events:none;}
          .g1{width:280px;height:280px;left:-70px;top:-60px;
              background:radial-gradient(circle, rgba(124,58,237,.34), transparent 65%);
              animation:drift1 14s ease-in-out infinite alternate;}
          .g2{width:300px;height:300px;right:-90px;top:32%;
              background:radial-gradient(circle, rgba(34,211,238,.26), transparent 65%);
              animation:drift2 17s ease-in-out infinite alternate;}
          .g3{width:320px;height:320px;left:12%;bottom:-110px;
              background:radial-gradient(circle, rgba(59,130,246,.28), transparent 65%);
              animation:drift1 20s ease-in-out infinite alternate-reverse;}
          @keyframes drift1{
            from{transform:translate(0,0) scale(1)}
            to{transform:translate(46px,34px) scale(1.12)}
          }
          @keyframes drift2{
            from{transform:translate(0,0) scale(1.06)}
            to{transform:translate(-40px,-30px) scale(1)}
          }
          /* 主区域：玻璃卡片 + 固定 + 动态 + 光标，整体略下移；行内禁止换行，光标永远跟在文字后 */
          .main{
            flex:1;
            display:flex;align-items:center;justify-content:center;
            position:relative;
            padding-top:5vh;
          }
          .card{
            background:rgba(255,255,255,.045);
            border:1px solid rgba(255,255,255,.09);
            border-radius:20px;
            padding:20px 26px;
            box-shadow:0 12px 40px rgba(0,0,0,.35);
          }
          .line{
            display:flex;align-items:baseline;
            font-size:32px;line-height:1.6;
            white-space:pre;
            flex-wrap:nowrap;
          }
          .fixed{color:#fff;font-weight:700;}
          .dynwrap{display:inline-flex;align-items:baseline;white-space:pre;}
          .dyn{
            background:linear-gradient(90deg,#34d399,#22d3ee,#a78bfa,#34d399);
            background-size:300% 100%;
            -webkit-background-clip:text;
            background-clip:text;
            -webkit-text-fill-color:transparent;
            color:transparent;
            animation:flow 6s linear infinite;
          }
          @keyframes flow{
            0%{background-position:0% 50%}
            100%{background-position:300% 50%}
          }
          .cursor{
            display:inline-block;width:2px;height:1.2em;margin-left:4px;
            background:#fff;vertical-align:text-bottom;
            animation:blink 1s step-end infinite;
          }
          @keyframes blink{0%,100%{opacity:1}50%{opacity:0}}
          /* 欢迎语：扫光文字（一道高光从左到右扫过） */
          .welcome{
            position:absolute;left:20px;top:15%;
            font-size:20px;font-weight:700;
            background:linear-gradient(90deg,
              #64748b 0%, #94a3b8 30%,
              #ffffff 50%,
              #94a3b8 70%, #64748b 100%);
            background-size:220% 100%;
            -webkit-background-clip:text;
            background-clip:text;
            -webkit-text-fill-color:transparent;
            opacity:0;
            animation:rise 1.2s cubic-bezier(0.22,0.61,0.36,1) forwards,
                      sheen 3.2s ease-in-out infinite 1.6s;
            animation-delay:0.3s, 1.6s;
          }
          @keyframes sheen{
            0%{background-position:0% 50%}
            100%{background-position:220% 50%}
          }
          @keyframes rise{
            0%{opacity:0;transform:translateY(60px)}
            100%{opacity:1;transform:translateY(0)}
          }
          /* 厂商 Logo 墙：三行无缝滚动（1/3 行向左，2 行向右，恒速） */
          .logos{
            display:flex;flex-direction:column;gap:10px;
            overflow:hidden;
            padding:6px 0 12px 0;
          }
          .row{overflow:hidden;width:100%;}
          .track{
            display:inline-flex;align-items:center;gap:16px;
            width:max-content;will-change:transform;
          }
          .chip{
            background:rgba(244,248,255,.94);
            border:1px solid rgba(255,255,255,.55);
            border-radius:12px;
            padding:8px 18px;
            display:flex;align-items:center;
            flex:0 0 auto;
            box-shadow:0 4px 14px rgba(0,0,0,.32);
          }
          .chip img{height:22px;display:block;}
          @keyframes marqL{from{transform:translateX(0)}to{transform:translateX(-50%)}}
          @keyframes marqR{from{transform:translateX(-50%)}to{transform:translateX(0)}}
          /* 底部立即体验按钮：光晕 + 持续喷射粒子 */
          .bottom{padding:18px 0 30px;text-align:center;position:relative;}
          .btn{
            display:inline-block;
            position:relative;
            background:linear-gradient(90deg,#34d399,#22d3ee);
            color:#111827;font-weight:700;font-size:18px;
            padding:14px 64px;border-radius:999px;
            text-decoration:none;
            box-shadow:0 10px 30px rgba(52,211,153,.30);
            transition:transform .15s ease;
          }
          #fx{position:absolute;left:0;right:0;bottom:0;width:100%;height:120px;pointer-events:none;z-index:0;}
          .btn:active{transform:scale(.96)}
        </style>
        </head>
        <body>
          <div class="glow g1"></div>
          <div class="glow g2"></div>
          <div class="glow g3"></div>

          <div class="main">
            <div class="card">
              <div class="line">
                <span class="fixed" id="fixed"></span><span class="dynwrap"><span class="dyn" id="dyn"></span><span class="cursor"></span></span>
              </div>
            </div>
          </div>

          <div class="welcome">欢迎使用QingYangAI!👋</div>

          <div class="logos">
            <div class="row"><div class="track" id="row0"></div></div>
            <div class="row"><div class="track" id="row1"></div></div>
            <div class="row"><div class="track" id="row2"></div></div>
          </div>

          <div class="bottom">
            <canvas id="fx"></canvas>
            <a class="btn" href="qingyang://next">立即体验</a>
          </div>

        <script>
        var LOGOS = __LOGOS__;

        (function(){
          // 固定「与AI一起」+ 动态词循环打字
          var fixedEl = document.getElementById('fixed');
          var dynEl = document.getElementById('dyn');
          var FIXED_WORD = '与AI一起';
          var TYPE_MS = 110, DEL_MS = 55, HOLD_MS = 1400, SWITCH_MS = 400, ROUND_END_MS = 2000;
          var WORDS = ['逻辑推理', '方案构思', '代码开发', 'lin', 'QingYangAI Agent'];
          var sleep = function(ms){ return new Promise(function(r){ setTimeout(r, ms); }); };

          async function typeInto(el, text){
            for(var i=0;i<=text.length;i++){
              el.textContent = text.slice(0,i);
              await sleep(TYPE_MS);
            }
          }
          async function deleteFrom(el){
            var cur = el.textContent;
            for(var i=cur.length-1;i>=0;i--){
              el.textContent = cur.slice(0,i);
              await sleep(DEL_MS);
            }
          }
          async function deleteDynOnly(){
            await deleteFrom(dynEl);
            await sleep(SWITCH_MS);
          }
          async function deleteAll(){
            await deleteFrom(dynEl);
            await deleteFrom(fixedEl);
            await sleep(SWITCH_MS);
          }

          async function run(){
            try{
              await document.fonts.load('16px MapleMono');
              await document.fonts.ready;
            }catch(e){}
            await sleep(300);
            while(true){
              await typeInto(fixedEl, FIXED_WORD);
              for(var i=0;i<3;i++){
                await typeInto(dynEl, WORDS[i]);
                await sleep(HOLD_MS);
                await deleteDynOnly();
              }
              await typeInto(dynEl, WORDS[3]);
              await sleep(HOLD_MS);
              await deleteAll();
              await typeInto(dynEl, WORDS[4]);
              await sleep(HOLD_MS);
              await deleteAll();
              await sleep(ROUND_END_MS);
            }
          }
          run();

          // ── 厂商 Logo 墙：三行取模分组，1/3 行向左、2 行向右，恒速 40px/s 无缝循环 ──
          var groups = [[],[],[]];
          LOGOS.forEach(function(l, i){ groups[i % 3].push(l); });
          var dirs = ['marqL', 'marqR', 'marqL'];

          groups.forEach(function(g, i){
            var track = document.getElementById('row' + i);
            if (!track) return;
            if (!g.length) { track.parentNode.style.display = 'none'; return; }

            var chipHtml = g.map(function(l){
              return '<div class="chip"><img src="' + l + '" onerror="this.parentNode.style.display=\'none\'"></div>';
            }).join('');
            track.innerHTML = chipHtml;

            // 无缝循环：只按「整组」翻倍，保证 translateX(-50%) 平移回到起点时左右两半严格相等——
            // 不按 guard/scrollWidth 猜边界，避免中间行滚到边缘对不齐闪跳、以及"快慢不一"。
            // 先按整组复制到至少两倍视口宽，再整体翻倍成两份相同内容。
            var copies = 1;
            track.innerHTML = chipHtml;
            while (track.scrollWidth < window.innerWidth * 2 && copies < 30) {
              track.innerHTML += chipHtml;
              copies++;
            }
            var halfHtml = track.innerHTML;
            track.innerHTML = halfHtml + halfHtml;

            // 恒定 40px/s：动画时长 = (单份宽度)/40，内容半份 = 单份，速度严格一致
            var secs = (track.scrollWidth / 2) / 40;
            track.style.animation = dirs[i] + ' ' + secs.toFixed(1) + 's linear infinite';
          });
        })();
          // ── 持续粒子：从按钮边缘向上喷射星空粒子，EaseOutQuad（二次方先快后慢）──
          // 不用乘系数阻尼；用粒子生命归一 t ∈ [0,1]，速度比例 = QuadraticEaseOut(t)=1-(1-t)^2
          (function(){
            var fx = document.getElementById('fx');
            var btn = document.querySelector('.btn');
            if (!fx || !btn || !fx.getContext) return;
            var ctx = fx.getContext('2d');
            var W, H, dpr = Math.min(window.devicePixelRatio || 1, 2);
            var parts = [];

            function resize(){
              var r = btn.getBoundingClientRect();
              fx.style.left = (r.left - 40) + 'px';
              fx.style.width = (r.width + 80) + 'px';
              W = fx.width = (r.width + 80) * dpr;
              H = fx.height = 120 * dpr;
              fx.style.height = '120px';
              ctx.setTransform(dpr,0,0,dpr,0,0);
            }
            window.addEventListener('resize', resize);
            resize();

            var COLORS = ['#a7f3d0','#a5f3fc','#c4b5fd','#fff','#fde68a'];
            var MAXLIFE = 1.6; // 秒

            function spawn(){
              // 从按钮上缘沿线随机横向位置发射
              var x = Math.random() * (W / dpr);
              var y = (H / dpr) - 2;
              parts.push({
                x: x, y: y,
                vx: (Math.random() * 40 - 20),       // 轻微横向漂移
                vy0: 70 + Math.random() * 110,        // 初始速度（刚发射最大）
                size: 1 + Math.random() * 2.2,
                color: COLORS[(Math.random() * COLORS.length) | 0],
                t: 0, life: MAXLIFE * (0.6 + Math.random() * 0.8)
              });
              if (parts.length > 200) parts.shift();
            }

            var last = performance.now();
            function frame(now){
              var dt = Math.min(0.05, (now - last) / 1000); last = now;
              // 持续生成：约每秒 90 颗
              var spawnN = Math.floor(90 * dt);
              for (var i = 0; i < spawnN; i++) spawn();
              if (Math.random() < 90 * dt - spawnN) spawn();

              ctx.clearRect(0, 0, W / dpr, H / dpr);
              for (var i = parts.length - 1; i >= 0; i--){
                var p = parts[i];
                p.t += dt;
                if (p.t >= p.life){ parts.splice(i,1); continue; }

                var t = Math.min(1, p.t / p.life);   // 生命归一化 [0,1]
                // EaseOutQuad：速度比例 = 1-(1-t)^2；先快后慢，无阻尼系数
                var ease = 1 - (1 - t) * (1 - t);

                p.x += p.vx * dt;
                p.y -= p.vy0 * ease * dt;             // 垂直速度随 ease 衰减（先快后慢）

                // 透明度：前段保持，末段渐隐销毁
                var alpha = t < 0.7 ? 1 : (1 - (t - 0.7) / 0.3);
                ctx.globalAlpha = Math.max(0, alpha);
                ctx.fillStyle = p.color;
                ctx.beginPath();
                ctx.arc(p.x, p.y, p.size, 0, Math.PI * 2);
                ctx.fill();
              }
              ctx.globalAlpha = 1;
              requestAnimationFrame(frame);
            }
            requestAnimationFrame(frame);
          })();
        </script>
        </body>
        </html>
        """;
        return html.Replace("__LOGOS__", System.Text.Json.JsonSerializer.Serialize(logos));
    }
}
