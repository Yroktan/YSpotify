package app.yspotify;

import android.content.Context;
import org.json.JSONObject;
import java.io.IOException;
import java.time.Duration;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

final class SpotifyApi {
    final OkHttpClient http = new OkHttpClient.Builder().connectTimeout(10, TimeUnit.SECONDS).readTimeout(15, TimeUnit.SECONDS).callTimeout(20, TimeUnit.SECONDS).build();
    final SessionStore session;
    volatile String endpoint = "https://spclient.wg.spotify.com";
    SpotifyApi(Context context) { session = new SessionStore(context.getApplicationContext()); }
    static OkHttpClient streaming(OkHttpClient client) { return client.newBuilder().callTimeout(0, TimeUnit.MILLISECONDS).readTimeout(20, TimeUnit.SECONDS).build(); }
    JSONObject request(String url, String method, JSONObject body) throws Exception {
        session.ensureFresh(http);
        for (int attempt = 0; attempt < 2; attempt++) {
            Request.Builder builder = new Request.Builder().url(url);
            for (Map.Entry<String, String> header : session.headers().entrySet()) builder.header(header.getKey(), header.getValue());
            RequestBody payload = body == null ? null : RequestBody.create(body.toString(), MediaType.get("application/json"));
            builder.method(method, payload);
            try (Response response = http.newCall(builder.build()).execute()) {
                if (response.code() == 401 && attempt == 0) { session.refreshClient(http); continue; }
                if (response.code() == 401) throw new IOException("SessionRefreshRequired");
                if (!response.isSuccessful()) throw new IOException("SpotifyHttp" + response.code());
                String text = response.body() == null ? "" : response.body().string();
                return text.isEmpty() ? new JSONObject() : J.parse(text);
            }
        }
        throw new IOException("SessionRefreshRequired");
    }
    JSONObject player(String path, String method, JSONObject body) throws Exception { return request(endpoint + path, method, body); }
    void resolveEndpoint() {
        try (Response response = http.newCall(new Request.Builder().url("https://apresolve.spotify.com/?type=spclient").build()).execute()) {
            if (!response.isSuccessful() || response.body() == null) return;
            org.json.JSONArray hosts = J.array(J.parse(response.body().string()), "spclient");
            for (int i = 0; i < hosts.length(); i++) {
                String host = hosts.optString(i);
                if (host.matches("[a-z0-9]+-spclient\\.spotify\\.com:443")) { endpoint = "https://" + host.substring(0, host.length() - 4); return; }
            }
        } catch (Exception ignored) { }
    }
    static String message(Throwable error) {
        String code = error.getMessage() == null ? "" : error.getMessage();
        if (code.contains("Session")) return "Не удалось обновить вход. Открой аккаунт.";
        if (code.contains("429")) return "Spotify временно ограничил запросы. Попробуй позже.";
        if (code.contains("403") || code.contains("Restricted")) return "Spotify ограничил это действие для текущего трека.";
        if (code.contains("Catalog")) return "Не удалось получить полный список. Сохранённая музыка остаётся доступной.";
        return "Не удалось подключиться к Spotify. Проверь соединение.";
    }
}
