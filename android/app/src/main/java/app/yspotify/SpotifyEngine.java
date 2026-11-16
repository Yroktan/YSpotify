package app.yspotify;

import org.json.JSONArray;
import org.json.JSONObject;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.text.Normalizer;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;
import java.util.zip.GZIPInputStream;

final class SpotifyEngine implements AutoCloseable {
    interface Listener {
        void media(SpotifyEngine origin, JSONObject metadata, String url, boolean ad, long generation);
        Audio audio();
        boolean advance(String previous, String next);
        void connectionError();
    }
    static final class Audio {
        final String uri;
        final long position, duration;
        final boolean paused, ended, ready;
        Audio(String uri, long position, long duration, boolean paused, boolean ended, boolean ready) { this.uri = uri; this.position = position; this.duration = duration; this.paused = paused; this.ended = ended; this.ready = ready; }
    }
    final SpotifyApi api;
    final Catalog catalog;
    final AtomicLong generation = new AtomicLong();
    private final Listener listener;
    private final String device = UUID.randomUUID().toString().replace("-", "") + UUID.randomUUID().toString().replace("-", "").substring(0, 8);
    private final LinkedHashMap<String, CachedUrl> urls = new LinkedHashMap<>();
    private final LinkedHashMap<String, String> aliases = new LinkedHashMap<>();
    private JSONObject machine = new JSONObject(), reference = new JSONObject();
    private long revision, sequence;
    private WebSocket dealer;
    private String endedState = "";
    private volatile boolean registered, closed, commanding;
    volatile byte[] certificate;
    private static final class CachedUrl { final String url; final long until; CachedUrl(String url) { this.url = url; until = System.currentTimeMillis() + 120000; } }
    SpotifyEngine(SpotifyApi api, Catalog catalog, Listener listener) { this.api = api; this.catalog = catalog; this.listener = listener; }
    synchronized void accept(JSONObject payload) {
        JSONObject incoming = payload.optJSONObject("state_machine"); if (incoming == null) return;
        boolean changed = !J.text(machine, "state_machine_id").equals(J.text(incoming, "state_machine_id"));
        machine = incoming;
        JSONObject next = payload.optJSONObject("updated_state_ref"); if (next == null) next = payload.optJSONObject("state_ref");
        reference = next != null ? next : changed ? J.o("state_index", 0) : reference;
        revision++;
    }
    synchronized JSONObject activeState() {
        JSONArray states = J.array(machine, "states"); String id = J.text(reference, "state_id"); int index = reference.optInt("state_index", 0);
        if (!id.isEmpty()) for (int i = 0; i < states.length(); i++) if (J.text(states.optJSONObject(i), "state_id").equals(id)) { index = i; break; }
        JSONObject state = states.optJSONObject(index); return state == null ? new JSONObject() : state;
    }
    synchronized JSONObject activeTrack() { JSONObject result = J.array(machine, "tracks").optJSONObject(activeState().optInt("track", 0)); return result == null ? new JSONObject() : result; }
    JSONObject metadata() {
        JSONObject metadata = J.obj(activeTrack(), "metadata"); String uri = J.text(metadata, "uri");
        Song selected = catalog.track(aliases.containsKey(uri) ? aliases.get(uri) : uri);
        StringBuilder artist = new StringBuilder(); JSONArray authors = J.array(metadata, "authors");
        for (int i = 0; i < authors.length(); i++) { String name = J.text(authors.optJSONObject(i), "name"); if (!name.isEmpty()) { if (artist.length() > 0) artist.append(", "); artist.append(name); } }
        String cover = selected == null ? "" : selected.cover; if (cover.isEmpty()) cover = J.text(J.array(metadata, "images").optJSONObject(0), "url");
        return J.o("uri", uri, "requestedUri", aliases.containsKey(uri) ? aliases.get(uri) : J.text(metadata, "linked_from_uri"), "name", J.text(metadata, "name"), "artist", artist.toString(), "cover", cover, "duration", metadata.optLong("duration"));
    }
    synchronized boolean music() { return J.text(J.obj(activeTrack(), "metadata"), "uri").startsWith("spotify:track:"); }
    synchronized boolean restricted(String name) { JSONObject state = activeState(), restrictions = J.obj(state, "restrictions"); return state.optBoolean(name) || restrictions.optBoolean(name) || J.array(restrictions, name + "_reasons").length() > 0; }
    boolean canNext() { return music() && !restricted("disallow_skipping_next"); }
    boolean canSeek() { return music() && !restricted("disallow_seeking"); }
    boolean canPrevious() {
        if (!music()) return false;
        if (!restricted("disallow_skipping_prev")) return true;
        JSONArray reasons = J.array(J.obj(activeState(), "restrictions"), "disallow_skipping_prev_reasons");
        if (reasons.length() == 0) return false;
        for (int i = 0; i < reasons.length(); i++) if (!reasons.optString(i).equals("no_prev_track")) return false;
        return true;
    }
    void initialize() throws Exception {
        if (registered && !closed) return;
        api.session.ensureFresh(api.http); api.resolveEndpoint();
        CountDownLatch latch = new CountDownLatch(1); String[] connection = {null};
        dealer = api.http.newWebSocket(new Request.Builder().url("wss://dealer.spotify.com/?access_token=" + java.net.URLEncoder.encode(J.text(api.session.snapshot(), "bearer").substring(7), "UTF-8")).build(), new WebSocketListener() {
            @Override public void onMessage(WebSocket socket, String text) {
                try {
                    JSONObject message = J.parse(text), headers = J.obj(message, "headers");
                    String id = J.text(headers, "Spotify-Connection-Id"); if (id.isEmpty()) id = J.text(headers, "x-spotify-connection-id");
                    if (!id.isEmpty()) { connection[0] = id; latch.countDown(); }
                    JSONArray payloads = J.array(message, "payloads");
                    for (int i = 0; i < payloads.length(); i++) {
                        Object value = payloads.opt(i);
                        if (value instanceof JSONObject) accept((JSONObject) value);
                        else if (value instanceof String) {
                            String payload = (String) value;
                            if (!payload.trim().startsWith("{")) {
                                byte[] bytes = Base64.getDecoder().decode(payload);
                                payload = J.text(headers, "Transfer-Encoding").equalsIgnoreCase("gzip") ? J.read(new GZIPInputStream(new ByteArrayInputStream(bytes))) : new String(bytes, StandardCharsets.UTF_8);
                            }
                            accept(J.parse(payload));
                        }
                    }
                } catch (Exception ignored) { }
            }
            @Override public void onFailure(WebSocket socket, Throwable error, Response response) { if (!closed) { registered = false; listener.connectionError(); } latch.countDown(); }
        });
        if (!latch.await(12, TimeUnit.SECONDS) || connection[0] == null) throw new IllegalStateException("SpotifyConnectionTimeout");
        JSONObject capabilities = J.o("change_volume", true, "enable_play_token", true, "supports_file_media_type", true, "play_token_lost_behavior", "pause", "disable_connect", false, "audio_podcasts", true, "video_playback", false, "manifest_formats", new JSONArray(java.util.Arrays.asList("file_ids_mp4", "file_ids_mp4_dual")), "supports_preferred_media_type", true, "supports_playback_offsets", true, "supports_playback_speed", true);
        JSONObject info = J.o("brand", "spotify", "capabilities", capabilities, "device_type", "computer", "metadata", new JSONObject(), "model", "web_player", "is_group", false, "is_public", true, "device_id", device, "name", "YSpotify Android", "platform_identifier", "web_player android;chrome;mobile", "client_version", "harmony:4.80.0-3cdf02c91");
        JSONObject result = api.player("/track-playback/v1/devices", "POST", J.o("device", info, "outro_endcontent_snooping", false, "volume", 65535, "client_version", "harmony:4.80.0-3cdf02c91", "connection_id", connection[0]));
        sequence = result.optLong("initial_seq_num"); registered = true;
        try {
            Request.Builder request = new Request.Builder().url(api.endpoint + "/widevine-license/v1/application-certificate");
            for (Map.Entry<String, String> header : api.session.headers().entrySet()) request.header(header.getKey(), header.getValue());
            try (Response response = api.http.newCall(request.build()).execute()) { if (response.isSuccessful() && response.body() != null) certificate = response.body().bytes(); }
        } catch (Exception ignored) { }
    }
    private JSONObject commandBody(String action) { return J.o("endpoint", action, "options", new JSONObject(), "logging_params", J.o("page_instance_ids", new JSONArray(), "interaction_ids", new JSONArray(), "command_id", UUID.randomUUID().toString().replace("-", ""))); }
    private JSONObject send(JSONObject command) throws Exception { return api.player("/connect-state/v1/player/command/from/" + device + "/to/" + device, "POST", J.o("command", command)); }
    void play(String uri, String context, long expectedGeneration) throws Exception {
        if (generation.get() != expectedGeneration) return; initialize();
        if (closed || generation.get() != expectedGeneration) return;
        commanding = true;
        try {
            long before; synchronized (this) { before = revision; }
            String source = context != null && context.matches("spotify:(playlist:[a-zA-Z0-9]{22}|collection:tracks)") ? context : uri;
            JSONObject command = commandBody("play"); command.put("context", J.o("uri", source, "url", "context://" + source, "metadata", new JSONObject()));
            command.put("play_origin", J.o("feature_identifier", source.startsWith("spotify:playlist:") ? "playlist" : "search", "referrer_identifier", "yspotify"));
            if (!source.equals(uri)) command.put("options", J.o("skip_to", J.o("track_uri", uri)));
            accept(send(command));
            Song expected = catalog.track(uri);
            boolean matched = false;
            for (int i = 0; i < 150 && generation.get() == expectedGeneration; i++) {
                JSONObject meta = J.obj(activeTrack(), "metadata"); String actual = J.text(meta, "uri"); boolean same = false;
                if (expected != null && normalize(expected.name).equals(normalize(J.text(meta, "name")))) for (int j = 0; j < J.array(meta, "authors").length(); j++) { String author = normalize(J.text(J.array(meta, "authors").optJSONObject(j), "name")); if (!author.isEmpty() && normalize(expected.artist).contains(author)) same = true; }
                synchronized (this) { matched = revision != before && (actual.equals(uri) || J.text(meta, "linked_from_uri").equals(uri) || same || actual.startsWith("spotify:ad:")); }
                if (matched) break; Thread.sleep(20);
            }
            if (generation.get() != expectedGeneration) return;
            if (!matched) throw new IllegalStateException("SpotifyPlaybackTimeout");
            if (music()) { String actual = J.text(J.obj(activeTrack(), "metadata"), "uri"); if (aliases.size() >= 64) aliases.remove(aliases.keySet().iterator().next()); aliases.put(actual, uri); }
            load(expectedGeneration);
        } finally { commanding = false; }
    }
    private String normalize(String value) { return Normalizer.normalize(value, Normalizer.Form.NFKC).trim().toLowerCase(java.util.Locale.ROOT); }
    String resolve(String file) throws Exception {
        CachedUrl cached = urls.get(file); if (cached != null && cached.until > System.currentTimeMillis()) return cached.url;
        JSONObject result = api.player("/storage-resolve/v2/files/audio/interactive/10/" + file + "?version=10000000&product=9&platform=39&alt=json", "GET", null);
        String url = J.array(result, "cdnurl").optString(0); if (!url.startsWith("https://")) throw new IllegalStateException("SpotifyMissingAudio");
        if (urls.size() >= 16) urls.remove(urls.keySet().iterator().next()); urls.put(file, new CachedUrl(url)); return url;
    }
    void load(long expectedGeneration) throws Exception {
        String file = J.text(J.array(J.obj(activeTrack(), "manifest"), "file_ids_mp4").optJSONObject(0), "file_id");
        if (file.isEmpty()) throw new IllegalStateException("SpotifyUnsupportedMedia");
        String url = resolve(file); if (generation.get() != expectedGeneration) return;
        endedState = ""; listener.media(this, metadata(), url, !music(), expectedGeneration);
    }
    void ping() { if (dealer != null) dealer.send("{\"type\":\"ping\"}"); }
    void seek(long position) throws Exception { if (!canSeek()) throw new IllegalStateException("PlaybackRestricted"); JSONObject command = commandBody("seek_to"); command.put("value", position); accept(send(command)); }
    void report() throws Exception {
        if (!registered || commanding || closed || J.array(machine, "states").length() == 0) return;
        Audio audio = listener.audio(); if (audio == null || !audio.ready || !audio.uri.equals(J.text(J.obj(activeTrack(), "metadata"), "uri"))) return;
        long version = generation.get(); JSONObject state = activeState(); String id = J.text(state, "state_id");
        JSONObject reference = J.o("state_machine_id", J.text(machine, "state_machine_id"), "state_id", id, "paused", audio.paused);
        JSONObject response = api.player("/track-playback/v1/devices/" + device + "/state", "PUT", J.o("seq_num", ++sequence, "state_ref", reference, "sub_state", J.o("playback_speed", audio.paused ? 0 : 1, "position", audio.position, "duration", audio.duration, "media_type", "AUDIO", "bitrate", 128000, "audio_quality", "HIGH", "format", 10, "is_video_on", false), "debug_source", audio.ended ? "track_data_finalized" : "playback_progress"));
        if (version != generation.get()) return; String previous = J.text(J.obj(activeTrack(), "metadata"), "uri"); accept(response);
        if (audio.ended && !endedState.equals(id)) {
            endedState = id;
            if (J.text(activeState(), "state_id").equals(id)) { JSONObject transition = J.obj(J.obj(state, "transitions"), "advance"); if (transition.has("state_index")) synchronized (this) { this.reference = J.o("state_index", transition.optInt("state_index")); } }
            String next = J.text(J.obj(activeTrack(), "metadata"), "uri");
            if (!J.text(activeState(), "state_id").equals(id) && !listener.advance(previous, next)) load(version);
        }
    }
    @Override public void close() { closed = true; generation.incrementAndGet(); if (dealer != null) dealer.close(1000, "YSpotify closed"); }
}
