using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace HomeBackgroundMod
{
    /// <summary>
    /// Android 后台线程 MediaCodec (YUV420_888) + DirectByteBuffer 零拷贝 + GPU Shader (CriMana/SofdecPrimeYuv) 视频背景。
    /// 纯反射访问 UnityEngine.AndroidJNIModule，保证单个 DLL 同时兼容 Windows 与 Android。
    /// </summary>
    internal sealed class AndroidVideoBackground
    {
        private unsafe delegate sbyte* GetDirectBufferAddressFn(IntPtr buf);

        private static bool _jniResolved;
        private static Type _ajcType;
        private static Type _ajoType;
        private static Type _jniType;
        private static MethodInfo _callStaticObjMethod;
        private static MethodInfo _callVoidMethod;
        private static MethodInfo _callGenericMethod;
        private static MethodInfo _getRawObjectMethod;
        private static GetDirectBufferAddressFn _getDirectBufferAddress;

        private object _decoder;
        private object _slot0Obj;
        private object _slot1Obj;
        private readonly IntPtr[] _slotPtrs = new IntPtr[2];
        private bool _useDirectPtr;

        private enum ColorFixMode
        {
            DirectSrgbRt,
            DisableSrgbWrite,
            CopyLinearRtToSrgbTex
        }

        private static readonly string[] FallbackLinearKeywords =
        {
            "CRI_LINEAR_COLORSPACE",
            "CRI_OUTPUT_LINEAR",
            "CRI_LINEAR",
            "LINEAR",
            "_LINEAR",
            "GAMMA_TO_LINEAR",
            "SRGB_TO_LINEAR",
            "UNITY_COLORSPACE_LINEAR"
        };

        private Texture2D _texY;
        private Texture2D _texU;
        private Texture2D _texV;
        private RenderTexture _rt;
        private Texture2D _displayTex;
        private Material _yuvMat;
        private ColorFixMode _colorMode = ColorFixMode.DirectSrgbRt;

        private byte[] _yScratch;
        private byte[] _uScratch;
        private byte[] _vScratch;

        private int _width;
        private int _height;
        private int _ySize;
        private int _uvSize;
        private int _uOffset;
        private int _vOffset;

        private bool _hasFrame;
        private bool _paused;
        private string _error = "";
        private string _colorDiag = "";

        public Texture Texture { get { return _displayTex != null ? (Texture)_displayTex : _rt; } }
        public bool IsPlaying { get { return _decoder != null && _rt != null; } }
        public bool IsPrepared { get { return _decoder != null; } }
        public bool HasFrame { get { return _hasFrame; } }
        public bool CanCheckFrame { get { return true; } }
        public string Error { get { return _error; } }
        public string ColorDiag { get { return _colorDiag; } }

        public bool TryStart(string path, int maxWidth, int maxHeight, out string error)
        {
            error = null;
            if (IsPlaying) return true;
            if (!File.Exists(path))
            {
                error = "找不到视频: " + path;
                return false;
            }
            if (string.IsNullOrEmpty(AndroidVideoDex.Base64))
            {
                error = "内置 Android 解码器 DEX 为空";
                return false;
            }

            Shader yuvShader = Shader.Find("CriMana/SofdecPrimeYuv");
            if (yuvShader == null || !yuvShader.isSupported)
            {
                error = "CriMana/SofdecPrimeYuv 着色器不可用";
                return false;
            }

            if (!EnsureJniResolved(out error))
            {
                return false;
            }

            object parent = null;
            object buffer = null;
            object loader = null;
            object clazz = null;
            object byteBufferCls = null;
            object classLoaderCls = null;
            object javaClassCls = null;

            try
            {
                byte[] dex = Convert.FromBase64String(AndroidVideoDex.Base64);
                classLoaderCls = NewAjc("java.lang.ClassLoader");
                parent = CallStaticObj(classLoaderCls, "getSystemClassLoader");

                byteBufferCls = NewAjc("java.nio.ByteBuffer");
                buffer = CallStaticObj(byteBufferCls, "wrap", (object)dex);

                loader = NewAjo("dalvik.system.InMemoryDexClassLoader", buffer, parent);
                javaClassCls = NewAjc("java.lang.Class");
                clazz = CallStaticObj(javaClassCls, "forName", "homebg.VideoDecoder", true, loader);

                _decoder = CallObj(clazz, "newInstance");
                bool ok = Call<bool>(_decoder, "start", path, Math.Max(2, maxWidth), Math.Max(2, maxHeight));
                if (!ok)
                {
                    string jErr = Call<string>(_decoder, "getError");
                    error = "VideoDecoder.start 失败: " + (jErr ?? "");
                    _error = error;
                    Stop();
                    return false;
                }

                _width = Call<int>(_decoder, "getWidth");
                _height = Call<int>(_decoder, "getHeight");
                _ySize = Call<int>(_decoder, "getYSize");
                _uvSize = Call<int>(_decoder, "getUVSize");
                _uOffset = Call<int>(_decoder, "getUOffset");
                _vOffset = Call<int>(_decoder, "getVOffset");

                if (_width < 2 || _height < 2 || _ySize <= 0 || _uvSize <= 0)
                {
                    error = "视频尺寸无效: " + _width + "x" + _height;
                    _error = error;
                    Stop();
                    return false;
                }

                _useDirectPtr = false;
                if (_getDirectBufferAddress != null)
                {
                    try
                    {
                        _slot0Obj = CallObj(_decoder, "getSlotBuffer", 0);
                        _slot1Obj = CallObj(_decoder, "getSlotBuffer", 1);
                        if (_slot0Obj != null && _slot1Obj != null)
                        {
                            IntPtr raw0 = (IntPtr)_getRawObjectMethod.Invoke(_slot0Obj, null);
                            IntPtr raw1 = (IntPtr)_getRawObjectMethod.Invoke(_slot1Obj, null);
                            unsafe
                            {
                                _slotPtrs[0] = raw0 != IntPtr.Zero ? new IntPtr(_getDirectBufferAddress(raw0)) : IntPtr.Zero;
                                _slotPtrs[1] = raw1 != IntPtr.Zero ? new IntPtr(_getDirectBufferAddress(raw1)) : IntPtr.Zero;
                            }
                            _useDirectPtr = _slotPtrs[0] != IntPtr.Zero && _slotPtrs[1] != IntPtr.Zero;
                        }
                    }
                    catch
                    {
                        _useDirectPtr = false;
                    }
                }

                if (!_useDirectPtr)
                {
                    _yScratch = new byte[_ySize];
                    _uScratch = new byte[_uvSize];
                    _vScratch = new byte[_uvSize];
                }

                // Probe 3 已验证：CriMana/SofdecPrimeYuv 采样 Alpha8 通道，且用 _MovieTexture_ST (1, -1, 0, 1) GPU 垂直翻转
                // 显式指定 linear=true 避免任何 sRGB 采样转换干扰 Y/U/V 原始数值
                _texY = new Texture2D(_width, _height, TextureFormat.Alpha8, false, true);
                _texY.wrapMode = TextureWrapMode.Clamp;
                _texY.filterMode = FilterMode.Bilinear;
                _texY.name = "HomeBG_Y";

                _texU = new Texture2D(_width / 2, _height / 2, TextureFormat.Alpha8, false, true);
                _texU.wrapMode = TextureWrapMode.Clamp;
                _texU.filterMode = FilterMode.Bilinear;
                _texU.name = "HomeBG_U";

                _texV = new Texture2D(_width / 2, _height / 2, TextureFormat.Alpha8, false, true);
                _texV.wrapMode = TextureWrapMode.Clamp;
                _texV.filterMode = FilterMode.Bilinear;
                _texV.name = "HomeBG_V";

                _yuvMat = new Material(yuvShader);
                SetYuvTextures(_texY, _texU, _texV);
                _yuvMat.SetTexture("_TextureA", Texture2D.whiteTexture);
                _yuvMat.SetVector("_MovieTexture_ST", new Vector4(1f, -1f, 0f, 1f));
                _yuvMat.SetVector("_MovieChromaTexture_ST", new Vector4(1f, -1f, 0f, 1f));
                _yuvMat.SetVector("_MovieAlphaTexture_ST", new Vector4(1f, 1f, 0f, 0f));
                _yuvMat.SetFloat("_Side", 0f);
                _yuvMat.SetFloat("_SideTransparent", 0f);
                _yuvMat.SetFloat("_SrcBlendMode", 1f);
                _yuvMat.SetFloat("_DstBlendMode", 0f);
                _yuvMat.SetFloat("_CullMode", 0f);
                _yuvMat.SetFloat("_ZWriteMode", 0f);
                _yuvMat.SetVector("_ClipBox", new Vector4(-1e6f, -1e6f, 1e6f, 1e6f));

                _colorMode = CalibrateColorSpace(yuvShader);

                RenderTextureReadWrite rtRw = (_colorMode == ColorFixMode.CopyLinearRtToSrgbTex)
                    ? RenderTextureReadWrite.Linear
                    : RenderTextureReadWrite.sRGB;
                _rt = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGB32, rtRw);
                _rt.wrapMode = TextureWrapMode.Clamp;
                _rt.filterMode = FilterMode.Bilinear;
                _rt.name = "HomeBackgroundModAndroidRT";
                _rt.Create();

                if (_colorMode == ColorFixMode.CopyLinearRtToSrgbTex)
                {
                    _displayTex = new Texture2D(_width, _height, TextureFormat.RGBA32, false, false);
                    _displayTex.wrapMode = TextureWrapMode.Clamp;
                    _displayTex.filterMode = FilterMode.Bilinear;
                    _displayTex.name = "HomeBackgroundModAndroidDisplayTex";
                    _displayTex.Apply(false, true);
                }

                _hasFrame = false;
                _paused = false;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                _error = e.ToString();
                Stop();
                return false;
            }
            finally
            {
                DisposeQuietly(clazz);
                DisposeQuietly(loader);
                DisposeQuietly(buffer);
                DisposeQuietly(parent);
                DisposeQuietly(javaClassCls);
                DisposeQuietly(byteBufferCls);
                DisposeQuietly(classLoaderCls);
            }
        }

        public void SetPaused(bool paused)
        {
            if (_decoder == null || _paused == paused) return;
            _paused = paused;
            try { CallVoid(_decoder, "setPaused", paused); }
            catch { }
        }

        public void Blit()
        {
            if (_decoder == null || _rt == null || _yuvMat == null) return;
            int slot = -1;
            try
            {
                slot = Call<int>(_decoder, "acquireReadySlot");
                if (slot < 0 || slot > 1) return;

                if (_useDirectPtr)
                {
                    IntPtr basePtr = _slotPtrs[slot];
                    if (basePtr == IntPtr.Zero) return;
                    _texY.LoadRawTextureData(basePtr, _ySize);
                    _texU.LoadRawTextureData(IntPtr.Add(basePtr, _uOffset), _uvSize);
                    _texV.LoadRawTextureData(IntPtr.Add(basePtr, _vOffset), _uvSize);
                }
                else
                {
                    byte[] packed = Call<byte[]>(_decoder, "copySlotBytes", slot);
                    if (packed == null || packed.Length < _vOffset + _uvSize) return;
                    Buffer.BlockCopy(packed, 0, _yScratch, 0, _ySize);
                    Buffer.BlockCopy(packed, _uOffset, _uScratch, 0, _uvSize);
                    Buffer.BlockCopy(packed, _vOffset, _vScratch, 0, _uvSize);
                    _texY.LoadRawTextureData(_yScratch);
                    _texU.LoadRawTextureData(_uScratch);
                    _texV.LoadRawTextureData(_vScratch);
                }

                _texY.Apply(false, false);
                _texU.Apply(false, false);
                _texV.Apply(false, false);

                RenderTexture prev = RenderTexture.active;
                if (_colorMode == ColorFixMode.DisableSrgbWrite)
                {
                    bool prevSrgb = GL.sRGBWrite;
                    GL.sRGBWrite = false;
                    try
                    {
                        Graphics.Blit(_texY, _rt, _yuvMat);
                    }
                    finally
                    {
                        GL.sRGBWrite = prevSrgb;
                        RenderTexture.active = prev;
                    }
                }
                else
                {
                    try
                    {
                        Graphics.Blit(_texY, _rt, _yuvMat);
                        if (_colorMode == ColorFixMode.CopyLinearRtToSrgbTex && _displayTex != null)
                        {
                            Graphics.CopyTexture(_rt, _displayTex);
                        }
                    }
                    finally
                    {
                        RenderTexture.active = prev;
                    }
                }

                _hasFrame = true;
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
            finally
            {
                if (slot >= 0)
                {
                    try { CallVoid(_decoder, "releaseSlot", slot); } catch { }
                }
            }
        }

        public void Stop()
        {
            _hasFrame = false;
            _paused = false;

            if (_decoder != null)
            {
                try { CallVoid(_decoder, "stop"); } catch { }
                DisposeQuietly(_decoder);
                _decoder = null;
            }

            DisposeQuietly(_slot0Obj); _slot0Obj = null;
            DisposeQuietly(_slot1Obj); _slot1Obj = null;
            _slotPtrs[0] = IntPtr.Zero;
            _slotPtrs[1] = IntPtr.Zero;
            _useDirectPtr = false;

            _yScratch = null;
            _uScratch = null;
            _vScratch = null;

            DestroyUnityObject(_yuvMat); _yuvMat = null;
            DestroyUnityObject(_texY); _texY = null;
            DestroyUnityObject(_texU); _texU = null;
            DestroyUnityObject(_texV); _texV = null;
            DestroyUnityObject(_displayTex); _displayTex = null;
            DestroyUnityObject(_rt); _rt = null;
            _colorMode = ColorFixMode.DirectSrgbRt;
            _colorDiag = "";
        }

        private void SetYuvTextures(Texture yTex, Texture uTex, Texture vTex)
        {
            _yuvMat.SetTexture("_MainTex", yTex);
            _yuvMat.SetTexture("_TextureY", yTex);
            _yuvMat.SetTexture("_TextureU", uTex);
            _yuvMat.SetTexture("_TextureV", vTex);
        }

        /// <summary>
        /// 在 Linear 色彩空间下用 4x4 中灰（Y=U=V=128）自校准输出管线，避免 sRGB 输出写入 sRGB RT 导致画面发白。
        /// </summary>
        private ColorFixMode CalibrateColorSpace(Shader yuvShader)
        {
            ColorSpace activeCs = QualitySettings.activeColorSpace;
            if (activeCs != ColorSpace.Linear)
            {
                _colorDiag = "cs=" + activeCs + ", mode=DirectSrgbRt";
                return ColorFixMode.DirectSrgbRt;
            }

            Texture2D tY = null;
            Texture2D tU = null;
            Texture2D tV = null;
            Texture2D readTex = null;
            Texture2D testSrgbTex = null;
            RenderTexture checkLinearRt = null;
            RenderTexture testSrgbRt = null;
            RenderTexture testLinearSrcRt = null;
            RenderTexture prevActive = RenderTexture.active;

            try
            {
                tY = CreateGrayAlpha8x4();
                tU = CreateGrayAlpha8x4();
                tV = CreateGrayAlpha8x4();
                SetYuvTextures(tY, tU, tV);

                checkLinearRt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                checkLinearRt.Create();
                readTex = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);

                Graphics.Blit(tY, checkLinearRt, _yuvMat);
                float rawOut = ReadCenterR(checkLinearRt, readTex);
                if (rawOut <= 0.35f)
                {
                    _colorDiag = string.Format(CultureInfo.InvariantCulture, "cs=Linear, raw={0:F3}, mode=DirectSrgbRt(alreadyLinear)", rawOut);
                    return ColorFixMode.DirectSrgbRt;
                }

                List<string> keywords = CollectCandidateKeywords(yuvShader);
                for (int i = 0; i < keywords.Count; i++)
                {
                    string kw = keywords[i];
                    _yuvMat.EnableKeyword(kw);
                    Graphics.Blit(tY, checkLinearRt, _yuvMat);
                    float kwOut = ReadCenterR(checkLinearRt, readTex);
                    if (kwOut >= 0.14f && kwOut <= 0.32f)
                    {
                        _colorDiag = string.Format(CultureInfo.InvariantCulture, "cs=Linear, raw={0:F3}, kw={1}, out={2:F3}, mode=DirectSrgbRt(keyword)", rawOut, kw, kwOut);
                        return ColorFixMode.DirectSrgbRt;
                    }
                    _yuvMat.DisableKeyword(kw);
                }

                float srgbWriteOut = -1f;
                try
                {
                    testSrgbRt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    testSrgbRt.Create();
                    bool prevSrgb = GL.sRGBWrite;
                    GL.sRGBWrite = false;
                    try
                    {
                        Graphics.Blit(tY, testSrgbRt, _yuvMat);
                    }
                    finally
                    {
                        GL.sRGBWrite = prevSrgb;
                    }
                    Graphics.Blit(testSrgbRt, checkLinearRt);
                    srgbWriteOut = ReadCenterR(checkLinearRt, readTex);
                    if (srgbWriteOut >= 0.12f && srgbWriteOut <= 0.35f)
                    {
                        _colorDiag = string.Format(CultureInfo.InvariantCulture, "cs=Linear, raw={0:F3}, srgbWriteOff={1:F3}, mode=DisableSrgbWrite", rawOut, srgbWriteOut);
                        return ColorFixMode.DisableSrgbWrite;
                    }
                }
                catch { }

                float copyTexOut = -1f;
                try
                {
                    testLinearSrcRt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    testLinearSrcRt.Create();
                    Graphics.Blit(tY, testLinearSrcRt, _yuvMat);

                    testSrgbTex = new Texture2D(4, 4, TextureFormat.RGBA32, false, false);
                    testSrgbTex.Apply(false, true);
                    Graphics.CopyTexture(testLinearSrcRt, testSrgbTex);
                    Graphics.Blit(testSrgbTex, checkLinearRt);
                    copyTexOut = ReadCenterR(checkLinearRt, readTex);
                    if (copyTexOut >= 0.12f && copyTexOut <= 0.35f)
                    {
                        _colorDiag = string.Format(CultureInfo.InvariantCulture, "cs=Linear, raw={0:F3}, srgbWriteOff={1:F3}, copyTex={2:F3}, mode=CopyLinearRtToSrgbTex", rawOut, srgbWriteOut, copyTexOut);
                        return ColorFixMode.CopyLinearRtToSrgbTex;
                    }
                }
                catch { }

                _colorDiag = string.Format(CultureInfo.InvariantCulture, "cs=Linear, raw={0:F3}, srgbWriteOff={1:F3}, copyTex={2:F3}, mode=DirectSrgbRt(fallback)", rawOut, srgbWriteOut, copyTexOut);
                return ColorFixMode.DirectSrgbRt;
            }
            catch (Exception e)
            {
                _colorDiag = "cs=" + activeCs + ", err=" + e.Message + ", mode=DirectSrgbRt(errFallback)";
                return ColorFixMode.DirectSrgbRt;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (_yuvMat != null && _texY != null && _texU != null && _texV != null)
                {
                    SetYuvTextures(_texY, _texU, _texV);
                }
                DestroyUnityObject(testSrgbTex);
                DestroyUnityObject(testSrgbRt);
                DestroyUnityObject(testLinearSrcRt);
                DestroyUnityObject(checkLinearRt);
                DestroyUnityObject(readTex);
                DestroyUnityObject(tY);
                DestroyUnityObject(tU);
                DestroyUnityObject(tV);
            }
        }

        private static Texture2D CreateGrayAlpha8x4()
        {
            Texture2D tex = new Texture2D(4, 4, TextureFormat.Alpha8, false, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Point;
            byte[] data = new byte[16];
            for (int i = 0; i < data.Length; i++) data[i] = 128;
            tex.LoadRawTextureData(data);
            tex.Apply(false, false);
            return tex;
        }

        private static float ReadCenterR(RenderTexture rt, Texture2D readTex)
        {
            RenderTexture.active = rt;
            readTex.ReadPixels(new Rect(0, 0, 4, 4), 0, 0, false);
            readTex.Apply(false, false);
            return readTex.GetPixel(2, 2).r;
        }

        private static List<string> CollectCandidateKeywords(Shader shader)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (shader != null)
                {
                    PropertyInfo spaceProp = typeof(Shader).GetProperty("keywordSpace", BindingFlags.Instance | BindingFlags.Public);
                    object space = spaceProp?.GetValue(shader, null);
                    if (space != null)
                    {
                        PropertyInfo kwsProp = space.GetType().GetProperty("keywords", BindingFlags.Instance | BindingFlags.Public);
                        Array kws = kwsProp?.GetValue(space, null) as Array;
                        if (kws != null)
                        {
                            PropertyInfo nameProp = null;
                            for (int i = 0; i < kws.Length; i++)
                            {
                                object kwObj = kws.GetValue(i);
                                if (kwObj == null) continue;
                                if (nameProp == null)
                                {
                                    nameProp = kwObj.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public);
                                }
                                string name = nameProp?.GetValue(kwObj, null) as string;
                                if (!string.IsNullOrEmpty(name) && seen.Add(name))
                                {
                                    result.Add(name);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            for (int i = 0; i < FallbackLinearKeywords.Length; i++)
            {
                string kw = FallbackLinearKeywords[i];
                if (seen.Add(kw)) result.Add(kw);
            }
            return result;
        }

        // ------------------------------------------------------------------
        private static bool EnsureJniResolved(out string error)
        {
            error = null;
            if (_jniResolved && _ajcType != null && _ajoType != null) return true;

            _ajcType = FindType("UnityEngine.AndroidJavaClass", "UnityEngine.AndroidJNIModule");
            _ajoType = FindType("UnityEngine.AndroidJavaObject", "UnityEngine.AndroidJNIModule");
            _jniType = FindType("UnityEngine.AndroidJNI", "UnityEngine.AndroidJNIModule");
            if (_ajcType == null || _ajoType == null)
            {
                error = "UnityEngine.AndroidJNIModule 不可用";
                return false;
            }

            _callStaticObjMethod = FindCallMethod(_ajcType, "CallStatic", isGeneric: true)?.MakeGenericMethod(_ajoType);
            _callVoidMethod = FindCallMethod(_ajoType, "Call", isGeneric: false);
            _callGenericMethod = FindCallMethod(_ajoType, "Call", isGeneric: true);
            _getRawObjectMethod = _ajoType.GetMethod("GetRawObject", Type.EmptyTypes);

            if (_callStaticObjMethod == null || _callVoidMethod == null || _callGenericMethod == null)
            {
                error = "AndroidJavaObject 反射方法解析失败";
                return false;
            }

            if (_jniType != null && _getRawObjectMethod != null)
            {
                try
                {
                    MethodInfo gdba = _jniType.GetMethod("GetDirectBufferAddress", new[] { typeof(IntPtr) });
                    if (gdba != null)
                    {
                        _getDirectBufferAddress = (GetDirectBufferAddressFn)Delegate.CreateDelegate(
                            typeof(GetDirectBufferAddressFn), gdba);
                    }
                }
                catch
                {
                    _getDirectBufferAddress = null;
                }
            }

            _jniResolved = true;
            return true;
        }

        private static MethodInfo FindCallMethod(Type type, string name, bool isGeneric)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.Name != name || m.IsGenericMethodDefinition != isGeneric) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(object[]))
                    {
                        return m;
                    }
                }
            }
            return null;
        }

        private static object NewAjc(string className)
        {
            return Activator.CreateInstance(_ajcType, new object[] { className });
        }

        private static object NewAjo(string className, params object[] args)
        {
            return Activator.CreateInstance(_ajoType, new object[] { className, args });
        }

        private static object CallStaticObj(object ajc, string method, params object[] args)
        {
            return _callStaticObjMethod.Invoke(ajc, new object[] { method, args });
        }

        private static object CallObj(object target, string method, params object[] args)
        {
            MethodInfo m = _callGenericMethod.MakeGenericMethod(_ajoType);
            return m.Invoke(target, new object[] { method, args });
        }

        private static T Call<T>(object target, string method, params object[] args)
        {
            MethodInfo m = _callGenericMethod.MakeGenericMethod(typeof(T));
            return (T)m.Invoke(target, new object[] { method, args });
        }

        private static void CallVoid(object target, string method, params object[] args)
        {
            _callVoidMethod.Invoke(target, new object[] { method, args });
        }

        private static void DisposeQuietly(object obj)
        {
            IDisposable d = obj as IDisposable;
            if (d != null)
            {
                try { d.Dispose(); } catch { }
            }
        }

        private static void DestroyUnityObject(UnityEngine.Object obj)
        {
            if (obj != null)
            {
                try { UnityEngine.Object.Destroy(obj); } catch { }
            }
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
                try
                {
                    Type t = assemblies[i].GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
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
