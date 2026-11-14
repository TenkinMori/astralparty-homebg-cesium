package homebg;

import android.media.MediaCodec;
import android.media.MediaExtractor;
import android.media.MediaFormat;
import android.media.MediaCodecInfo;

import java.nio.ByteBuffer;

/**
 * MediaCodec 解码到 ByteBuffer（不经过 ImageReader/Surface）。
 * 由 Unity 每帧 step()，避免 Java 线程和 Surface 生命周期导致闪退。
 */
public final class VideoDecoder {
    private MediaExtractor extractor;
    private MediaCodec codec;
    private volatile byte[] latest;
    private int width;
    private int height;
    private int colorFormat = -1;
    private String error = "";
    private boolean inputDone;
    private final MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();

    public static VideoDecoder create() { return new VideoDecoder(); }

    public boolean start(String path, int targetWidth, int targetHeight) {
        stop();
        try {
            extractor = new MediaExtractor();
            extractor.setDataSource(path);

            int track = -1;
            MediaFormat format = null;
            for (int i = 0; i < extractor.getTrackCount(); i++) {
                MediaFormat f = extractor.getTrackFormat(i);
                String mime = f.getString(MediaFormat.KEY_MIME);
                if (mime != null && mime.startsWith("video/")) { track = i; format = f; break; }
            }
            if (track < 0 || format == null) { error = "no video track"; return false; }
            extractor.selectTrack(track);

            int srcW = format.getInteger(MediaFormat.KEY_WIDTH);
            int srcH = format.getInteger(MediaFormat.KEY_HEIGHT);
            width = Math.min(targetWidth, srcW);
            height = Math.max(2, (int) ((long) srcH * width / Math.max(1, srcW)));

            String mime = format.getString(MediaFormat.KEY_MIME);
            codec = MediaCodec.createDecoderByType(mime);
            codec.configure(format, null, null, 0);
            codec.start();

            inputDone = false;
            return true;
        } catch (Throwable t) {
            error = t.toString();
            stop();
            return false;
        }
    }

    public void step() {
        if (codec == null) return;
        try {
            if (!inputDone) {
                int inIndex = codec.dequeueInputBuffer(0);
                if (inIndex >= 0) {
                    ByteBuffer buffer = codec.getInputBuffer(inIndex);
                    int size = extractor.readSampleData(buffer, 0);
                    if (size < 0) {
                        codec.queueInputBuffer(inIndex, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                        inputDone = true;
                    } else {
                        codec.queueInputBuffer(inIndex, 0, size, extractor.getSampleTime(), 0);
                        extractor.advance();
                    }
                }
            }

            int outIndex = codec.dequeueOutputBuffer(info, 0);
            if (outIndex >= 0) {
                if (info.size > 0) {
                    ByteBuffer out = codec.getOutputBuffer(outIndex);
                    if (out != null) {
                        out.position(info.offset);
                        out.limit(info.offset + info.size);
                        byte[] data = new byte[info.size];
                        out.get(data);
                        convert(data);
                    }
                }
                codec.releaseOutputBuffer(outIndex, false);
                if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                    extractor.seekTo(0, MediaExtractor.SEEK_TO_CLOSEST_SYNC);
                    inputDone = false;
                    codec.flush();
                    codec.start();
                }
            } else if (outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                MediaFormat fmt = codec.getOutputFormat();
                colorFormat = fmt.getInteger(MediaFormat.KEY_COLOR_FORMAT);
            }
        } catch (Throwable t) {
            error = t.toString();
        }
    }

    private void convert(byte[] data) {
        int w = width;
        int h = height;
        byte[] rgba = new byte[w * h * 4];
        int cf = colorFormat;

        if (cf == MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Planar) {
            int ySize = w * h;
            int uvSize = (w / 2) * (h / 2);
            int uOff = ySize;
            int vOff = ySize + uvSize;
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int Y = data[y * w + x] & 0xFF;
                    int U = data[uOff + (y / 2) * (w / 2) + (x / 2)] & 0xFF;
                    int V = data[vOff + (y / 2) * (w / 2) + (x / 2)] & 0xFF;
                    write(rgba, (y * w + x) * 4, Y, U, V);
                }
            }
            latest = rgba;
            return;
        }

        // 默认按 NV12 / YUV420SemiPlanar 处理
        int uvOff = w * h;
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int Y = data[y * w + x] & 0xFF;
                int uvIndex = uvOff + (y / 2) * w + (x / 2) * 2;
                int U = data[uvIndex] & 0xFF;
                int V = data[uvIndex + 1] & 0xFF;
                write(rgba, (y * w + x) * 4, Y, U, V);
            }
        }
        latest = rgba;
    }

    private static void write(byte[] out, int o, int Y, int U, int V) {
        int c = Y - 16;
        int d = U - 128;
        int e = V - 128;
        out[o] = (byte) clamp((298 * c + 409 * e + 128) >> 8);
        out[o + 1] = (byte) clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
        out[o + 2] = (byte) clamp((298 * c + 516 * d + 128) >> 8);
        out[o + 3] = (byte) 255;
    }

    private static int clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

    public byte[] takeLatest() { byte[] d = latest; latest = null; return d; }
    public int getWidth() { return width; }
    public int getHeight() { return height; }
    public String getError() { return error; }

    public void stop() {
        if (codec != null) {
            try { codec.stop(); } catch (Throwable ignored) { }
            try { codec.release(); } catch (Throwable ignored) { }
            codec = null;
        }
        if (extractor != null) {
            try { extractor.release(); } catch (Throwable ignored) { }
            extractor = null;
        }
        latest = null;
    }
}
