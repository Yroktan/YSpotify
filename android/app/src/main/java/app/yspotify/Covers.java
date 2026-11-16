package app.yspotify;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Handler;
import android.os.Looper;
import android.util.LruCache;
import android.widget.ImageView;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.InputStream;
import java.lang.ref.WeakReference;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

final class Covers {
    private final LruCache<String, Bitmap> images = new LruCache<String, Bitmap>(4096) { @Override protected int sizeOf(String key, Bitmap value) { return value.getAllocationByteCount() / 1024; } };
    private final Map<String, List<WeakReference<ImageView>>> pending = new HashMap<>();
    private final Map<String, Long> failed = new HashMap<>();
    private final ExecutorService workers = Executors.newFixedThreadPool(2);
    private final Handler main = new Handler(Looper.getMainLooper());
    private final File directory;
    private final OkHttpClient http;
    private boolean deferred;
    Covers(Context context, OkHttpClient http) { directory = new File(context.getCacheDir(), "covers"); directory.mkdirs(); this.http = http; }
    void defer(boolean value) { deferred = value; }
    void bind(ImageView view, String url, int size) {
        String key = url + ':' + size; view.setTag(key); Bitmap cached = images.get(key); if (cached != null) { view.setImageBitmap(cached); return; } view.setImageDrawable(null);
        if (deferred || url.isEmpty() || failed.containsKey(key) && failed.get(key) > System.currentTimeMillis()) return;
        if (pending.containsKey(key)) { pending.get(key).add(new WeakReference<>(view)); return; }
        List<WeakReference<ImageView>> targets = new ArrayList<>(); targets.add(new WeakReference<>(view)); pending.put(key, targets);
        workers.execute(() -> {
            Bitmap result = null;
            try {
                java.net.URI uri = new java.net.URI(url); String host = uri.getHost(); if (!"https".equals(uri.getScheme()) || host == null || !(host.equals("scdn.co") || host.endsWith(".scdn.co"))) throw new IllegalArgumentException();
                byte[] digest = MessageDigest.getInstance("SHA-256").digest(url.getBytes(StandardCharsets.UTF_8)); StringBuilder name = new StringBuilder(); for (byte b : digest) name.append(String.format(java.util.Locale.ROOT, "%02x", b)); File file = new File(directory, name + ".jpg"); byte[] bytes;
                if (file.exists() && file.length() <= 512 * 1024) bytes = Files.readAllBytes(file.toPath());
                else try (Response response = http.newCall(new Request.Builder().url(url).build()).execute()) {
                    if (!response.isSuccessful() || response.body() == null || response.body().contentLength() > 512 * 1024) throw new IllegalStateException();
                    try (InputStream input = response.body().byteStream(); ByteArrayOutputStream output = new ByteArrayOutputStream()) { byte[] buffer = new byte[8192]; int length; while ((length = input.read(buffer)) != -1) { if (output.size() + length > 512 * 1024) throw new IllegalStateException(); output.write(buffer, 0, length); } bytes = output.toByteArray(); }
                    Files.write(file.toPath(), bytes);
                }
                BitmapFactory.Options options = new BitmapFactory.Options(); options.inJustDecodeBounds = true; BitmapFactory.decodeByteArray(bytes, 0, bytes.length, options);
                if (options.outWidth < 1 || options.outHeight < 1 || options.outWidth > 2048 || options.outHeight > 2048) throw new IllegalStateException();
                options.inJustDecodeBounds = false;
                options.inSampleSize = 1; while (options.outWidth / options.inSampleSize > size * 2) options.inSampleSize *= 2;
                Bitmap decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.length, options); result = Bitmap.createScaledBitmap(decoded, size, size, true); if (decoded != result) decoded.recycle();
            } catch (Exception ignored) { }
            Bitmap completed = result;
            main.post(() -> { if (completed != null) images.put(key, completed); else failed.put(key, System.currentTimeMillis() + 60000); List<WeakReference<ImageView>> references = pending.remove(key); if (references != null) for (WeakReference<ImageView> reference : references) { ImageView target = reference.get(); if (target != null && key.equals(target.getTag()) && completed != null) target.setImageBitmap(completed); } });
        });
    }
    void close() { workers.shutdownNow(); images.evictAll(); pending.clear(); }
}
