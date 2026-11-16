package app.yspotify;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.media.MediaMetadata;
import android.media.session.MediaSession;
import android.media.session.PlaybackState;
import android.os.Binder;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import androidx.media3.common.AudioAttributes;
import androidx.media3.common.C;
import androidx.media3.common.MediaItem;
import androidx.media3.common.MimeTypes;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.okhttp.OkHttpDataSource;
import androidx.media3.exoplayer.DefaultLoadControl;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.exoplayer.drm.DefaultDrmSessionManager;
import androidx.media3.exoplayer.drm.DrmSessionManager;
import androidx.media3.exoplayer.drm.FrameworkMediaDrm;
import androidx.media3.exoplayer.drm.HttpMediaDrmCallback;
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

@UnstableApi
public final class PlaybackService extends Service implements SpotifyEngine.Listener {
    public final class LocalBinder extends Binder { PlaybackService service() { return PlaybackService.this; } }
    interface Listener { void changed(); }
    private final LocalBinder binder = new LocalBinder();
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ScheduledExecutorService network = Executors.newSingleThreadScheduledExecutor();
    private final List<Listener> listeners = new CopyOnWriteArrayList<>();
    private ExoPlayer player;
    private MediaSession mediaSession;
    SpotifyApi api;
    Catalog catalog;
    private volatile SpotifyEngine engine;
    private volatile SpotifyEngine.Audio audio = new SpotifyEngine.Audio("", 0, 0, true, false, false);
    private volatile JSONObject track = new JSONObject();
    private volatile List<Song> queue = new ArrayList<>();
    private volatile List<Integer> order = new ArrayList<>();
    private volatile int queueIndex = -1;
    private volatile String context, error = "", repeat = "off";
    private volatile boolean shuffle, busy, foreground, queueStarted, destroyed;
    private String pendingUri = "";
    private float volume;
    private long clickedAt, startedAfterMs;
    private final Runnable position = new Runnable() { @Override public void run() { updateAudio(); changed(); main.postDelayed(this, player != null && player.isPlaying() ? 500 : 2000); } };
    @Override public void onCreate() {
        super.onCreate(); api = new SpotifyApi(this);
        api.session.fallback = previous -> {
            java.util.concurrent.CompletableFuture<JSONObject> pending = new java.util.concurrent.CompletableFuture<>(); AuthCapture[] capture = {null};
            main.post(() -> { capture[0] = new AuthCapture(this, previous, pending::complete, pending::completeExceptionally); capture[0].timeout(18000); capture[0].start(); });
            try { return pending.get(20, TimeUnit.SECONDS); } finally { main.post(() -> { if (capture[0] != null) capture[0].close(); }); }
        };
        catalog = new Catalog(this, api); engine = new SpotifyEngine(api, catalog, this);
        NotificationManager notifications = getSystemService(NotificationManager.class);
        notifications.createNotificationChannel(new NotificationChannel("playback", "Воспроизведение", NotificationManager.IMPORTANCE_LOW));
        volume = getSharedPreferences("player", MODE_PRIVATE).getFloat("volume", .8f);
        OkHttpDataSource.Factory data = new OkHttpDataSource.Factory(api.http);
        DefaultMediaSourceFactory media = new DefaultMediaSourceFactory(data);
        media.setDrmSessionManagerProvider(item -> {
            MediaItem.DrmConfiguration configuration = item.localConfiguration == null ? null : item.localConfiguration.drmConfiguration;
            if (configuration == null) return DrmSessionManager.DRM_UNSUPPORTED;
            HttpMediaDrmCallback callback = new HttpMediaDrmCallback(configuration.licenseUri.toString(), data);
            for (java.util.Map.Entry<String, String> header : configuration.licenseRequestHeaders.entrySet()) callback.setKeyRequestProperty(header.getKey(), header.getValue());
            return new DefaultDrmSessionManager.Builder().setUuidAndExoMediaDrmProvider(C.WIDEVINE_UUID, uuid -> {
                try {
                    FrameworkMediaDrm drm = FrameworkMediaDrm.newInstance(uuid);
                    if (engine.certificate != null) try { drm.setPropertyByteArray("serviceCertificate", engine.certificate); drm.setPropertyString("privacyMode", "enable"); } catch (Exception ignored) { }
                    return drm;
                } catch (Exception failure) { return FrameworkMediaDrm.DEFAULT_PROVIDER.acquireExoMediaDrm(uuid); }
            }).setPlayClearSamplesWithoutKeys(false).build(callback);
        });
        player = new ExoPlayer.Builder(this).setMediaSourceFactory(media)
                .setLoadControl(new DefaultLoadControl.Builder().setBufferDurationsMs(5000, 30000, 300, 1000).setTargetBufferBytes(512 * 1024).setPrioritizeTimeOverSizeThresholds(true).build()).build();
        player.setAudioAttributes(new AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MUSIC).build(), true);
        player.setHandleAudioBecomingNoisy(true); player.setWakeMode(C.WAKE_MODE_LOCAL); player.setVolume(volume);
        player.addListener(new Player.Listener() {
            @Override public void onIsPlayingChanged(boolean playing) { if (playing) { if (busy) startedAfterMs = System.currentTimeMillis() - clickedAt; busy = false; error = ""; } updateAudio(); updateNotification(); changed(); }
            @Override public void onPlaybackStateChanged(int state) { updateAudio(); if (state == Player.STATE_ENDED) report(); updateNotification(); changed(); }
            @Override public void onPlayerError(PlaybackException failure) { busy = false; error = failure.errorCode >= 6000 && failure.errorCode < 7000 ? "Spotify отклонил воспроизведение на этом устройстве." : "Не удалось воспроизвести трек. Попробуй ещё раз."; updateAudio(); updateNotification(); changed(); }
        });
        mediaSession = new MediaSession(this, "YSpotify"); mediaSession.setActive(true);
        mediaSession.setCallback(new MediaSession.Callback() {
            @Override public void onPlay() { if (!player.isPlaying()) toggle(); }
            @Override public void onPause() { if (player.getPlayWhenReady()) toggle(); }
            @Override public void onSkipToNext() { next(); }
            @Override public void onSkipToPrevious() { previous(); }
            @Override public void onSeekTo(long at) { seek(at); }
            @Override public void onStop() { stopPlayback(); }
        });
        main.post(position);
        network.scheduleWithFixedDelay(() -> { try { engine.report(); } catch (Exception failure) { showError(failure); } }, 5, 5, TimeUnit.SECONDS);
        network.scheduleWithFixedDelay(() -> engine.ping(), 20, 20, TimeUnit.SECONDS);
        if (api.session.exists()) network.execute(() -> { try { engine.initialize(); } catch (Exception failure) { showError(failure); } });
    }
    @Override public IBinder onBind(Intent intent) { return binder; }
    @Override public int onStartCommand(Intent intent, int flags, int id) {
        ensureForeground();
        if (intent != null) {
            String action = intent.getAction();
            if ("toggle".equals(action)) toggle(); else if ("next".equals(action)) next(); else if ("previous".equals(action)) previous(); else if ("stop".equals(action)) stopPlayback();
        }
        return START_NOT_STICKY;
    }
    void addListener(Listener listener) { if (!listeners.contains(listener)) listeners.add(listener); listener.changed(); }
    void removeListener(Listener listener) { listeners.remove(listener); }
    private void changed() { if (destroyed) return; if (Looper.myLooper() != Looper.getMainLooper()) { main.post(this::changed); return; } for (Listener listener : listeners) listener.changed(); }
    JSONObject snapshot() { return J.o("track", J.parse(track.toString()), "playing", player.isPlaying(), "busy", busy, "pendingUri", pendingUri, "error", error, "position", audio.position, "duration", audio.duration, "volume", volume, "canNext", engine.canNext() && nextIndex(1) >= 0, "canPrevious", engine.canPrevious(), "canSeek", engine.canSeek(), "shuffle", shuffle, "repeat", repeat, "loggedIn", api.session.exists(), "startedAfterMs", startedAfterMs); }
    void setVolume(float value) { volume = Math.max(0, Math.min(1, value)); player.setVolume(volume); getSharedPreferences("player", MODE_PRIVATE).edit().putFloat("volume", volume).apply(); changed(); }
    void toggle() { if (player.getMediaItemCount() == 0) return; engine.generation.incrementAndGet(); if (player.getPlayWhenReady()) player.pause(); else { ensureForeground(); player.play(); } updateAudio(); updateNotification(); changed(); report(); }
    void seek(long at) { if (!engine.canSeek() || audio.duration <= 0) return; long target = Math.max(0, Math.min(audio.duration - 100, at)); player.seekTo(target); updateAudio(); network.execute(() -> { try { engine.seek(target); } catch (Exception failure) { showError(failure); } }); changed(); }
    void play(Song song, List<Song> selected, String source) {
        if (!song.uri.startsWith("spotify:track:")) return;
        queue = new ArrayList<>(selected); queueIndex = -1; for (int i = 0; i < queue.size(); i++) if (queue.get(i).uri.equals(song.uri) && queue.get(i).uid.equals(song.uid)) { queueIndex = i; break; }
        context = source; arrange(); queueStarted = false; start(song.uri);
    }
    private void start(String uri) {
        ensureForeground(); clickedAt = System.currentTimeMillis(); startedAfterMs = 0; busy = true; pendingUri = uri; error = "";
        if (player.getPlaybackState() == Player.STATE_BUFFERING) player.stop();
        SpotifyEngine origin = engine; long version = origin.generation.incrementAndGet(); String source = context; changed();
        network.execute(() -> { try { if (engine == origin) origin.play(uri, source, version); } catch (Exception failure) { if (engine == origin && origin.generation.get() == version) showError(failure); } });
    }
    private void arrange() { List<Integer> arranged = new ArrayList<>(); for (int i = 0; i < queue.size(); i++) arranged.add(i); if (shuffle) { Collections.shuffle(arranged); arranged.remove(Integer.valueOf(queueIndex)); if (queueIndex >= 0) arranged.add(0, queueIndex); } order = arranged; }
    private int nextIndex(int direction) { if (queueIndex < 0 || order.isEmpty()) return -1; int index = order.indexOf(queueIndex) + direction; if (index < 0 || index >= order.size()) { if (!repeat.equals("context")) return -1; index = (index + order.size()) % order.size(); } return order.get(index); }
    void next() { if (!engine.canNext()) return; int index = nextIndex(1); if (index >= 0) { queueIndex = index; queueStarted = false; start(queue.get(index).uri); } }
    void previous() { if (!engine.canPrevious()) return; if (audio.position > 3000 && engine.canSeek()) { seek(0); return; } int index = nextIndex(-1); if (index >= 0) { queueIndex = index; queueStarted = false; start(queue.get(index).uri); } else if (engine.canSeek()) seek(0); }
    void shuffle() { if (!engine.music()) return; shuffle = !shuffle; arrange(); changed(); }
    void repeat() { if (!engine.music()) return; repeat = repeat.equals("off") ? "context" : repeat.equals("context") ? "track" : "off"; changed(); }
    @Override public void media(SpotifyEngine origin, JSONObject metadata, String url, boolean ad, long version) {
        main.post(() -> {
            if (destroyed || engine != origin || engine.generation.get() != version) return;
            track = J.parse(metadata.toString()); String requested = J.text(metadata, "requestedUri");
            queueStarted = queueIndex >= 0 && (queue.get(queueIndex).uri.equals(requested) || queue.get(queueIndex).uri.equals(J.text(metadata, "uri")));
            MediaItem.Builder item = new MediaItem.Builder().setMediaId(J.text(metadata, "uri")).setUri(url).setMimeType(MimeTypes.AUDIO_MP4);
            if (!ad) item.setDrmConfiguration(new MediaItem.DrmConfiguration.Builder(C.WIDEVINE_UUID).setLicenseUri(api.endpoint + "/widevine-license/v1/audio/license").setLicenseRequestHeaders(api.session.headers()).setMultiSession(false).setPlayClearContentWithoutKey(false).build());
            player.setMediaItem(item.build()); player.prepare(); player.play(); updateNotification(); changed();
        });
    }
    @Override public SpotifyEngine.Audio audio() { return audio; }
    private void updateAudio() { if (player == null) return; long duration = player.getDuration(); if (duration == C.TIME_UNSET || duration < 0) duration = track.optLong("duration"); audio = new SpotifyEngine.Audio(J.text(track, "uri"), Math.max(0, player.getCurrentPosition()), duration, !player.getPlayWhenReady(), player.getPlaybackState() == Player.STATE_ENDED, player.getPlaybackState() == Player.STATE_READY || player.getPlaybackState() == Player.STATE_ENDED); }
    private void report() { network.execute(() -> { try { engine.report(); } catch (Exception failure) { showError(failure); } }); }
    @Override public boolean advance(String previous, String next) {
        if (!next.startsWith("spotify:track:") || queueIndex < 0) return false;
        int candidate = queueIndex;
        if (previous.startsWith("spotify:track:") && queueStarted && !repeat.equals("track")) candidate = nextIndex(1);
        if (candidate >= 0 && candidate < queue.size() && queue.get(candidate).uri.equals(next)) { queueIndex = candidate; return false; }
        main.post(() -> {
            int index = queueIndex;
            if (previous.startsWith("spotify:track:") && queueStarted && !repeat.equals("track")) index = nextIndex(1);
            if (index < 0) { stopPlayback(); return; }
            queueIndex = index; queueStarted = false; start(queue.get(index).uri);
        });
        return true;
    }
    @Override public void connectionError() { main.post(() -> { error = "Соединение с Spotify прервано. Проверь сеть."; changed(); }); }
    private void showError(Exception failure) { main.post(() -> { if (destroyed) return; busy = false; error = SpotifyApi.message(failure); changed(); updateNotification(); }); }
    void sessionChanged() { engine.close(); catalog = new Catalog(this, api); engine = new SpotifyEngine(api, catalog, this); network.execute(() -> { try { engine.initialize(); } catch (Exception failure) { showError(failure); } main.post(this::changed); }); }
    void logout() { stopPlayback(); engine.close(); api.session.clear(); catalog = new Catalog(this, api); engine = new SpotifyEngine(api, catalog, this); queue = new ArrayList<>(); order = new ArrayList<>(); queueIndex = -1; track = new JSONObject(); player.clearMediaItems(); updateAudio(); mediaSession.setMetadata(new MediaMetadata.Builder().build()); mediaSession.setPlaybackState(new PlaybackState.Builder().setState(PlaybackState.STATE_STOPPED, 0, 0).build()); changed(); }
    private PendingIntent action(String action) { return PendingIntent.getService(this, action.hashCode(), new Intent(this, PlaybackService.class).setAction(action), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT); }
    private Notification notification() {
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this, "playback").setSmallIcon(R.drawable.ic_notification).setContentTitle(J.text(track, "name").isEmpty() ? "YSpotify" : J.text(track, "name")).setContentText(busy ? "Открываю трек…" : J.text(track, "artist"))
                .setContentIntent(open).setVisibility(Notification.VISIBILITY_PUBLIC).setOnlyAlertOnce(true).setOngoing(player.getPlayWhenReady())
                .addAction(new Notification.Action.Builder(android.R.drawable.ic_media_previous, "Назад", action("previous")).build())
                .addAction(new Notification.Action.Builder(player.getPlayWhenReady() ? android.R.drawable.ic_media_pause : android.R.drawable.ic_media_play, player.getPlayWhenReady() ? "Пауза" : "Слушать", action("toggle")).build())
                .addAction(new Notification.Action.Builder(android.R.drawable.ic_media_next, "Дальше", action("next")).build())
                .setStyle(new Notification.MediaStyle().setMediaSession(mediaSession.getSessionToken()).setShowActionsInCompactView(0, 1, 2)).setDeleteIntent(action("stop")).build();
    }
    private void ensureForeground() { if (!foreground) { startForeground(1, notification()); foreground = true; } }
    @android.annotation.SuppressLint("NotificationPermission")
    private void updateNotification() {
        if (destroyed || mediaSession == null || player == null) return;
        int state = busy ? PlaybackState.STATE_BUFFERING : player.isPlaying() ? PlaybackState.STATE_PLAYING : PlaybackState.STATE_PAUSED;
        mediaSession.setPlaybackState(new PlaybackState.Builder().setActions(PlaybackState.ACTION_PLAY | PlaybackState.ACTION_PAUSE | PlaybackState.ACTION_PLAY_PAUSE | PlaybackState.ACTION_SKIP_TO_NEXT | PlaybackState.ACTION_SKIP_TO_PREVIOUS | PlaybackState.ACTION_SEEK_TO | PlaybackState.ACTION_STOP).setState(state, player.getCurrentPosition(), player.isPlaying() ? 1 : 0).build());
        mediaSession.setMetadata(new MediaMetadata.Builder().putString(MediaMetadata.METADATA_KEY_TITLE, J.text(track, "name")).putString(MediaMetadata.METADATA_KEY_ARTIST, J.text(track, "artist")).putLong(MediaMetadata.METADATA_KEY_DURATION, audio.duration).build());
        if (foreground) getSystemService(NotificationManager.class).notify(1, notification());
    }
    private void stopPlayback() { engine.generation.incrementAndGet(); player.stop(); busy = false; updateAudio(); stopForeground(STOP_FOREGROUND_REMOVE); foreground = false; changed(); }
    @Override public void onTaskRemoved(Intent rootIntent) { if (!player.getPlayWhenReady()) stopSelf(); super.onTaskRemoved(rootIntent); }
    @Override public void onDestroy() { destroyed = true; main.removeCallbacksAndMessages(null); network.shutdownNow(); engine.close(); player.release(); mediaSession.release(); listeners.clear(); super.onDestroy(); }
}
