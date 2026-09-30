<div align="center">

# 青阳AI

**一个真的能"看见"你手机状态的 Android AI 伴侣**

不是又一个套壳聊天框。Ta 会主动查你现在在用什么 App、手机还剩多少电、昨天屏幕用了多久，
然后据此跟你说话 —— 而不是等你问。

[![Platform](https://img.shields.io/badge/platform-Android%2024%2B-3DDC84?logo=android&logoColor=white)](#)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](#)
[![MAUI](https://img.shields.io/badge/MAUI-net10.0--android-5C2D91)](#)
[![License](https://img.shields.io/badge/license-MIT-blue)](#)

</div>

---

## 这是什么

青阳AI 是一个 **.NET MAUI 写的原生 Android 应用**，把大模型接成一个住在你手机里的 AI 伙伴。

和市面上大多数"AI 聊天 App"最大的区别只有一个：

> **大部分 AI 伴侣是纯嘴炮 —— 你说"我好累"，它回"抱抱你"。**
> **青阳AI 是真的知道你在干嘛：它能看到你现在开着微信、电量只剩 12%、今天屏幕已用 8 小时。**

这个能力来自内置的 **9 个感知 API**，Ta 会在需要的时候主动调用，而不是靠你一句句汇报。

---

## 核心能力

### 👁 感知系统 —— 真正"看得见"

Ta 有一套隐藏指令 `{api:"名"}`，想查什么自己查，客户端执行后把真实结果回传给模型：

| API | 能知道什么 |
|---|---|
| `screen_time` | 今天各 App 用了多久（判断你睡没睡好、是不是刷太久了） |
| `foreground` | 你此刻正在用哪个 App |
| `notifications` | 最近收到的通知（微信等） |
| `calendar` | 今天的日程安排 |
| `battery` | 电量和充电状态 |
| `music` | 是否在放歌、铃声模式 |
| `alarm` | 下一个闹钟（结合时间判断你该不该起床） |
| `screen` | 屏幕亮着还是灭了 |
| `network` | 网络状态 |

> **这些全部走 Android 系统标准 API，不需要 Root，也不需要 Shizuku。**
> 未授权时返回说明文字而不是崩溃。

### 🤖 Agent 循环执行

不是"问一句答一句"。开启 Agent 模式后，Ta 会：

```
收到目标 → 思考 → 执行动作 → 拿到结果 → 再思考 → 继续 …… 直到任务完成
```

自带**卡死检测**（同一动作重复 3 次就停下并如实告知），不会陷入无限循环。

可执行的动作包括：运行命令（Shizuku）、读写文件、打开网页、浏览网页、下载、生图、读图。

### 🎨 文生图（三种后端 + 实时预览）

统一入口，自动探测后端类型：

- **OpenAI 兼容** —— 走 Responses API 的 `partial_images`，支持中间帧
- **ComfyUI** —— websocket 收预览帧（`PREVIEW_IMAGE` 事件）
- **SD-WebUI / Forge** —— 轮询 `/sdapi/v1/progress`

支持在聊天图框里**实时显示生成进度百分比和中间预览图**；后端不支持时明确标注"当前模型不支持实时预览"，不假装。

还带一个**关水印开关集合** —— 各家服务商字段名不一样（`watermark` / `add_watermark` / `no_watermark`…），做成下拉可选 + 自定义 JSON。

### 💬 富交互对话

- **打字机效果** —— 流式逐字吐出，不是一块块蹦
- **心情 / 头像 / 语音** —— `{mood:}` 改表情、`{avatar:}` 换头像、TTS 语音回复
- **记忆系统** —— 长期记忆落库，可查可删
- **日记 / 内在生活** —— Ta 有自己的"生活线"
- **主动关心** —— 后台定时心跳，配安静守则（免打扰时段 + 每日上限），不会骚扰你

### 🔐 隐私

- **API Key 只存本机**（`Preferences`），不上传任何服务器
- 项目本身**没有任何中转服务器**，你的对话直连你自己填的 API 地址
- 感知数据**只在本地读取、用完即走**，不落盘、不外传
- 感知能力**可随时在设置里关掉**

---

## 安装

### 直接装 APK（推荐）

1. 去 [Releases](../../releases) 下载最新 `青阳AI-x.x.xx.apk`
2. 手机上允许"安装未知来源应用"
3. 装完打开，跟着引导走两步：**① 填 API 地址和 Key ② 选人设**

> 需要 **Android 7.0 (API 24) 及以上**。

### 你需要准备什么

青阳AI **不自带模型**，你需要自己有一个 OpenAI 兼容的 API：

| 你需要的 | 说明 |
|---|---|
| **API 地址** | 形如 `https://xxx.com/v1/chat/completions` |
| **API Key** | 你自己在服务商那里申请的 |
| **模型名** | 填完地址和 Key 后点「获取模型列表」自动拉取 |

支持任何 **OpenAI 兼容**接口（官方、各种中转、本地部署的 Ollama / vLLM 等都可以）。
文生图 / 语音 / 视觉可以各自单独配一个地址和 Key。

### 从源码构建

```bash
# 需要 .NET 10 SDK + Android SDK + JDK 21
dotnet build-server shutdown
dotnet publish -f net10.0-android -c Release \
  -p:AndroidSdkDirectory="<你的 Android SDK 路径>"
```

产物在 `bin/Release/net10.0-android/com.qingyang.ai-Signed.apk`。

---

## 隐藏指令协议

Ta 回复里的这些特殊标记**不会显示在聊天框**，只做副作用：

| 指令 | 作用 |
|---|---|
| `{api:"screen_time"}` | 调感知 API 拿真实数据 |
| `{img:"一只在窗台晒太阳的猫"}` | 生成图片 |
| `{cmd:"ls /sdcard"}` | 执行 shell 命令（需 Shizuku） |
| `{read:"路径"}` / `{write:"路径"}` / `{edit:"路径"}` | 文件操作 |
| `{browse:"网址"}` / `{web:"关键词"}` | 打开网页 / 联网搜索 |
| `{download:"网址"}` | 下载文件 |
| `{mood:"开心"}` / `{avatar:"..."}` / `{say:"..."}` | 心情 / 头像 / 语音 |
| `{todo:"..."}` / `{plan:"..."}` / `{done:"..."}` | 任务清单 |

---

## 常见问题

**Q：为什么它说"我没法知道你的手机状态"？**
A：某些感知 API 需要系统权限（如「用法访问」需在系统设置里手动授予「有权访问使用情况」）。去 设置 → Shizuku / 感知权限 里逐个打开。

**Q：需要 Root 吗？**
A：**不需要**。感知功能走系统标准 API。只有 `{cmd:}` 执行 shell 命令需要 [Shizuku](https://shizuku.rikka.app/)（也不需要 Root，用 ADB 授权即可）。

**Q：我的 Key 安全吗？**
A：只存在你手机本地，代码里没有任何上报逻辑，也没有中转服务器。

**Q：为什么不做审核/过滤？**
A：青阳AI 用的是**你自己申请的 API**，内容由你选的服务商负责。本项目不额外加一层过滤，但这不代表可以拿它做违法的事 —— 请遵守你所在地法律和服务商条款。

---

## 项目状态

活跃开发中。当前版本 **1.9.27**，46 个源文件 + 11 个 Android 平台实现。

功能在持续迭代，遇到问题欢迎开 Issue。

## 许可

MIT License —— 随便用、随便改，商用也行。

---

<div align="center">

**如果这个项目让你觉得有点意思，给个 ⭐ 就是最大的鼓励。**

</div>
