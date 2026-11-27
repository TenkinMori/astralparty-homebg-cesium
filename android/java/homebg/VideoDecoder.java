package homebg;

import android.graphics.ImageFormat;
import android.graphics.Rect;
import android.media.Image;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaExtractor;
import android.media.MediaFormat;

import java.nio.ByteBuffer;

/**
 * 后台线程 MediaCodec 视频解码器：
 * - 输出 YUV420 Planar (Y | U | V) 到 2 块复用 DirectByteBuffer（零每帧堆分配）
 * - 按 presentationTimeUs 节流，避免跟游戏 60/120Hz 同步狂解
 * - 支持离开主页暂停 (setPaused)，零 CPU 空转
 */
public final class VideoDecoder {
    private static final long WAIT_SLICE_MS = 5L;
    private static final long LATE_DROP_NS = 150_000_000L;

    private final Object slotLock = new Object();
    private final Object pauseLock = new Object();

    private MediaExtractor extractor;
    private MediaCodec codec;
    private Thread worker;

    private volatile boolean running;
    private volatile boolean paused;
    private volatile boolean resetAnchor;
    private volatile String error = "";

    private int width;
    private int height;
    private int ySize;
    private int uvSize;
    private int uOffset;
    private int vOffset;

    private final ByteBuffer[] slots = new ByteBuffer[2];
    private int readySlot = -1;
    private int heldSlot = -1;

    private byte[] scratchRow;
    private byte[] fallbackScratch;

    public static VideoDecoder create() {
        return new VideoDecoder();
    }

    public boolean start(String path, int targetWidth, int targetHeight) {
        stop();
        error = "";
        try {
            extractor = new MediaExtractor();
            extractor.setDataSource(path);

            int track = -1;
            MediaFormat format = null;
            String mime = null;
            for (int i = 0; i < extractor.getTrackCount(); i++) {
                MediaFormat f = extractor.getTrackFormat(i);
                String m = f.getString(MediaFormat.KEY_MIME);
                if (m != null && m.startsWith("video/")) {
                    track = i;
                    format = f;
                    mime = m;
                    break;
                }
            }
            if (track < 0 || format == null || mime == null) {
                error = "no video track";
                stop();
                return false;
            }
            extractor.selectTrack(track);

            int srcW = format.getInteger(MediaFormat.KEY_WIDTH);
            int srcH = format.getInteger(MediaFormat.KEY_HEIGHT);
            if (format.containsKey("crop-left") && format.containsKey("crop-right")) {
                srcW = format.getInteger("crop-right") - format.getInteger("crop-left") + 1;
            }
            if (format.containsKey("crop-top") && format.containsKey("crop-bottom")) {
                srcH = format.getInteger("crop-bottom") - format.getInteger("crop-top") + 1;
            }

            width = Math.max(2, srcW & ~1);
            height = Math.max(2, srcH & ~1);
            ySize = width * height;
            uvSize = (width / 2) * (height / 2);
            uOffset = ySize;
            vOffset = ySize + uvSize;

            int totalBytes = ySize + uvSize * 2;
            slots[0] = ByteBuffer.allocateDirect(totalBytes);
            slots[1] = ByteBuffer.allocateDirect(totalBytes);
            readySlot = -1;
            heldSlot = -1;
            scratchRow = new byte[Math.max(width, 4096)];

            codec = configureCodec(mime, format);
            codec.start();

            running = true;
            paused = false;
            resetAnchor = true;

            worker = new Thread(this::decodeLoop, "HomeBgVideoDecoder");
            worker.setDaemon(true);
            worker.start();
            return true;
        } catch (Throwable t) {
            error = t.toString();
            stop();
            return false;
        }
    }

