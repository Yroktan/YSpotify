package app.yspotify;

import android.content.Context;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.webkit.CookieManager;
import android.webkit.ConsoleMessage;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import androidx.webkit.WebViewCompat;
import androidx.webkit.WebViewFeature;
import org.json.JSONObject;
import org.json.JSONTokener;
import java.time.ZonedDateTime;
import java.time.format.DateTimeFormatter;
import java.util.Collections;
import java.util.Map;
import java.util.function.Consumer;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

final class AuthCapture implements AutoCloseable {
    final WebView web;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final OkHttpClient http = new OkHttpClient();
    private final Consumer<JSONObject> success;
    private final Consumer<Exception> failure;
    private String bearer = "", client = "", version = "";
    private long expiry;
    private boolean validating, completed;
    AuthCapture(Context context, JSONObject previous, Consumer<JSONObject> success, Consumer<Exception> failure) {
        this.success = success; this.failure = failure; client = J.text(previous, "client"); version = J.text(previous, "version");
        web = new WebView(context); web.getSettings().setJavaScriptEnabled(true); web.getSettings().setDomStorageEnabled(true); web.getSettings().setAllowFileAccess(false); web.getSettings().setAllowContentAccess(false);
        web.getSettings().setUserAgentString(web.getSettings().getUserAgentString().replace("; wv", "").replace("Version/4.0 ", ""));
        CookieManager cookies = CookieManager.getInstance(); cookies.setAcceptCookie(true); cookies.setAcceptThirdPartyCookies(web, false);
        for (String part : J.text(previous, "cookie").split(";")) { String text = part.trim(); if (text.startsWith("sp_dc=") || text.startsWith("sp_key=")) cookies.setCookie("https://open.spotify.com", text + "; Domain=.spotify.com; Path=/; Secure; HttpOnly"); }
        web.setWebChromeClient(new WebChromeClient() { @Override public boolean onConsoleMessage(ConsoleMessage message) { return true; } });
        if (WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) WebViewCompat.addWebMessageListener(web, "YSpotifySession", Collections.singleton("https://open.spotify.com"), (view, message, origin, mainFrame, reply) -> {
            if (!mainFrame || !"https".equals(origin.getScheme()) || !"open.spotify.com".equals(origin.getHost())) return;
            try { token(J.parse(message.getData())); } catch (Exception ignored) { }
        });
        String script = "(()=>{const send=(d,date)=>{if(!d||d.isAnonymous===true)return;const t=d.accessToken||d.access_token;if(t&&window.YSpotifySession)YSpotifySession.postMessage(JSON.stringify({token:t,expiry:d.accessTokenExpirationTimestampMs||d.expiresAt||0,seconds:d.expires_in||0,date}));};const original=window.fetch;window.fetch=async function(...a){const r=await original.apply(this,a);if(/(^|\\.)spotify\\.com$/.test(new URL(r.url).hostname))r.clone().json().then(d=>send(d,r.headers.get('date'))).catch(()=>{});return r;};const open=XMLHttpRequest.prototype.open;XMLHttpRequest.prototype.open=function(...a){this.addEventListener('load',()=>{try{send(JSON.parse(this.responseText),this.getResponseHeader('date'))}catch{}});return open.apply(this,a);};})();";
        if (WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT)) WebViewCompat.addDocumentStartJavaScript(web, script, Collections.singleton("https://open.spotify.com"));
        web.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                String host = request.getUrl().getHost();
                return !"https".equals(request.getUrl().getScheme()) || host == null || !(host.equals("spotify.com") || host.endsWith(".spotify.com") || host.equals("accounts.google.com") || host.endsWith(".facebook.com"));
            }
            @Override public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
                String host = request.getUrl().getHost();
                if (host != null && host.endsWith(".spotify.com") && cookie().contains("sp_dc=")) {
                    Map<String, String> headers = request.getRequestHeaders();
                    main.post(() -> { if (completed) return; for (Map.Entry<String, String> header : headers.entrySet()) { if (header.getKey().equalsIgnoreCase("authorization") && header.getValue().startsWith("Bearer ")) bearer = header.getValue(); if (header.getKey().equalsIgnoreCase("client-token")) client = header.getValue(); if (header.getKey().equalsIgnoreCase("spotify-app-version")) version = header.getValue(); } validate(); });
                }
                return null;
            }
            @Override public void onPageFinished(WebView view, String url) {
                if (url.startsWith("https://open.spotify.com/")) view.evaluateJavascript("JSON.stringify((()=>{try{const d=JSON.parse(document.querySelector('script#session')?.textContent||'{}');return{token:d.isAnonymous===true?'':d.accessToken,expiry:d.accessTokenExpirationTimestampMs}}catch{return{}}})())", result -> { try { Object decoded = new JSONTokener(result).nextValue(); if (decoded instanceof String) token(J.parse((String) decoded)); } catch (Exception ignored) { } });
                validate();
            }
        });
    }
    private void token(JSONObject data) {
        String token = J.text(data, "token"); if (token.isEmpty() || !cookie().contains("sp_dc=")) return;
        bearer = "Bearer " + token; long server = System.currentTimeMillis();
        try { server = ZonedDateTime.parse(J.text(data, "date"), DateTimeFormatter.RFC_1123_DATE_TIME).toInstant().toEpochMilli(); } catch (Exception ignored) { }
        long ttl = data.optLong("expiry") - server; if (data.optLong("seconds") > 0) ttl = data.optLong("seconds") * 1000;
        if (ttl > 0 && ttl < 7200000) expiry = System.currentTimeMillis() + ttl;
        validate();
    }
    static String cookie() {
        String raw = CookieManager.getInstance().getCookie("https://open.spotify.com"); StringBuilder result = new StringBuilder();
        if (raw != null) for (String value : raw.split(";")) { String part = value.trim(); if (part.startsWith("sp_dc=") || part.startsWith("sp_key=")) { if (result.length() > 0) result.append("; "); result.append(part); } }
        return result.toString();
    }
    void start() { web.loadUrl(cookie().contains("sp_dc=") ? "https://open.spotify.com/search" : "https://accounts.spotify.com/login?continue=https%3A%2F%2Fopen.spotify.com%2Fsearch"); }
    void timeout(long milliseconds) { main.postDelayed(() -> { if (!completed) { completed = true; failure.accept(new IllegalStateException("SessionRefreshRequired")); } }, milliseconds); }
    void validate() {
        if (completed || validating || !bearer.startsWith("Bearer ") || client.isEmpty() || !cookie().contains("sp_dc=")) return;
        validating = true; String auth = bearer, clientToken = client, appVersion = version; long until = expiry > System.currentTimeMillis() + 60000 ? expiry : System.currentTimeMillis() + 5 * 60000;
        JSONObject variables = J.o("operationName", "profileAttributes", "variables", new JSONObject(), "extensions", J.o("persistedQuery", J.o("version", 1, "sha256Hash", "08ffb4730af3746e04a8301396f20875dbbce10c75243803091a9274eacc8ac0")));
        Request request = new Request.Builder().url("https://api-partner.spotify.com/pathfinder/v2/query").header("authorization", auth).header("client-token", clientToken).header("app-platform", "WebPlayer").header("origin", "https://open.spotify.com").post(RequestBody.create(variables.toString(), MediaType.get("application/json"))).build();
        http.newCall(request).enqueue(new okhttp3.Callback() {
            @Override public void onFailure(okhttp3.Call call, java.io.IOException error) { main.post(() -> validating = false); }
            @Override public void onResponse(okhttp3.Call call, Response response) {
                boolean valid = false;
                try (Response reply = response) { if (reply.isSuccessful() && reply.body() != null) { JSONObject profile = J.obj(J.obj(J.obj(J.parse(reply.body().string()), "data"), "me"), "profile"); valid = !J.text(profile, "username").isEmpty(); } } catch (Exception ignored) { }
                boolean accepted = valid; main.post(() -> { validating = false; if (!accepted || completed) return; completed = true; CookieManager.getInstance().flush(); success.accept(J.o("bearer", auth, "client", clientToken, "version", appVersion, "validUntil", until, "cookie", cookie())); });
            }
        });
    }
    @Override public void close() { completed = true; main.removeCallbacksAndMessages(null); http.dispatcher().cancelAll(); web.stopLoading(); web.destroy(); }
}
