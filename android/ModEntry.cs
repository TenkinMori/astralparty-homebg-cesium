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
    /// 把主页（UIHomePanel.loader_bg）的背景换成自定义图片。
    ///
    /// 不修改游戏文件：只在内存里给 loader 赋一张运行时加载的 Texture2D。
    /// 游戏在切换主页/重设背景时可能覆盖，所以 OnUpdate 里低频巡检补挂。
    /// </summary>
    public sealed class HomeBackgroundController : ModBase
    {
        private static void Toast(string message)
        {
            try
            {
                AndroidJavaClass activityThread = new AndroidJavaClass("android.app.ActivityThread");
                AndroidJavaObject context = activityThread.CallStatic<AndroidJavaObject>("currentApplication");
                AndroidJavaClass toastClass = new AndroidJavaClass("android.widget.Toast");
                AndroidJavaObject toast = toastClass.CallStatic<AndroidJavaObject>("makeText", context, message, 1);
                toast.Call("show");
            }
            catch { }
        }

        private static void Dbg(string message)
        {
            try
            {
                string dir = CesiumLoader.SDK.SdkPath.CurrentModDirectory;
                if (string.IsNullOrEmpty(dir)) dir = CesiumLoader.SDK.SdkPath.LoaderDirectory;
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "homebg-debug.log"),
                    System.DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + "\n");
            }
            catch { }
        }

        private const double PollIntervalSeconds = 0.5;
        private const string DefaultImage = "assets/bg.png";
        private const string DefaultVideo = "bg.mp4";

        private bool _enabled = true;
        private bool _hideThemeVideo = true;
        private string _imageRelative = DefaultImage;
        private string _videoRelative = DefaultVideo;

        private Texture2D _texture;
        private NTexture _nTexture;
        private GLoader _overlay;
        private string _loadedPath;

        private VideoBackground _video;
        private AndroidVideoBackground _androidVideo;
        private GTextField _debugText;
        private string _videoStatus = "not started";
        private NTexture _videoTexture;
        private bool _videoTried;
        private bool _videoFailed;
        private bool _forceVideo;
        private float _videoStartTime;

        private double _nextPoll;
        private double _nextWarn;
        private string _lastWarn;
        private bool _loggedApply;
        private bool _loggedHideVideo;

        public override string Name { get { return "自定义主页背景"; } }
        public override string Version { get { return "1.1.0"; } }
        public override string Author { get { return "TenkinMori"; } }
        public override string Description
        {
            get { return "把游戏主页背景换成自定义视频或图片（bg.mp4 / assets/bg.png）。"; }
        }

        public override void OnInitialize()
        {
            Dbg("init");
            Debug.Log("[HomeBG] init");
            ReadConfig(writeDefaults: true);
            Log.Info("配置: enable=" + _enabled + ", image=" + _imageRelative);
            Debug.Log("[HomeBG] enabled=" + _enabled + " video=" + _videoRelative + " image=" + _imageRelative);
            if (_enabled && ResolveVideoPath() == null) Preload();
        }

        public override void OnConfigChanged()
        {
            ReadConfig(writeDefaults: false);
            DisposeTexture();
            _loggedApply = false;
            Log.Info("配置已更新: enable=" + _enabled + ", image=" + _imageRelative);
            if (_enabled && ResolveVideoPath() == null) Preload();
        }

        public override void OnUpdate()
        {
            if (!_enabled) return;
            if (_androidVideo != null && _androidVideo.IsPlaying) _androidVideo.Blit();
            double now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
            if (now < _nextPoll) return;
            _nextPoll = now + PollIntervalSeconds;

            try { Apply(); }
            catch (Exception e) { Warn("应用失败: " + e.Message); }
        }

        public override void OnUnload()
        {
            DisposeTexture();
        }

        // ------------------------------------------------------------------
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
                cfg.SaveIfDirty();
            }

            _enabled = cfg.GetBool("enable", true);
            _forceVideo = cfg.GetBool("forceVideo", false);
            _hideThemeVideo = cfg.GetBool("hideThemeVideo", true);
            string video = cfg.GetString("video", DefaultVideo);
            _videoRelative = string.IsNullOrEmpty(video) ? DefaultVideo : video;
            string image = cfg.GetString("image", DefaultImage);
            _imageRelative = string.IsNullOrEmpty(image) ? DefaultImage : image;
        }

        private void Apply()
        {
            UIManager manager = SimpleSingletonProvider<UIManager>.inst;
            if (manager == null) { Dbg("apply manager null"); return; }

            IBasePanel current = manager.currentPanel;
            if (current == null || current.config == null) { Dbg("apply panel null"); return; }
            if (current.config.PanelType != UIPanelType.Home) { Dbg("apply not home: " + current.config.PanelType); return; }

            HomePanel home = current as HomePanel;
            if (home == null) { Dbg("apply cast home null: " + current.GetType().FullName); return; }

            UIHomePanel ui = home.UIObject;
            if (ui == null) { Dbg("apply ui null"); return; }
            if (ui.loader_bg == null) { Dbg("apply loader_bg null"); return; }

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

            Dbg("apply source=" + (source == null ? "null" : source.width + "x" + source.height));
            ApplyOverlay(ui, source);
            Dbg("apply overlay done");
        }

        /// <summary>优先用模组目录下的 bg.mp4；没有视频就回退到 PNG。</summary>
        private bool EnsureVideo()
        {
#if UNITY_ANDROID
            return EnsureAndroidVideo();
#else
            return EnsureUnityVideo();
#endif
        }

        private bool EnsureAndroidVideo()
        {
            if (_androidVideo == null)
            {
                _androidVideo = new AndroidVideoBackground();
                string vp = ResolveVideoPath();
                _videoStatus = "path=" + (vp ?? "null");
            }
            if (_androidVideo.IsPlaying)
            {
                _androidVideo.Blit();
                if (_androidVideo.HasFrame)
                {
                    if (_videoTexture == null) _videoTexture = new NTexture(_androidVideo.Texture);
                    return true;
                }
                return false;
            }
            if (_videoTried || _videoFailed)
            {
                _videoStatus = "skip tried=" + _videoTried + " failed=" + _videoFailed + " tex=" + (_videoTexture != null);
                if (_videoTexture == null && !_androidVideo.IsPlaying) _videoTried = false;
                else return false;
            }
            _videoTried = true;
            string path = ResolveVideoPath();
            if (path == null) return false;
            int vw = Screen.width;
            int vh = Screen.height;
            try { if (GRoot.inst != null) { vw = (int)GRoot.inst.width; vh = (int)GRoot.inst.height; } } catch { }
            string error;
            if (!_androidVideo.TryStart(path, vw, vh, out error))
            {
                string detail = string.IsNullOrEmpty(_androidVideo.Error) ? error : _androidVideo.Error;
                Warn("Android 视频启动失败: " + detail);
                _videoStatus = "fail: " + detail;
                _videoFailed = true;
                return false;
            }
            _androidVideo.Blit();
            _videoTexture = new NTexture(_androidVideo.Texture);
            _videoStatus = "started tex=" + (_androidVideo.Texture != null) + " err=" + (_androidVideo.Error ?? "");
            Log.Info("已启动 Android 视频背景: " + path);
            return true;
        }

        private bool EnsureUnityVideo()
        {
            if (_video == null) _video = new VideoBackground();
            if (_video.IsPlaying)
            {
                // Android 的 Prepare 是异步的，prepare 完成后才能 Play。
                if (_video.IsPrepared) _video.Play();
                if (_video.HasFrame)
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
                return false;
            }

            _videoTexture = new NTexture(_video.Texture);
            Log.Info("已启动视频背景: " + path);
            return true;
        }

        private string ResolveVideoPath()
        {
            string root = SdkPath.CurrentModDirectory;
            string[] candidates =
            {
                Path.Combine(SdkPath.LoaderDirectory, _videoRelative.Replace('/', Path.DirectorySeparatorChar)),
                SdkPath.GetLoaderPath(DefaultVideo),
                Path.Combine(root, _videoRelative.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, "assets", _videoRelative.Replace('/', Path.DirectorySeparatorChar)),
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
        private void UpdateDebugText(UIHomePanel ui)
        {
            try
            {
                if (_debugText == null || _debugText.parent != ui)
                {
                    _debugText = new GTextField();
                    _debugText.name = "HomeBGDebug";
                    _debugText.touchable = false;
                    _debugText.textFormat.size = 18;
                    _debugText.color = Color.white;
                    _debugText.SetSize(1400f, 80f);
                    _debugText.SetPosition(30f, 90f, 0f);
                    ui.AddChild(_debugText);
                }
                string names = "";
                for (int i = 0; i < ui.numChildren && i < 12; i++)
                {
                    var child = ui.GetChildAt(i);
                    names += i + ":" + (child == null ? "null" : child.name) + " ";
                }
                _debugText.text = "HomeBG err=" + (_androidVideo == null ? "null" : _androidVideo.Error) + " | " + _videoStatus
                    + " | playing=" + (_androidVideo != null && _androidVideo.IsPlaying)
                    + " frame=" + (_androidVideo != null && _androidVideo.HasFrame)
                    + " tried=" + _videoTried + " failed=" + _videoFailed
                    + " err=" + (_androidVideo == null ? "null" : _androidVideo.Error)
                    + " | " + names;
                if (ui.GetChildIndex(_debugText) != ui.numChildren - 1)
                    ui.SetChildIndex(_debugText, ui.numChildren - 1);
            }
            catch { }
        }

        private void ApplyOverlay(UIHomePanel ui, NTexture source)
        {
            if (_overlay == null || _overlay.parent != ui)
            {
                _overlay = new GLoader();
                _overlay.name = "HomeBackgroundModOverlay";
                _overlay.fill = FillType.ScaleFree;
                _overlay.touchable = false;

                int index = DesiredOverlayIndex(ui);
                ui.AddChildAt(_overlay, index);

                Log.Info("overlay 插入 index=" + index + "/" + ui.numChildren
                    + " loader_bg=" + IndexOf(ui, ui.loader_bg)
                    + " Com_BGSet=" + IndexOf(ui, ui.Com_BGSet));
            }
            else
            {
                int want = DesiredOverlayIndex(ui);
                if (ui.GetChildIndex(_overlay) != want) ui.SetChildIndex(_overlay, want);
            }

            _overlay.SetSize(GRoot.inst.width, GRoot.inst.height);
            _overlay.SetPosition(0f, 0f, 0f);

            if (!ReferenceEquals(_overlay.texture, source))
            {
                _overlay.texture = source;
                if (!_loggedApply)
                {
                    _loggedApply = true;
                    Log.Info("已替换主页背景(overlay): " + _loadedPath);
                }
            }
        }

        /// <summary>固定在 loader_bg 之上、其余按钮/文字之下。</summary>
        private static int DesiredOverlayIndex(UIHomePanel ui)
        {
            return Math.Min(2, ui.numChildren);
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
            bgSet.visible = false;
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

            if (_video != null)
            {
                _video.Stop();
                _video = null;
            }
            _videoTexture = null;
            _videoTried = false;

            if (_texture != null)
            {
                try { UnityEngine.Object.Destroy(_texture); } catch { }
                _texture = null;
            }
            _nTexture = null;
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

