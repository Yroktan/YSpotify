package app.yspotify;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.widget.ListView;
import android.widget.TextView;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import org.junit.runner.RunWith;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
@androidx.media3.common.util.UnstableApi
public class StartupTest {
    @Test public void nativePlayerStartsAndLoginOpensAfterRotation() throws Exception {
        android.app.Instrumentation instrumentation = InstrumentationRegistry.getInstrumentation();
        new SessionStore(instrumentation.getTargetContext()).clear();
        try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(new Intent(instrumentation.getTargetContext(), MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))) {
            Thread.sleep(800); instrumentation.waitForIdleSync();
            scenario.onActivity(activity -> {
                assertEquals(0, ((ListView) activity.findViewById(R.id.song_list)).getCount());
                assertFalse(activity.findViewById(R.id.play_button).isEnabled());
                assertTrue(((TextView) activity.findViewById(R.id.status_text)).getText().toString().isEmpty());
                activity.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE);
            });
            Thread.sleep(800); instrumentation.waitForIdleSync();
            android.app.Instrumentation.ActivityMonitor monitor = instrumentation.addMonitor(LoginActivity.class.getName(), null, false);
            Activity login = null;
            try {
                scenario.onActivity(activity -> assertTrue(activity.findViewById(R.id.account_button).performClick()));
                login = instrumentation.waitForMonitorWithTimeout(monitor, 5000); assertNotNull(login);
            } finally {
                if (login != null) { Activity opened = login; instrumentation.runOnMainSync(opened::finish); }
                instrumentation.removeMonitor(monitor);
            }
        }
    }
}
