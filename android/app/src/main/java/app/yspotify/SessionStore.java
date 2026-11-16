package app.yspotify;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.webkit.CookieManager;
import org.json.JSONObject;
import java.io.File;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.KeyStore;
import java.time.ZonedDateTime;
import java.time.format.DateTimeFormatter;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

final class SessionStore {
    private final File file;
    private volatile JSONObject value;
    private long revision;
    private final Object refreshLock = new Object();
    interface Refresher { JSONObject refresh(JSONObject previous) throws Exception; }
    volatile Refresher fallback;
    SessionStore(Context context) {
        file = new File(context.getFilesDir(), "session.enc");
        try {
            if (file.exists()) {
                ByteBuffer bytes = ByteBuffer.wrap(Files.readAllBytes(file.toPath()));
                int length = bytes.get() & 255;
                if (length != 12 || bytes.remaining() < length + 16) throw new IllegalStateException();
                byte[] iv = new byte[length]; bytes.get(iv);
                byte[] encrypted = new byte[bytes.remaining()]; bytes.get(encrypted);
                Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
                cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, iv));
                value = J.parse(new String(cipher.doFinal(encrypted), StandardCharsets.UTF_8));
            }
            if (BuildConfig.DEBUG) {
                File imported = new File(context.getFilesDir(), "debug-session.json");
                if (imported.exists()) {
                    JSONObject incoming = J.parse(new String(Files.readAllBytes(imported.toPath()), StandardCharsets.UTF_8));
                    StringBuilder cookie = new StringBuilder();
                    for (int i = 0; i < J.array(incoming, "cookies").length(); i++) {
                        JSONObject entry = J.array(incoming, "cookies").optJSONObject(i);
                        String name = J.text(entry, "name");
                        if (name.equals("sp_dc") || name.equals("sp_key")) {
                            if (cookie.length() > 0) cookie.append("; ");
                            cookie.append(name).append('=').append(J.text(entry, "value"));
                        }
                    }
                    save(J.o("bearer", J.text(incoming, "bearer"), "client", J.text(incoming, "client"), "version", J.text(incoming, "version"), "validUntil", incoming.optLong("validUntil"), "cookie", cookie.toString()));
                    if (!imported.delete()) throw new IllegalStateException("Import cleanup failed");
                }
            }
        } catch (Exception ignored) { value = null; }
    }
    private SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        String alias = "YSpotify.session.v1";
        if (store.containsAlias(alias)) return (SecretKey) store.getKey(alias, null);
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build());
        return generator.generateKey();
    }
    boolean exists() { JSONObject current = value; return current != null && !J.text(current, "bearer").isEmpty() && !J.text(current, "client").isEmpty(); }
    JSONObject snapshot() { JSONObject current = value; return current == null ? new JSONObject() : J.parse(current.toString()); }
    synchronized void save(JSONObject next) throws Exception {
        if (!J.text(next, "bearer").startsWith("Bearer ") || J.text(next, "client").isEmpty()) throw new IllegalArgumentException("Invalid session");
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] encrypted = cipher.doFinal(next.toString().getBytes(StandardCharsets.UTF_8));
        byte[] iv = cipher.getIV(); ByteBuffer packed = ByteBuffer.allocate(1 + iv.length + encrypted.length);
        packed.put((byte) iv.length).put(iv).put(encrypted);
        File temporary = new File(file.getPath() + ".tmp"); Files.write(temporary.toPath(), packed.array());
        Files.move(temporary.toPath(), file.toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        value = J.parse(next.toString());
        revision++;
    }
    synchronized void clear() { revision++; value = null; if (file.exists()) file.delete(); }
    Map<String, String> headers() {
        JSONObject value = snapshot();
        Map<String, String> result = new LinkedHashMap<>();
        result.put("authorization", J.text(value, "bearer")); result.put("client-token", J.text(value, "client"));
        result.put("app-platform", "WebPlayer"); result.put("origin", "https://open.spotify.com"); result.put("referer", "https://open.spotify.com/");
        if (!J.text(value, "version").isEmpty()) result.put("spotify-app-version", J.text(value, "version"));
        return result;
    }
    void ensureFresh(OkHttpClient http) throws Exception {
        if (!exists()) throw new IllegalStateException("SessionRequired");
        synchronized (refreshLock) { if (snapshot().optLong("validUntil") < System.currentTimeMillis() + 60000) refresh(http); }
    }
    void refresh(OkHttpClient http) throws Exception { refresh(http, false); }
    void refreshClient(OkHttpClient http) throws Exception { refresh(http, true); }
    private void refresh(OkHttpClient http, boolean withClient) throws Exception {
        synchronized (refreshLock) {
            long expected; JSONObject previous;
            synchronized (this) { if (!exists()) throw new IllegalStateException("SessionRequired"); expected = revision; previous = snapshot(); }
            JSONObject next;
            try { next = withClient && fallback != null ? fallback.refresh(previous) : refreshHttp(http, previous); }
            catch (Exception failure) { if (fallback == null) throw failure; next = fallback.refresh(previous); }
            synchronized (this) { if (revision != expected || !exists()) throw new IllegalStateException("SessionChanged"); save(next); }
        }
    }
    private JSONObject refreshHttp(OkHttpClient http, JSONObject previous) throws Exception {
        String cookie = J.text(previous, "cookie");
        if (cookie.isEmpty()) throw new IllegalStateException("SessionRefreshRequired");
        Request request = new Request.Builder().url("https://open.spotify.com/").header("Cookie", cookie).header("Cache-Control", "no-cache").build();
        try (Response response = http.newCall(request).execute()) {
            String html = response.body() == null ? "" : response.body().string();
            Matcher script = Pattern.compile("<script\\b[^>]*\\bid=[\"']session[\"'][^>]*>(.*?)</script>", Pattern.DOTALL | Pattern.CASE_INSENSITIVE).matcher(html);
            JSONObject fresh = script.find() ? J.parse(script.group(1)) : new JSONObject();
            if (fresh.optBoolean("isAnonymous", false)) throw new IllegalStateException("SessionRefreshRequired");
            String token = J.text(fresh, "accessToken");
            if (token.isEmpty()) throw new IllegalStateException("SessionRefreshRequired");
            long expiry = fresh.optLong("accessTokenExpirationTimestampMs");
            long server = System.currentTimeMillis();
            try { server = ZonedDateTime.parse(response.header("Date", ""), DateTimeFormatter.RFC_1123_DATE_TIME).toInstant().toEpochMilli(); } catch (Exception ignored) { }
            long ttl = Math.max(60000, Math.min(3600000, expiry - server));
            JSONObject next = J.parse(previous.toString()); next.put("bearer", "Bearer " + token); next.put("validUntil", System.currentTimeMillis() + ttl);
            Map<String, String> retained = new LinkedHashMap<>();
            for (String part : cookie.split(";")) { String[] pair = part.trim().split("=", 2); if (pair.length == 2) retained.put(pair[0], pair[1]); }
            for (String header : response.headers("Set-Cookie")) { String[] pair = header.split(";", 2)[0].split("=", 2); if (pair.length == 2 && (pair[0].equals("sp_dc") || pair[0].equals("sp_key"))) retained.put(pair[0], pair[1]); }
            StringBuilder updated = new StringBuilder();
            for (Map.Entry<String, String> entry : retained.entrySet()) { if (updated.length() > 0) updated.append("; "); updated.append(entry.getKey()).append('=').append(entry.getValue()); }
            next.put("cookie", updated.toString()); return next;
        }
    }
}
