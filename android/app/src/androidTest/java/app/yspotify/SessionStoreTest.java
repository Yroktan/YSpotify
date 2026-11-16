package app.yspotify;

import android.content.Context;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.After;
import org.junit.Test;
import org.junit.runner.RunWith;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
public class SessionStoreTest {
    private final Context context = InstrumentationRegistry.getInstrumentation().getTargetContext();
    private org.json.JSONObject fixture() { return J.o("bearer", "Bearer yspotify-instrumentation-fixture", "client", "instrumentation-fixture", "cookie", "", "validUntil", 0); }
    @After public void cleanup() { new SessionStore(context).clear(); }
    @Test public void encryptedSessionSurvivesRecreationAndLogout() throws Exception {
        SessionStore store = new SessionStore(context); store.save(fixture());
        byte[] disk = Files.readAllBytes(new File(context.getFilesDir(), "session.enc").toPath());
        assertFalse(new String(disk, StandardCharsets.ISO_8859_1).contains("yspotify-instrumentation-fixture"));
        SessionStore restored = new SessionStore(context); assertTrue(restored.exists()); assertEquals("Bearer yspotify-instrumentation-fixture", J.text(restored.snapshot(), "bearer"));
        restored.clear(); assertFalse(new SessionStore(context).exists());
    }
    @Test public void corruptSessionRequiresLogin() throws Exception {
        SessionStore store = new SessionStore(context); store.save(fixture()); File file = new File(context.getFilesDir(), "session.enc"); byte[] bytes = Files.readAllBytes(file.toPath()); bytes[bytes.length - 1] ^= 1; Files.write(file.toPath(), bytes);
        assertFalse(new SessionStore(context).exists());
    }
    @Test public void pendingRefreshCannotRestoreLoggedOutAccount() throws Exception {
        SessionStore store = new SessionStore(context); store.save(fixture()); CountDownLatch entered = new CountDownLatch(1), finish = new CountDownLatch(1);
        store.fallback = previous -> { entered.countDown(); assertTrue(finish.await(5, TimeUnit.SECONDS)); return fixture(); };
        ExecutorService worker = Executors.newSingleThreadExecutor();
        try {
            Future<Boolean> result = worker.submit(() -> { try { store.refresh(new okhttp3.OkHttpClient()); return false; } catch (IllegalStateException expected) { return "SessionChanged".equals(expected.getMessage()); } });
            assertTrue(entered.await(5, TimeUnit.SECONDS)); store.clear(); finish.countDown(); assertTrue(result.get(5, TimeUnit.SECONDS)); assertFalse(store.exists()); assertFalse(new SessionStore(context).exists());
        } finally { finish.countDown(); worker.shutdownNow(); }
    }
}
