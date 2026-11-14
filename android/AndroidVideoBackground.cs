using System;
using System.IO;
using UnityEngine;

namespace HomeBackgroundMod
{
    /// <summary>
    /// Android 流式视频背景：MediaCodec + ImageReader，Java 侧转 RGBA，C# 侧更新 Texture2D。
    /// 不依赖 UnityEngine.VideoModule，也不需要 OES shader / Unity Editor。
    /// </summary>
    internal sealed class AndroidVideoBackground
    {
        private const string DexBase64 = "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SVoAA1ZKSQACVkwABlZMSUlJSQAFVkxMTEkAEVZpZGVvRGVjb2Rlci5qYXZhAAFaAAJaTAAEWkxJSQACW0IAB2FkdmFuY2UABWNsYW1wAAVjb2RlYwAMY29sb3ItZm9ybWF0AAtjb2xvckZvcm1hdAAJY29uZmlndXJlAAdjb252ZXJ0AAZjcmVhdGUAE2NyZWF0ZURlY29kZXJCeVR5cGUAEmRlcXVldWVJbnB1dEJ1ZmZlcgATZGVxdWV1ZU91dHB1dEJ1ZmZlcgAFZXJyb3IACWV4dHJhY3RvcgAFZmxhZ3MABWZsdXNoAANnZXQACGdldEVycm9yAAlnZXRIZWlnaHQADmdldElucHV0QnVmZmVyAApnZXRJbnRlZ2VyAA9nZXRPdXRwdXRCdWZmZXIAD2dldE91dHB1dEZvcm1hdAANZ2V0U2FtcGxlVGltZQAJZ2V0U3RyaW5nAA1nZXRUcmFja0NvdW50AA5nZXRUcmFja0Zvcm1hdAAIZ2V0V2lkdGgABmhlaWdodAAEaW5mbwAJaW5wdXREb25lAAZsYXRlc3QABWxpbWl0AANtYXgABG1pbWUAA21pbgAObm8gdmlkZW8gdHJhY2sABm9mZnNldAAIcG9zaXRpb24AEHF1ZXVlSW5wdXRCdWZmZXIADnJlYWRTYW1wbGVEYXRhAAdyZWxlYXNlABNyZWxlYXNlT3V0cHV0QnVmZmVyAAZzZWVrVG8AC3NlbGVjdFRyYWNrAA1zZXREYXRhU291cmNlAARzaXplAAVzdGFydAAKc3RhcnRzV2l0aAAEc3RlcAAEc3RvcAAKdGFrZUxhdGVzdAAIdG9TdHJpbmcABnZpZGVvLwAFd2lkdGgABXdyaXRlAJsBfn5EOHsiYmFja2VuZCI6ImRleCIsImNvbXBpbGF0aW9uLW1vZGUiOiJkZWJ1ZyIsImhhcy1jaGVja3N1bXMiOmZhbHNlLCJtaW4tYXBpIjoxLCJzaGEtMSI6IjA4NGE4OTEyNmFlMDU5NmM1OTkxNDNkZjU3YTY1NDY0NmFmMjgzMTMiLCJ2ZXJzaW9uIjoiOS4yLjQtZGV2In0AAAkFBwMCAQIBAgECAQIBEgECAUIBAhqBgAT4DgEKiA4BAqwPAQnsDQgKvBUeAeAOAQGwDgEByA4BAYwLAQGsEQEBwBQBAdANAAAAAAAOAAAAAAAAAAEAAAAAAAAAAQAAAF8AAABwAAAAAgAAABIAAADsAQAAAwAAAB4AAAA0AgAABAAAAAwAAACcAwAABQAAAC4AAAD8AwAABgAAAAEAAABsBQAAASAAAAwAAACMBQAAAyAAAAwAAABMCwAAARAAAA0AAAAEDAAAAiAAAF8AAACCDAAAACAAAAEAAABDEQAAAxAAAAEAAACMEQAAABAAAAEAAACQEQAA";
        private AndroidJavaObject _decoder;
        private Texture2D _texture;
        private int _width;
        private int _height;
        private bool _hasFrame;
        private string _error = "";

        public Texture2D Texture { get { return _texture; } }
        public bool IsPlaying { get { return _decoder != null && _texture != null; } }
        public bool IsPrepared { get { return _decoder != null; } }
        public bool HasFrame { get { return _hasFrame; } }
        public bool CanCheckFrame { get { return true; } }
        public string Error { get { return _error; } }

        public bool TryStart(string path, int width, int height, out string error)
        {
            error = null;
            if (IsPlaying) return true;
            if (!File.Exists(path))
            {
                error = "找不到视频: " + path;
                return false;
            }

            try
            {
                byte[] dex = Convert.FromBase64String(DexBase64);
                AndroidJavaObject parent = new AndroidJavaClass("java.lang.ClassLoader")
                    .CallStatic<AndroidJavaObject>("getSystemClassLoader");
                AndroidJavaObject buffer = new AndroidJavaClass("java.nio.ByteBuffer")
                    .CallStatic<AndroidJavaObject>("wrap", dex);
                AndroidJavaObject loader = new AndroidJavaObject(
                    "dalvik.system.InMemoryDexClassLoader", buffer, parent);
                AndroidJavaObject clazz = new AndroidJavaClass("java.lang.Class")
                    .CallStatic<AndroidJavaObject>("forName", "homebg.VideoDecoder", true, loader);
                _decoder = clazz.Call<AndroidJavaObject>("newInstance");

                bool ok = _decoder.Call<bool>("start", path, Math.Max(2, width), Math.Max(2, height));
                if (!ok)
                {
                    error = "VideoDecoder.start 失败: " + _decoder.Call<string>("getError");
                    Stop();
                    return false;
                }

                _width = _decoder.Call<int>("getWidth");
                _height = _decoder.Call<int>("getHeight");
                if (_width <= 0 || _height <= 0)
                {
                    error = "视频尺寸无效";
                    Stop();
                    return false;
                }

                _texture = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
                _texture.wrapMode = TextureWrapMode.Clamp;
                _texture.name = "HomeBackgroundModAndroidVideo";
                _texture.Apply(false, false);
                _hasFrame = false;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                _error = e.ToString();
                Stop();
                return false;
            }
        }

        public void Blit()
        {
            if (_decoder == null || _texture == null) return;
            try
            {
                _decoder.Call("step");
                byte[] rgba = _decoder.Call<byte[]>("takeLatest");
                if (rgba == null || rgba.Length != _width * _height * 4) return;

                int stride = _width * 4;
                byte[] flipped = new byte[rgba.Length];
                for (int y = 0; y < _height; y++)
                {
                    Buffer.BlockCopy(rgba, y * stride, flipped, (_height - 1 - y) * stride, stride);
                }
                _texture.LoadRawTextureData(flipped);
                _texture.Apply(false, false);
                _hasFrame = true;
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
        }

        public void Stop()
        {
            _hasFrame = false;
            try { if (_decoder != null) _decoder.Call("stop"); } catch { }
            _decoder = null;
            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }
        }
    }
}
