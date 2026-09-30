# -*- coding: utf-8 -*-
"""
千万别点 —— 一行一句，循环播放。

运行方式：
  - 双击同目录的「千万别点.bat」（推荐）
  - 或命令行执行：python 千万别点.py

操作：
  空格 / 鼠标左键  =  重播
  Esc              =  退出
"""

import tkinter as tk
from tkinter import font as tkfont

# ---------------------------------------------------------------- 文本内容
RAW = """
我用数字omg，我每句话omg
三百回合omg，五花八门omg
七上八下omg，四十五度omg
六亲不认omg，四通八达omg
三天两头omg，初一十五omg
二三得六omg，三三得九omg
三心二意omg，十有八九omg
五一劳动omg，八月十五omg
零八后夜omg，二月初十omg
三峡两岸omg，大年三十omg
五湖四海omg，六神无主omg
七个矮人omg，十八罗汉omg
百万大军omg，七大奇迹omg
太极八卦omg，千年神龟omg
四六不懂omg，举一反三omg
四两千斤omg，五个傻乐omg
周六歇班omg，八月旅游omg
正月初一omg，二十年前omg
十八年后omg，水陆两岸omg
四国军棋omg，二比二平omg
"""

# 按「，」拆成单句，一句一行
PHRASES = [
    p.strip()
    for line in RAW.strip().splitlines()
    for p in line.split("，")
    if p.strip()
]

# ---------------------------------------------------------------- 外观参数
BG      = "#faf8f4"   # 窗口底色
CARD    = "#ffffff"   # 正文卡片
BORDER  = "#ece7de"
TEXT    = "#1c1c1e"   # 正文
ACCENT  = "#d94f2b"   # omg 强调色
MUTED   = "#a8a29a"   # 次要文字

BEAT_MS = 300         # 每句间隔（毫秒）
VISIBLE = 13          # 同屏可见行数
TAIL    = "omg"       # 需要高亮的后缀


def blend(c1, c2, t):
    """把颜色 c1 按比例 t 混向 c2"""
    t = max(0.0, min(1.0, t))
    a = tuple(int(c1[i:i + 2], 16) for i in (1, 3, 5))
    b = tuple(int(c2[i:i + 2], 16) for i in (1, 3, 5))
    return "#%02x%02x%02x" % tuple(
        round(a[i] + (b[i] - a[i]) * t) for i in range(3)
    )


def pick_font(root, candidates, size, weight="normal"):
    families = set(tkfont.families(root))
    for name in candidates:
        if name in families:
            return tkfont.Font(root=root, family=name, size=size, weight=weight)
    return tkfont.Font(root=root, size=size, weight=weight)


