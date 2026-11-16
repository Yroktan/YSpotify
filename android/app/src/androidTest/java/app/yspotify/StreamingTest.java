package app.yspotify;

import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.okhttp.OkHttpDataSource;
import androidx.media3.exoplayer.DefaultLoadControl;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import okhttp3.OkHttpClient;
import okhttp3.mockwebserver.MockResponse;
import okhttp3.mockwebserver.MockWebServer;
import okhttp3.tls.HandshakeCertificates;
import okhttp3.tls.HeldCertificate;
import okio.Buffer;
import org.junit.Test;
import org.junit.runner.RunWith;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;
import static org.junit.Assert.*;

@UnstableApi
@RunWith(AndroidJUnit4.class)
public class StreamingTest {
    @Test public void continuousAudioOutlivesMetadataRequestTimeout() throws Exception {
        HeldCertificate certificate = new HeldCertificate.Builder().commonName("localhost").addSubjectAlternativeName("localhost").build();
        HandshakeCertificates serverTls = new HandshakeCertificates.Builder().heldCertificate(certificate).build();
        HandshakeCertificates clientTls = new HandshakeCertificates.Builder().addTrustedCertificate(certificate.certificate()).build();
        OkHttpClient client = new OkHttpClient.Builder().sslSocketFactory(clientTls.sslSocketFactory(), clientTls.trustManager()).callTimeout(20, TimeUnit.SECONDS).build();
        android.app.Instrumentation instrumentation = InstrumentationRegistry.getInstrumentation();
        AtomicReference<PlaybackException> error = new AtomicReference<>(); AtomicLong position = new AtomicLong(); ExoPlayer[] player = {null};
        try (MockWebServer server = new MockWebServer()) {
            server.useHttps(serverTls.sslSocketFactory(), false);
            server.enqueue(new MockResponse().setHeader("Content-Type", "audio/wav").setBody(new Buffer().write(wave())).throttleBody(8000, 500, TimeUnit.MILLISECONDS)); server.start();
            String stream = server.url("/fixture.wav").toString();
            instrumentation.runOnMainSync(() -> {
                player[0] = new ExoPlayer.Builder(instrumentation.getTargetContext()).setMediaSourceFactory(new DefaultMediaSourceFactory(new OkHttpDataSource.Factory(SpotifyApi.streaming(client))))
                        .setLoadControl(new DefaultLoadControl.Builder().setBufferDurationsMs(5000, 30000, 300, 1000).build()).build();
                player[0].setVolume(0); player[0].addListener(new Player.Listener() { @Override public void onPlayerError(PlaybackException failure) { error.set(failure); } });
                player[0].setMediaItem(MediaItem.fromUri(stream)); player[0].prepare(); player[0].play();
            });
            try {
                long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(40);
                while (position.get() < 23000 && error.get() == null && System.nanoTime() < deadline) { Thread.sleep(300); instrumentation.runOnMainSync(() -> position.set(player[0].getCurrentPosition())); }
                assertNull(error.get()); assertTrue("Audio stopped at " + position.get() + "ms", position.get() >= 23000);
            } finally { instrumentation.runOnMainSync(() -> player[0].release()); }
        } finally { client.dispatcher().executorService().shutdownNow(); client.connectionPool().evictAll(); }
    }
    private byte[] wave() {
        int rate = 8000, samples = rate * 60, bytes = samples * 2;
        ByteBuffer audio = ByteBuffer.allocate(44 + bytes).order(ByteOrder.LITTLE_ENDIAN);
        audio.put(new byte[]{'R','I','F','F'}).putInt(36 + bytes).put(new byte[]{'W','A','V','E','f','m','t',' '}).putInt(16).putShort((short) 1).putShort((short) 1).putInt(rate).putInt(rate * 2).putShort((short) 2).putShort((short) 16).put(new byte[]{'d','a','t','a'}).putInt(bytes);
        for (int sample = 0; sample < samples; sample++) audio.putShort((short) (2000 * Math.sin(2 * Math.PI * 440 * sample / rate)));
        return audio.array();
    }
}