    private static MediaCodec configureCodec(String mime, MediaFormat format) throws Exception {
        MediaCodec c = MediaCodec.createDecoderByType(mime);
        try {
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                    MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible);
            c.configure(format, null, null, 0);
            return c;
        } catch (Throwable ignored) {
            try { c.release(); } catch (Throwable ignored2) { }
            c = MediaCodec.createDecoderByType(mime);
            if (format.containsKey(MediaFormat.KEY_COLOR_FORMAT)) {
                MediaFormat clean = new MediaFormat();
                clean.setString(MediaFormat.KEY_MIME, mime);
                clean.setInteger(MediaFormat.KEY_WIDTH, format.getInteger(MediaFormat.KEY_WIDTH));
                clean.setInteger(MediaFormat.KEY_HEIGHT, format.getInteger(MediaFormat.KEY_HEIGHT));
                if (format.containsKey("csd-0")) clean.setByteBuffer("csd-0", format.getByteBuffer("csd-0"));
                if (format.containsKey("csd-1")) clean.setByteBuffer("csd-1", format.getByteBuffer("csd-1"));
                c.configure(clean, null, null, 0);
            } else {
                c.configure(format, null, null, 0);
            }
            return c;
        }
    }

    private void decodeLoop() {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        boolean inputDone = false;
        long anchorPtsUs = -1L;
        long anchorWallNs = 0L;
        int outColorFormat = -1;
        MediaFormat outFormat = null;

        try {
            while (running) {
                if (paused) {
                    synchronized (pauseLock) {
                        while (running && paused) {
                            pauseLock.wait(200L);
                        }
                    }
                    resetAnchor = true;
                    continue;
                }

                if (!inputDone) {
                    int inIndex = codec.dequeueInputBuffer(5000L);
                    if (inIndex >= 0) {
                        ByteBuffer inBuf = codec.getInputBuffer(inIndex);
                        int sampleSize = inBuf != null ? extractor.readSampleData(inBuf, 0) : -1;
                        if (sampleSize < 0) {
                            codec.queueInputBuffer(inIndex, 0, 0, 0L, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                            inputDone = true;
                        } else {
                            codec.queueInputBuffer(inIndex, 0, sampleSize, extractor.getSampleTime(), 0);
                            extractor.advance();
                        }
                    }
                }

                int outIndex = codec.dequeueOutputBuffer(info, 5000L);
                if (outIndex >= 0) {
                    boolean eos = (info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                    if (info.size > 0 && running) {
                        long ptsUs = info.presentationTimeUs;
                        if (resetAnchor || anchorPtsUs < 0L || ptsUs < anchorPtsUs) {
                            anchorPtsUs = ptsUs;
                            anchorWallNs = System.nanoTime();
                            resetAnchor = false;
                        }

                        long dueNs = anchorWallNs + (ptsUs - anchorPtsUs) * 1000L;
                        while (running && !paused) {
                            long remainNs = dueNs - System.nanoTime();
                            if (remainNs <= 0L) break;
                            long sleepMs = Math.min(WAIT_SLICE_MS, (remainNs + 999_999L) / 1_000_000L);
                            if (sleepMs > 0L) {
                                Thread.sleep(sleepMs);
                            }
                        }

                        if (running && !paused) {
                            long lateNs = System.nanoTime() - dueNs;
                            if (lateNs > LATE_DROP_NS) {
                                anchorPtsUs = ptsUs;
                                anchorWallNs = System.nanoTime();
                            }

                            int writeSlot = pickWritableSlot();
                            ByteBuffer dst = slots[writeSlot];
                            boolean wrote = false;

                            Image image = null;
                            try {
                                image = codec.getOutputImage(outIndex);
                            } catch (Throwable ignored) {
                                image = null;
                            }

                            if (image != null) {
                                try {
                                    wrote = copyImageToSlot(image, dst);
                                } finally {
                                    image.close();
                                }
                            } else {
                                ByteBuffer outBuf = codec.getOutputBuffer(outIndex);
                                if (outBuf != null) {
                                    wrote = copyBufferToSlot(outBuf, info, outFormat, outColorFormat, dst);
                                }
                            }

                            if (wrote) {
                                publishSlot(writeSlot);
                            }
                        }
                    }

                    codec.releaseOutputBuffer(outIndex, false);

                    if (eos && running) {
                        extractor.seekTo(0L, MediaExtractor.SEEK_TO_CLOSEST_SYNC);
                        codec.flush();
                        inputDone = false;
                        resetAnchor = true;
                        anchorPtsUs = -1L;
                    }
                } else if (outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    outFormat = codec.getOutputFormat();
                    if (outFormat.containsKey(MediaFormat.KEY_COLOR_FORMAT)) {
                        outColorFormat = outFormat.getInteger(MediaFormat.KEY_COLOR_FORMAT);
                    }
                }
            }
        } catch (Throwable t) {
            if (running) {
                error = t.toString();
            }
        }
    }

    private int pickWritableSlot() {
        synchronized (slotLock) {
            if (heldSlot == 0) return 1;
            if (heldSlot == 1) return 0;
            return readySlot == 0 ? 1 : 0;
        }
    }

    private void publishSlot(int slot) {
        synchronized (slotLock) {
            readySlot = slot;
        }
    }

    private boolean copyImageToSlot(Image image, ByteBuffer dst) {
        if (image.getFormat() != ImageFormat.YUV_420_888) {
            return false;
        }
        Image.Plane[] planes = image.getPlanes();
        if (planes == null || planes.length < 3) {
            return false;
        }

        Rect crop = image.getCropRect();
        int copyW = Math.min(width, crop.width()) & ~1;
        int copyH = Math.min(height, crop.height()) & ~1;
        if (copyW < 2 || copyH < 2) {
            return false;
        }

        int cropLeft = crop.left;
        int cropTop = crop.top;

        // Y plane
        copyPlane(
                planes[0].getBuffer(),
                planes[0].getRowStride(),
                planes[0].getPixelStride(),
                cropLeft,
                cropTop,
                copyW,
                copyH,
                dst,
                0,
                width);

        // U and V planes (4:2:0)
        int cw = copyW / 2;
        int ch = copyH / 2;
        int cLeft = cropLeft / 2;
        int cTop = cropTop / 2;
        int dstHalfW = width / 2;

        copyPlane(
                planes[1].getBuffer(),
                planes[1].getRowStride(),
                planes[1].getPixelStride(),
                cLeft,
                cTop,
                cw,
                ch,
                dst,
                uOffset,
                dstHalfW);

        copyPlane(
                planes[2].getBuffer(),
                planes[2].getRowStride(),
                planes[2].getPixelStride(),
                cLeft,
                cTop,
                cw,
                ch,
                dst,
                vOffset,
                dstHalfW);

        return true;
    }

    private void copyPlane(
            ByteBuffer src,
            int srcRowStride,
            int srcPixelStride,
            int startX,
            int startY,
            int copyW,
            int copyH,
            ByteBuffer dst,
            int dstOffset,
            int dstRowStride) {
        ByteBuffer dup = src.duplicate();
        int limit = dup.limit();

        if (srcPixelStride == 1) {
            byte[] row = scratchRow.length >= copyW ? scratchRow : (scratchRow = new byte[copyW]);
            for (int y = 0; y < copyH; y++) {
                int srcPos = (startY + y) * srcRowStride + startX;
                if (srcPos + copyW > limit) break;
                dup.position(srcPos);
                dup.get(row, 0, copyW);
                dst.position(dstOffset + y * dstRowStride);
                dst.put(row, 0, copyW);
            }
            return;
        }

        byte[] row = scratchRow.length >= copyW ? scratchRow : (scratchRow = new byte[copyW]);
        for (int y = 0; y < copyH; y++) {
            int rowBase = (startY + y) * srcRowStride + startX * srcPixelStride;
            int needed = (copyW - 1) * srcPixelStride + 1;
            if (rowBase + needed > limit) break;
            for (int x = 0; x < copyW; x++) {
                row[x] = dup.get(rowBase + x * srcPixelStride);
            }
            dst.position(dstOffset + y * dstRowStride);
            dst.put(row, 0, copyW);
        }
    }

    private boolean copyBufferToSlot(
            ByteBuffer outBuf,
            MediaCodec.BufferInfo info,
            MediaFormat outFormat,
            int cf,
            ByteBuffer dst) {
        int stride = width;
        int sliceHeight = height;
        if (outFormat != null) {
            if (outFormat.containsKey(MediaFormat.KEY_STRIDE)) {
                stride = Math.max(width, outFormat.getInteger(MediaFormat.KEY_STRIDE));
            }
            if (outFormat.containsKey(MediaFormat.KEY_SLICE_HEIGHT)) {
                sliceHeight = Math.max(height, outFormat.getInteger(MediaFormat.KEY_SLICE_HEIGHT));
            }
        }

        ByteBuffer dup = outBuf.duplicate();
        int base = info.offset;
        int limit = info.offset + info.size;
        if (base + stride * (height - 1) + width > limit) {
            return false;
        }

        // Y
        copyPlane(dup, stride, 1, 0, base, width, height, dst, 0, width);

        int cw = width / 2;
        int ch = height / 2;
        int uvBase = base + stride * sliceHeight;

        if (cf == MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Planar) {
            int cStride = stride / 2;
            int uPlane = uvBase;
            int vPlane = uvBase + cStride * (sliceHeight / 2);
            if (vPlane + cStride * (ch - 1) + cw > limit) return false;
            for (int y = 0; y < ch; y++) {
                copyRawRow(dup, uPlane + y * cStride, cw, dst, uOffset + y * cw);
                copyRawRow(dup, vPlane + y * cStride, cw, dst, vOffset + y * cw);
            }
            return true;
        }

        // Default NV12 (YUV420SemiPlanar)
        if (uvBase + stride * (ch - 1) + width > limit) return false;
        byte[] uRow = new byte[cw];
        byte[] vRow = new byte[cw];
        for (int y = 0; y < ch; y++) {
            int pos = uvBase + y * stride;
            for (int x = 0; x < cw; x++) {
                uRow[x] = dup.get(pos + x * 2);
                vRow[x] = dup.get(pos + x * 2 + 1);
            }
            dst.position(uOffset + y * cw);
            dst.put(uRow, 0, cw);
            dst.position(vOffset + y * cw);
            dst.put(vRow, 0, cw);
        }
        return true;
    }

    private void copyRawRow(ByteBuffer src, int srcPos, int len, ByteBuffer dst, int dstPos) {
        byte[] row = scratchRow.length >= len ? scratchRow : (scratchRow = new byte[len]);
        src.position(srcPos);
        src.get(row, 0, len);
        dst.position(dstPos);
        dst.put(row, 0, len);
    }

    public ByteBuffer getSlotBuffer(int slot) {
        if (slot < 0 || slot >= slots.length) return null;
        return slots[slot];
    }

    public int acquireReadySlot() {
        synchronized (slotLock) {
            if (readySlot < 0) return -1;
            heldSlot = readySlot;
            readySlot = -1;
            return heldSlot;
        }
    }

    public void releaseSlot(int slot) {
        synchronized (slotLock) {
            if (heldSlot == slot) {
                heldSlot = -1;
            }
        }
    }

    public byte[] copySlotBytes(int slot) {
        if (slot < 0 || slot >= slots.length || slots[slot] == null) return null;
        int total = ySize + uvSize * 2;
        if (fallbackScratch == null || fallbackScratch.length != total) {
            fallbackScratch = new byte[total];
        }
        ByteBuffer dup = slots[slot].duplicate();
        dup.position(0);
        dup.get(fallbackScratch, 0, total);
        return fallbackScratch;
    }

    public void setPaused(boolean p) {
        if (paused == p) return;
        synchronized (pauseLock) {
            paused = p;
            if (!p) {
                resetAnchor = true;
                pauseLock.notifyAll();
            }
        }
    }

    public int getWidth() { return width; }
    public int getHeight() { return height; }
    public int getYSize() { return ySize; }
    public int getUVSize() { return uvSize; }
    public int getUOffset() { return uOffset; }
    public int getVOffset() { return vOffset; }
    public String getError() { return error; }

    public void stop() {
        running = false;
        synchronized (pauseLock) {
            paused = false;
            pauseLock.notifyAll();
        }
        Thread t = worker;
        worker = null;
        if (t != null) {
            try { t.join(500L); } catch (InterruptedException ignored) { }
        }
        if (codec != null) {
            try { codec.stop(); } catch (Throwable ignored) { }
            try { codec.release(); } catch (Throwable ignored) { }
            codec = null;
        }
        if (extractor != null) {
            try { extractor.release(); } catch (Throwable ignored) { }
            extractor = null;
        }
        synchronized (slotLock) {
            readySlot = -1;
            heldSlot = -1;
        }
        slots[0] = null;
        slots[1] = null;
    }
}