class OmgApp:
    def __init__(self, root):
        self.root = root
        self.i = 0
        self.rows = []
        self.job = None

        root.title("千万别点")
        root.configure(bg=BG)
        root.resizable(False, False)

        f_title  = pick_font(root, ["Microsoft YaHei UI", "微软雅黑", "PingFang SC", "Noto Sans CJK SC"], 16, "bold")
        f_sub    = pick_font(root, ["Microsoft YaHei UI", "微软雅黑", "PingFang SC"], 10)
        f_phrase = pick_font(root, ["Microsoft YaHei UI", "微软雅黑", "PingFang SC"], 21)
        f_hint   = pick_font(root, ["Microsoft YaHei UI", "微软雅黑", "PingFang SC"], 9)

        pad = 26

        # ---- 头部
        header = tk.Frame(root, bg=BG)
        header.pack(fill="x", padx=pad, pady=(22, 12))
        tk.Label(header, text="千万别点", font=f_title, bg=BG, fg=TEXT).pack(anchor="w")
        tk.Label(
            header,
            text="点了就得看完 · 共 %d 句" % len(PHRASES),
            font=f_sub, bg=BG, fg=MUTED,
        ).pack(anchor="w", pady=(3, 0))

        # ---- 正文卡片
        card = tk.Frame(root, bg=CARD, highlightbackground=BORDER, highlightthickness=1)
        card.pack(fill="both", expand=True, padx=pad)

        self.txt = tk.Text(
            card, width=28, height=VISIBLE, font=f_phrase,
            bg=CARD, fg=TEXT, relief="flat", bd=0, highlightthickness=0,
            padx=22, pady=16, spacing1=4, spacing2=6, spacing3=4,
            wrap="none", cursor="arrow", takefocus=0,
        )
        self.txt.pack(fill="both", expand=True)
        self.txt.configure(state="disabled")

        # ---- 底部
        footer = tk.Frame(root, bg=BG)
        footer.pack(fill="x", padx=pad, pady=(10, 20))
        tk.Label(
            footer, text="空格 / 点击 重播    ·    Esc 退出",
            font=f_hint, bg=BG, fg=MUTED,
        ).pack(side="left")
        self.counter = tk.Label(footer, text="", font=f_hint, bg=BG, fg=MUTED)
        self.counter.pack(side="right")

        # ---- 事件
        root.bind("<space>", self.restart)
        root.bind("<Escape>", lambda e: root.destroy())
        root.bind("<Button-1>", self.restart)

        # ---- 居中
        root.update_idletasks()
        w, h = root.winfo_reqwidth(), root.winfo_reqheight()
        x = (root.winfo_screenwidth() - w) // 2
        y = max(0, (root.winfo_screenheight() - h) // 2 - 40)
        root.geometry("+%d+%d" % (x, y))

        self.beat()

    # ------------------------------------------------------------ 内部方法
    def _colors(self, level):
        t = level / max(1, VISIBLE - 1)
        return blend(TEXT, BG, min(t * 0.88, 0.88)), blend(ACCENT, BG, min(t * 0.85, 0.85))

    def _paint(self, row):
        c, o = self._colors(row["level"])
        self.txt.tag_configure(row["rtag"], foreground=c)
        self.txt.tag_configure(row["otag"], foreground=o)

    def _set_counter(self):
        self.counter.configure(text="%02d / %02d" % (self.i, len(PHRASES)))

    def beat(self):
        self.job = None
        if self.i >= len(PHRASES):
            self.job = self.root.after(1500, self.restart)
            return

        # 已出现的行整体下移一级（越旧越淡）
        for row in self.rows:
            row["level"] = min(row["level"] + 1, VISIBLE - 1)
            self._paint(row)

        phrase = PHRASES[self.i]
        head = phrase[:-len(TAIL)] if phrase.lower().endswith(TAIL) else phrase
        tail = phrase[len(head):]

        self.txt.configure(state="normal")
        start = self.txt.index("end-1c")
        self.txt.insert("end", head)
        mid = self.txt.index("end-1c")
        self.txt.insert("end", tail)
        stop = self.txt.index("end-1c")
        self.txt.insert("end", "\n")

        rtag, otag = "row%d" % self.i, "omg%d" % self.i
        self.txt.tag_add(rtag, start, stop)
        self.txt.tag_add(otag, mid, stop)
        self.txt.tag_raise(otag)          # omg 优先于整行颜色

        row = {"rtag": rtag, "otag": otag, "level": 0}
        self.rows.append(row)
        self._paint(row)

        self.txt.see("end")
        self.txt.configure(state="disabled")

        self.i += 1
        self._set_counter()
        self.job = self.root.after(BEAT_MS, self.beat)

    def restart(self, event=None):
        if self.job:
            self.root.after_cancel(self.job)
            self.job = None
        self.txt.configure(state="normal")
        self.txt.delete("1.0", "end")
        for row in self.rows:
            self.txt.tag_delete(row["rtag"])
            self.txt.tag_delete(row["otag"])
        self.txt.configure(state="disabled")
        self.rows = []
        self.i = 0
        self._set_counter()
        self.beat()


if __name__ == "__main__":
    root = tk.Tk()
    OmgApp(root)
    root.mainloop()
