using System;
using System.Reflection;
using UnityEngine;

namespace HomeBackgroundMod
{
    /// <summary>
    /// 用 Unity 的 VideoPlayer 播放用户放的 mp4，渲染到 RenderTexture，交给 FairyGUI 的 GLoader 显示。
    /// 游戏本身只用 CRI Mana(USM)，VideoPlayer 通过反射拿，避免编译期依赖 UnityEngine.VideoModule。
    /// </summary>
    internal sealed class VideoBackground
    {
        private GameObject _gameObject;
        private object _player;
        private RenderTexture _renderTexture;
        private Type _playerType;
        private bool _started;

        public RenderTexture Texture { get { return _renderTexture; } }
        public bool IsPlaying { get { return _player != null && _renderTexture != null; } }

        /// <summary>
        /// Prepare 是异步的；Android 上 Prepare 后立刻 Play 会丢帧，PC 也统一等就绪再 Play。
        /// </summary>
        public bool IsPrepared
        {
            get
            {
                try
                {
                    if (_player == null || _playerType == null) return false;
                    PropertyInfo p = _playerType.GetProperty("isPrepared");
                    return p != null && Convert.ToBoolean(p.GetValue(_player, null));
                }
                catch { return false; }
            }
        }

        public bool TryStart(string path, int width, int height, out string error)
        {
            error = null;
            if (IsPlaying) return true;
            if (!System.IO.File.Exists(path))
            {
                error = "找不到视频: " + path;
                return false;
            }

            _playerType = FindType("UnityEngine.Video.VideoPlayer", "UnityEngine.VideoModule");
            if (_playerType == null)
            {
                error = "VideoPlayer 类型不可用（UnityEngine.VideoModule 未加载）";
                return false;
            }

            try
            {
                _gameObject = new GameObject("HomeBackgroundModVideo");
                UnityEngine.Object.DontDestroyOnLoad(_gameObject);
                _player = _gameObject.AddComponent(_playerType);

                _renderTexture = new RenderTexture(Math.Max(2, width), Math.Max(2, height), 0, RenderTextureFormat.ARGB32);
                _renderTexture.name = "HomeBackgroundModVideoRT";

                Set("url", new Uri(path).AbsoluteUri);
                SetEnum("renderMode", "RenderTexture");
                Set("targetTexture", _renderTexture);
                Set("isLooping", true);
                Set("playOnAwake", false);
                TrySetEnum("audioOutputMode", "None");

                Invoke("Prepare");
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                Stop();
                return false;
            }
        }

        public void Play()
        {
            try
            {
                if (_player == null || _started) return;
                _started = true;
                Invoke("Play");
            }
            catch { }
        }

        public void Stop()
        {
            try
            {
                if (_player != null) Invoke("Stop");
            }
            catch { }

            try
            {
                if (_gameObject != null) UnityEngine.Object.Destroy(_gameObject);
            }
            catch { }

            try
            {
                if (_renderTexture != null) UnityEngine.Object.Destroy(_renderTexture);
            }
            catch { }

            _gameObject = null;
            _player = null;
            _renderTexture = null;
            _started = false;
        }

        // ------------------------------------------------------------------
        private void Set(string name, object value)
        {
            if (_playerType == null || _player == null) return;
            PropertyInfo property = _playerType.GetProperty(name);
            if (property != null && property.CanWrite)
            {
                property.SetValue(_player, value, null);
                return;
            }
            FieldInfo field = _playerType.GetField(name);
            if (field != null) field.SetValue(_player, value);
        }

        private void SetEnum(string name, string enumValue)
        {
            if (_playerType == null) return;
            PropertyInfo property = _playerType.GetProperty(name);
            if (property == null || !property.CanWrite) return;
            object value = Enum.Parse(property.PropertyType, enumValue, true);
            property.SetValue(_player, value, null);
        }

        private void TrySetEnum(string name, string enumValue)
        {
            try { SetEnum(name, enumValue); } catch { }
        }

        private void Invoke(string method)
        {
            if (_playerType == null || _player == null) return;
            MethodInfo info = _playerType.GetMethod(method, Type.EmptyTypes);
            if (info != null) info.Invoke(_player, null);
        }

        private static Type FindType(string fullName, string assemblyName)
        {
            try
            {
                Type direct = Type.GetType(fullName + ", " + assemblyName);
                if (direct != null) return direct;
            }
            catch { }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = null;
                try { type = assemblies[i].GetType(fullName); } catch { }
                if (type != null) return type;
            }

            try
            {
                Assembly assembly = Assembly.Load(assemblyName);
                if (assembly != null) return assembly.GetType(fullName);
            }
            catch { }

            return null;
        }
    }
}
