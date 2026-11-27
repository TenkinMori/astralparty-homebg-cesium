using System;
using System.IO;
using CesiumLoader.SDK;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using FairyGUI;
using Tools;
using UI;
using UnityEngine;

namespace HomeBackgroundMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new HomeBackgroundController());
        }
    }

    /// <summary>
    /// 把主页（UIHomePanel.loader_bg）的背景换成自定义视频或图片。
    ///
    /// 单 DLL 跨平台：
    /// - Windows：通过反射调用 Unity VideoPlayer 播放 mp4/webm
    /// - Android：通过后台 MediaCodec (YUV420_888) + DirectByteBuffer + GPU CriMana/SofdecPrimeYuv Shader 播放
    /// - 无视频时统一回退到 8-bit 非隔行 PNG
    /// </summary>
    public sealed class HomeBackgroundController : ModBase
    {
        private const double PollIntervalSeconds = 0.5;
        private const string DefaultImage = "assets/bg.png";
        private const string DefaultVideo = "bg.mp4";
        private const string ScaleModeFill = "fill";
        private const string ScaleModeFit = "fit";
        private const string ScaleModeTile = "tile";
        private const string ScaleModeStretch = "stretch";

        private bool _enabled = true;
        private bool _hideThemeVideo = true;
        private string _imageRelative = DefaultImage;
        private string _videoRelative = DefaultVideo;
        private string _scaleMode = ScaleModeFill;
        private bool _forceVideo;

        private Texture2D _texture;
        private NTexture _nTexture;
        private GGraph _backdrop;
        private float _backdropW;
        private float _backdropH;
        private GLoader _overlay;
        private string _loadedPath;

        private VideoBackground _video;
        private AndroidVideoBackground _androidVideo;
        private NTexture _videoTexture;
        private bool _videoTried;
        private bool _videoFailed;
        private float _videoStartTime;
        private string _activeVideoPath;

        private double _nextPoll;
        private double _nextWarn;
        private string _lastWarn;
        private bool _loggedApply;
        private bool _loggedHideVideo;

        public override string Name { get { return "自定义主页背景"; } }
        public override string Version { get { return "1.2.3"; } }
        public override string Author { get { return "TenkinMori"; } }
        public override string Description
        {
            get { return "把游戏主页背景换成自定义视频或图片（bg.mp4 / assets/bg.png）。"; }
        }

        private static bool IsAndroid
        {
            get { return Application.platform == RuntimePlatform.Android; }
        }

        public override void OnInitialize()
        {
            ReadConfig(writeDefaults: true);
            Log.Info("配置: enable=" + _enabled + ", scaleMode=" + _scaleMode + ", image=" + _imageRelative + ", video=" + _videoRelative);
            if (_enabled && ResolveVideoPath() == null) Preload();
        }

        public override void OnConfigChanged()
        {
            ReadConfig(writeDefaults: false);
            DisposeTexture();
            _loggedApply = false;
            _loggedHideVideo = false;
            _nextPoll = 0;
            Log.Info("配置已更新: enable=" + _enabled + ", scaleMode=" + _scaleMode + ", image=" + _imageRelative + ", video=" + _videoRelative);
            if (_enabled && ResolveVideoPath() == null) Preload();
        }

        public override void OnUpdate()
        {
            if (!_enabled) return;

            bool onHome = IsOnHomePanel();
            if (IsAndroid && _androidVideo != null && _androidVideo.IsPlaying)
            {
                _androidVideo.SetPaused(!onHome);
                if (onHome)
                {
                    bool hadFrame = _androidVideo.HasFrame;
                    _androidVideo.Blit();
                    if (!hadFrame && _androidVideo.HasFrame && _videoTexture == null)
                    {
                        _nextPoll = 0;
                    }
                }
            }

            double now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
            if (now < _nextPoll) return;
            _nextPoll = now + PollIntervalSeconds;

            if (!onHome) return;

            try { Apply(); }
            catch (Exception e) { Warn("应用失败: " + e.Message); }
        }

        public override void OnUnload()
        {
            DisposeTexture();
        }

        // ------------------------------------------------------------------
        private static bool IsOnHomePanel()
        {
            try
            {
                UIManager manager = SimpleSingletonProvider<UIManager>.inst;
                if (manager == null) return false;
                IBasePanel current = manager.currentPanel;
                return current != null && current.config != null && current.config.PanelType == UIPanelType.Home;
            }
            catch
            {
                return false;
            }
        }

        private void ReadConfig(bool writeDefaults)
        {
            ModConfig cfg = Config;
            if (cfg == null) return;

            if (writeDefaults && cfg.Count == 0)
            {
                cfg.Set("enable", true);
                cfg.Set("hideThemeVideo", true);
                cfg.Set("video", DefaultVideo);
                cfg.Set("image", DefaultImage);
                cfg.Set("scaleMode", ScaleModeFill);
                cfg.SaveIfDirty();
            }

            _enabled = cfg.GetBool("enable", true);
            _forceVideo = cfg.GetBool("forceVideo", false);
            _hideThemeVideo = cfg.GetBool("hideThemeVideo", true);
            string video = cfg.GetString("video", DefaultVideo);
            _videoRelative = string.IsNullOrEmpty(video) ? DefaultVideo : video;
            string image = cfg.GetString("image", DefaultImage);
            _imageRelative = string.IsNullOrEmpty(image) ? DefaultImage : image;
            _scaleMode = NormalizeScaleMode(cfg.GetString("scaleMode", ScaleModeFill));
        }

        private static string NormalizeScaleMode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return ScaleModeFill;
            switch (raw.Trim().ToLowerInvariant())
            {
                case ScaleModeFit: return ScaleModeFit;
                case ScaleModeTile: return ScaleModeTile;
                case ScaleModeStretch: return ScaleModeStretch;
                default: return ScaleModeFill;
            }
        }

        private void Apply()
        {
            UIManager manager = SimpleSingletonProvider<UIManager>.inst;
            if (manager == null) return;

            IBasePanel current = manager.currentPanel;
            if (current == null || current.config == null) return;
            if (current.config.PanelType != UIPanelType.Home) return;

            HomePanel home = current as HomePanel;
            if (home == null) return;

            UIHomePanel ui = home.UIObject;
            if (ui == null || ui.loader_bg == null) return;

            NTexture source = null;
            if (EnsureVideo()) source = _videoTexture;

            if (source == null)
            {
                if (_texture == null && !LoadTexture()) return;
                if (_nTexture == null) _nTexture = new NTexture(_texture);
                source = _nTexture;
            }

            // 先压掉「当期主题背景」视频，避免它盖住背景
            SuppressThemeVideo(ui);

            ApplyOverlay(ui, source);
        }

        /// <summary>优先用模组目录下的 bg.mp4；没有视频就回退到 PNG。</summary>
        private bool EnsureVideo()
        {
            return IsAndroid ? EnsureAndroidVideo() : EnsureUnityVideo();
        }

        private bool EnsureAndroidVideo()
        {
            if (_androidVideo == null) _androidVideo = new AndroidVideoBackground();
            if (_androidVideo.IsPlaying)
            {
                if (_androidVideo.HasFrame)
                {
                    if (_videoTexture == null) _videoTexture = new NTexture(_androidVideo.Texture);
                    return true;
                }
                return false;
            }
            if (_videoTried || _videoFailed) return false;

            _videoTried = true;
            string path = ResolveVideoPath();
            if (path == null) return false;

            int vw = Screen.width;
            int vh = Screen.height;
            try
            {
                if (GRoot.inst != null && GRoot.inst.width > 0 && GRoot.inst.height > 0)
                {
                    vw = (int)GRoot.inst.width;
                    vh = (int)GRoot.inst.height;
                }
            }
            catch { }

            string error;
            if (!_androidVideo.TryStart(path, vw, vh, out error))
            {
                string detail = string.IsNullOrEmpty(_androidVideo.Error) ? error : _androidVideo.Error;
                Warn("Android 视频启动失败: " + detail);
                _videoFailed = true;
                return false;
            }

            _activeVideoPath = path;
            if (!string.IsNullOrEmpty(_androidVideo.ColorDiag))
            {
                Log.Info("Android 视频色彩校准: " + _androidVideo.ColorDiag);
            }
            Log.Info("已启动 Android 视频背景: " + path);
            return _androidVideo.HasFrame;
        }

        private bool EnsureUnityVideo()
        {
            if (_video == null) _video = new VideoBackground();
            if (_video.IsPlaying)
            {
                if (_video.IsPrepared) _video.Play();
                if (_video.HasReceivedFrame)
                {
                    if (_videoTexture == null) _videoTexture = new NTexture(_video.Texture);
                    return true;
                }
                if (_videoStartTime > 0f && Time.unscaledTime - _videoStartTime > 4f)
                {
                    if (!_video.CanCheckFrame)
                    {
                        if (_forceVideo)
                        {
                            Warn("VideoPlayer 没有 frame 属性，forceVideo=true，使用视频纹理");
                            if (_videoTexture == null) _videoTexture = new NTexture(_video.Texture);
                            return true;
                        }
                        Warn("VideoPlayer 没有 frame 属性，默认回退图片（config: forceVideo=true 可强制视频）");
                        _video.Stop();
                        _videoFailed = true;
                        return false;
                    }
                    Warn("视频 4 秒内没有画面，回退到图片");
                    _video.Stop();
                    _videoFailed = true;
                }
                return false;
            }
            if (_videoTried || _videoFailed) return false;

            _videoTried = true;
            _videoStartTime = Time.unscaledTime;
            string path = ResolveVideoPath();
            if (path == null) return false;

            string error;
            if (!_video.TryStart(path, (int)GRoot.inst.width, (int)GRoot.inst.height, out error))
            {
                Warn("视频背景启动失败: " + error);
                _videoFailed = true;
                return false;
            }

            _activeVideoPath = path;
            _videoTexture = new NTexture(_video.Texture);
            Log.Info("已启动视频背景: " + path);
            return true;
        }

        private string ResolveVideoPath()
        {
            string root = SdkPath.CurrentModDirectory;
            string rel = _videoRelative.Replace('/', Path.DirectorySeparatorChar);
            string[] candidates =
            {
                Path.Combine(root, rel),
                Path.Combine(root, "assets", rel),
                Path.Combine(root, DefaultVideo)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i])) return candidates[i];
            }
            return null;
        }

        /// <summary>
        /// 在 Com_BGSet 之上插一个不接收点击的 GLoader，直接铺我们自己的图/视频。
        /// 比改 loader_bg 稳：游戏重新设 url / 换主题时不会把它顶掉。
        /// </summary>
        private void ApplyOverlay(UIHomePanel ui, NTexture source)
        {
            Rect fullRect = ComputeFullScreenRect(ui);
            int baseIndex = DesiredOverlayIndex(ui);

            if (_backdrop == null || _backdrop.parent != ui)
            {
                _backdrop = new GGraph();
                _backdrop.name = "HomeBackgroundModBackdrop";
                _backdrop.touchable = false;
                ui.AddChildAt(_backdrop, baseIndex);
                _backdropW = 0f;
                _backdropH = 0f;
            }
            else if (ui.GetChildIndex(_backdrop) < baseIndex)
            {
                ui.SetChildIndex(_backdrop, baseIndex);
            }

            _backdrop.SetPosition(fullRect.x, fullRect.y, 0f);
            if (Math.Abs(_backdropW - fullRect.width) > 0.5f || Math.Abs(_backdropH - fullRect.height) > 0.5f)
            {
                _backdrop.SetSize(fullRect.width, fullRect.height);
                _backdrop.DrawRect(fullRect.width, fullRect.height, 0, Color.black, Color.black);
                _backdropW = fullRect.width;
                _backdropH = fullRect.height;
            }

            int overlayIndex = Math.Min(ui.GetChildIndex(_backdrop) + 1, ui.numChildren);
            if (_overlay == null || _overlay.parent != ui)
            {
                _overlay = new GLoader();
                _overlay.name = "HomeBackgroundModOverlay";
                _overlay.touchable = false;
                ui.AddChildAt(_overlay, overlayIndex);

                Log.Info("overlay 插入 index=" + overlayIndex + "/" + ui.numChildren
                    + " loader_bg=" + IndexOf(ui, ui.loader_bg)
                    + " Com_BGSet=" + IndexOf(ui, ui.Com_BGSet)
                    + " rect=" + (int)fullRect.x + "," + (int)fullRect.y + " " + (int)fullRect.width + "x" + (int)fullRect.height);
            }
            else if (ui.GetChildIndex(_overlay) <= ui.GetChildIndex(_backdrop))
            {
                ui.SetChildIndex(_overlay, overlayIndex);
            }

            if (source != null && source.nativeTexture != null)
            {
                TextureWrapMode wantWrap = (_scaleMode == ScaleModeTile) ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                if (source.nativeTexture.wrapMode != wantWrap)
                {
                    source.nativeTexture.wrapMode = wantWrap;
                }
            }

            ApplyScaleMode(_overlay, _scaleMode);
            _overlay.SetSize(fullRect.width, fullRect.height);
            _overlay.SetPosition(fullRect.x, fullRect.y, 0f);

            if (!ReferenceEquals(_overlay.texture, source))
            {
                _overlay.texture = source;
                if (!_loggedApply)
                {
                    _loggedApply = true;
                    Log.Info("已替换主页背景(overlay, " + _scaleMode + "): " + (_activeVideoPath ?? _loadedPath ?? ""));
                }
            }

            if (_overlay.image != null)
            {
                bool wantTile = _scaleMode == ScaleModeTile;
                if (_overlay.image.scaleByTile != wantTile)
                {
                    _overlay.image.scaleByTile = wantTile;
                    if (wantTile) _overlay.image.textureScale = Vector2.one;
                    _overlay.InvalidateBatchingState();
                }
            }
        }

        private static Rect ComputeFullScreenRect(UIHomePanel ui)
        {
            try
            {
                if (GRoot.inst != null)
                {
                    Rect r = GRoot.inst.TransformRect(new Rect(0f, 0f, GRoot.inst.width, GRoot.inst.height), ui);
                    if (r.width > 1f && r.height > 1f) return r;
                    return new Rect(0f, 0f, GRoot.inst.width, GRoot.inst.height);
                }
            }
            catch { }
            return new Rect(0f, 0f, Screen.width, Screen.height);
        }

        private static void ApplyScaleMode(GLoader loader, string scaleMode)
        {
            switch (scaleMode)
            {
                case ScaleModeFit:
                    loader.fill = FillType.Scale;
                    loader.align = AlignType.Center;
                    loader.verticalAlign = VertAlignType.Middle;
                    break;
                case ScaleModeTile:
                    loader.fill = FillType.ScaleFree;
                    loader.align = AlignType.Left;
                    loader.verticalAlign = VertAlignType.Top;
                    break;
                case ScaleModeStretch:
                    loader.fill = FillType.ScaleFree;
                    loader.align = AlignType.Center;
                    loader.verticalAlign = VertAlignType.Middle;
                    break;
                default:
                    loader.fill = FillType.ScaleNoBorder;
                    loader.align = AlignType.Center;
                    loader.verticalAlign = VertAlignType.Middle;
                    break;
            }
        }

        /// <summary>插到 loader_bg 和 Com_BGSet 之上，但在其余按钮/文字之下。</summary>
        private static int DesiredOverlayIndex(UIHomePanel ui)
        {
            int index = 0;
            if (ui.loader_bg != null && ui.loader_bg.parent == ui) index = Math.Max(index, ui.GetChildIndex(ui.loader_bg) + 1);
            if (ui.Com_BGSet != null && ui.Com_BGSet.parent == ui) index = Math.Max(index, ui.GetChildIndex(ui.Com_BGSet) + 1);
            if (index > ui.numChildren) index = ui.numChildren;
            return index;
        }

        private static string IndexOf(UIHomePanel ui, GObject obj)
        {
            if (obj == null || obj.parent != ui) return "-";
            return ui.GetChildIndex(obj).ToString();
        }

        /// <summary>
        /// 主页的「当期主题背景」是 CRI 视频，渲染在 Com_BGSet.loader_Video(GGraph) 上，
        /// 会盖住 loader_bg。这里停掉并隐藏它，让自定义背景露出来。
        /// </summary>
        private void SuppressThemeVideo(UIHomePanel ui)
        {
            if (!_hideThemeVideo) return;
            UIHome_Com_BGSet bgSet = ui.Com_BGSet;
            if (bgSet == null || bgSet.loader_Video == null) return;

            GGraph video = bgSet.loader_Video;
            if (!video.visible) return;

            video.visible = false;
            try { SimpleSingletonProvider<CriMovieManager>.inst.StopAndDestroy(video); } catch { }

            if (!_loggedHideVideo)
            {
                _loggedHideVideo = true;
                Log.Info("已隐藏主题背景视频（Com_BGSet.loader_Video）");
            }
        }

        private void Preload()
        {
            if (!LoadTexture()) return;
            Log.Info("已加载背景图 " + _texture.width + "x" + _texture.height + ": " + _loadedPath);
        }

        private bool LoadTexture()
        {
            string root = SdkPath.CurrentModDirectory;
            string rel = _imageRelative.Replace('/', Path.DirectorySeparatorChar);
            string path = Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel);

            if (!File.Exists(path))
            {
                Warn("找不到图片: " + path);
                return false;
            }

            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception e) { Warn("读取图片失败: " + e.Message); return false; }

            int width, height;
            byte[] rgba;
            if (!PngDecoder.TryDecode(data, out width, out height, out rgba))
            {
                Warn("PNG 解码失败（仅支持 8-bit 非隔行 PNG）");
                return false;
            }

            for (int y = 0; y < height / 2; y++)
            {
                int a = y * width * 4;
                int b = (height - 1 - y) * width * 4;
                for (int i = 0; i < width * 4; i++)
                {
                    byte t = rgba[a + i];
                    rgba[a + i] = rgba[b + i];
                    rgba[b + i] = t;
                }
            }

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "HomeBackgroundMod";
            tex.LoadRawTextureData(rgba);
            tex.Apply(false, false);

            _texture = tex;
            _loadedPath = path;
            return true;
        }

        private void DisposeTexture()
        {
            if (_overlay != null)
            {
                try
                {
                    _overlay.RemoveFromParent();
                    _overlay.Dispose();
                }
                catch { }
                _overlay = null;
            }
            if (_backdrop != null)
            {
                try
                {
                    _backdrop.RemoveFromParent();
                    _backdrop.Dispose();
                }
                catch { }
                _backdrop = null;
                _backdropW = 0f;
                _backdropH = 0f;
            }

            if (_video != null)
            {
                _video.Stop();
                _video = null;
            }
            if (_androidVideo != null)
            {
                _androidVideo.Stop();
                _androidVideo = null;
            }
            if (_videoTexture != null)
            {
                try { _videoTexture.Dispose(); } catch { }
                _videoTexture = null;
            }

            _videoTried = false;
            _videoFailed = false;
            _videoStartTime = 0f;
            _activeVideoPath = null;

            if (_nTexture != null)
            {
                try { _nTexture.Dispose(); } catch { }
                _nTexture = null;
            }
            if (_texture != null)
            {
                try { UnityEngine.Object.Destroy(_texture); } catch { }
                _texture = null;
            }
            _loadedPath = null;
        }

        private void Warn(string message)
        {
            double now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
            if (message == _lastWarn && now < _nextWarn) return;
            _lastWarn = message;
            _nextWarn = now + 30;
            Log.Warn(message);
        }
    }
}
