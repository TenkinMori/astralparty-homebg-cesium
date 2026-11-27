# 自定义主页背景（CesiumLoader 版）

<p align="left">
  <img src="icon.png" width="96" alt="icon">
</p>

> **游戏：吉星派对（Astral Party）**
> **插件名：自定义主页背景** ｜ **作者：TenkinMori**
>
> CesiumLoader 托管模组：把 `HomeBackgroundMod.dll` 放进
> `AstralParty_ModLoader/mods/HomeBackgroundMod/` 即可。

## 作用

把游戏主页背景换成你自己的 **视频（mp4）** 或 **图片（png）**：

- 优先读取模组目录下的 `bg.mp4`，循环播放
- 没有 `bg.mp4` 时回退到 `assets/bg.png`
- 自动隐藏游戏自带的「当期主题背景」CRI 视频，避免盖住自定义背景

只改本机显示，不修改游戏文件、不改服务端。

## 安装

前提：游戏已安装 CesiumLoader（根目录有 `version.dll` 和 `AstralParty_ModLoader/`）。

1. 关闭游戏
2. 把 `dist/HomeBackgroundMod/` 整个文件夹复制到：

   ```text
   游戏目录/AstralParty_ModLoader/mods/HomeBackgroundMod/
   ├─ HomeBackgroundMod.dll
   ├─ HomeBackgroundMod.json
   ├─ config.json
   ├─ icon.png
   ├─ bg.mp4            ← 你自己的视频（可选）
   └─ assets/bg.png     ← 没有视频时用的静态图
   ```

3. 重启游戏，进主页

## 使用

### 视频背景

把任意带视频轨的 mp4 放到：

```text
...\mods\HomeBackgroundMod\bg.mp4
```

重启游戏，进主页即循环播放。

> 注意：只有音轨、没有视频轨的 mp4 会显示为空，请确认文件里有画面。

### 图片背景

删除/改名 `bg.mp4`，把图片覆盖到：

```text
...\mods\HomeBackgroundMod\assets\bg.png
```

重启游戏。

### 配置 `config.json`

```json
{
  "enable": true,
  "hideThemeVideo": true,
  "video": "bg.mp4",
  "image": "assets/bg.png"
}
```

| 键 | 说明 |
|---|---|
| `enable` | 总开关；`false` 恢复官方主页背景 |
| `hideThemeVideo` | 隐藏游戏自带主题背景视频 |
| `video` | 视频相对路径（默认 `bg.mp4`，也认 `assets/bg.mp4`） |
| `image` | 没有视频时的图片路径 |

保存后模组会热重载配置并重新初始化；不生效就重启游戏。

## 更新日志

### v1.2.1

- 修复 Unity Linear 色彩空间下视频背景写入 `sRGB` `RenderTexture` 导致画面发白的问题（4×4 中灰自校准：自动匹配 Shader 线性 Keyword、`GL.sRGBWrite = false` 或 `Linear RT → Graphics.CopyTexture → sRGB Texture2D`）
- 规范程序集版本号（`1.2.1`）及多平台统一分发目录

### v1.2.0

- 重构 Android 视频背景解码与渲染管线：后台线程 `MediaCodec`（`YUV_420_888`）双槽 `DirectByteBuffer` + 纯反射 JNI `GetDirectBufferAddress` 零拷贝 + 复用游戏内置 `CriMana/SofdecPrimeYuv` GPU 着色器直接渲染，解决主线程卡顿掉帧
- 支持进入对局/离开主页自动暂停解码，返回主页恢复
- Windows 与 Android 统一编译为单个跨平台 `HomeBackgroundMod.dll`

### v1.0.1

- 修复图片背景上下颠倒（PNG 行翻转）
- 视频背景保持原样，不做翻转

### v1.0.0

- 首个版本：视频/图片主页背景

## 原理

1. 游戏主页的「当期主题背景」是 CRI Mana(USM) 视频，渲染在
   `UIHome_Com_BGSet.loader_Video (GGraph)` 上；模组把它停止并隐藏。
2. 用 Unity 的 `VideoPlayer`（通过反射获取，避免编译期依赖 `UnityEngine.VideoModule`）
   播放 `bg.mp4`，渲染到 `RenderTexture`。
3. 在 `HomePanel` 里插一个不接收点击的 `GLoader` 覆盖层，把 `RenderTexture` 铺满屏幕，
   层级位于 `loader_bg` / `Com_BGSet` 之上、按钮之下。
4. 没有视频时用内置 PNG 解码器（`PngDecoder`）读 `assets/bg.png`，
   转成 RGBA32 后交给 `Texture2D.LoadRawTextureData`。
5. `OnUpdate` 低频巡检：主页重新出现、游戏重设背景或重播主题视频时自动补挂。

## 验证

看 `AstralParty_ModLoader/logs/activity-mod-*.log`，出现即生效：

```text
[HomeBackgroundMod] 已启动视频背景: ...\mods\HomeBackgroundMod\bg.mp4
[HomeBackgroundMod] overlay 插入 index=2/22 loader_bg=0 Com_BGSet=1
[HomeBackgroundMod] 已替换主页背景(overlay)
```

## 构建

需要 .NET SDK 8+：

```powershell
pwsh -File build.ps1
```

产物在 `dist/HomeBackgroundMod/`。

- `lib/CesiumLoader.SDK.dll`：CesiumLoader SDK（MIT，随仓库提供以便直接构建）
- `refs/game/*.dll`：游戏热更程序集（2026-11-12 国服版本），只用于编译引用

## 兼容性

- CesiumLoader SDK 3.1.1+（实测 3.7.0 / 4.1.1）
- 游戏 3.3.0（2026-11-12 更新后）
- Windows x64 / Android（MediaCodec 硬件解码 + GPU YUV 着色器）

## 许可

- 本模组代码：MIT（见 `LICENSE`）
- `lib/CesiumLoader.SDK.dll`：CesiumLoader SDK，MIT，版权归其作者
- `refs/game/` 下的游戏程序集版权归游戏官方，仅用于编译
