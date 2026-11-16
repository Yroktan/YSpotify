package app.yspotify;

import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.graphics.Bitmap;
import android.graphics.Rect;
import android.view.View;
import android.widget.ListView;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import org.junit.runner.RunWith;
import java.io.File;
import java.io.FileOutputStream;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
@androidx.media3.common.util.UnstableApi
public class PhoneLayoutTest {
    @Test public void portraitLandscapeAndPlayerStayInsideTheWindow() throws Exception {
        android.app.Instrumentation instrumentation = InstrumentationRegistry.getInstrumentation();
        Intent intent = new Intent(instrumentation.getTargetContext(), MainActivity.class).putExtra("preview", true).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(intent)) {
            instrumentation.waitForIdleSync();
            scenario.onActivity(activity -> { activity.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_PORTRAIT); }); Thread.sleep(1000); instrumentation.waitForIdleSync();
            scenario.onActivity(this::assertLayout); screenshot("portrait");
            scenario.onActivity(activity -> { assertTrue(((View) activity.findViewById(R.id.current_title).getParent()).performClick()); }); Thread.sleep(400); instrumentation.waitForIdleSync(); screenshot("player");
            instrumentation.getUiAutomation().performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK); Thread.sleep(300);
            scenario.onActivity(activity -> activity.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE)); Thread.sleep(1000); instrumentation.waitForIdleSync(); scenario.onActivity(this::assertLayout); screenshot("landscape");
        }
    }
    private void assertLayout(MainActivity activity) {
        View root = activity.getWindow().getDecorView(); Rect window = new Rect(); assertTrue(root.getGlobalVisibleRect(window));
        View play = activity.findViewById(R.id.play_button), previous = activity.findViewById(R.id.previous_button), next = activity.findViewById(R.id.next_button); ListView songs = activity.findViewById(R.id.song_list);
        assertEquals(10, songs.getCount()); assertTrue(songs.getHeight() > 40); Rect controls = new Rect(); assertTrue(play.getGlobalVisibleRect(controls)); assertTrue(window.contains(controls));
        for (View control : new View[]{play, previous, next, activity.findViewById(R.id.tab_search), activity.findViewById(R.id.tab_liked), activity.findViewById(R.id.tab_library)}) { int[] point = new int[2]; control.getLocationOnScreen(point); assertTrue(point[0] >= window.left); assertTrue(point[0] + control.getWidth() <= window.right); assertTrue(point[1] + control.getHeight() <= window.bottom); }
        int[] listPosition = new int[2], playPosition = new int[2]; songs.getLocationOnScreen(listPosition); play.getLocationOnScreen(playPosition); assertTrue(listPosition[1] + songs.getHeight() <= playPosition[1]);
        songs.setSelection(9);
    }
    private void screenshot(String name) throws Exception {
        android.app.Instrumentation instrumentation = InstrumentationRegistry.getInstrumentation();
        Bitmap image = instrumentation.getUiAutomation().takeScreenshot(); assertNotNull(image);
        String suffix = instrumentation.getTargetContext().getResources().getConfiguration().fontScale + "_" + image.getWidth();
        String directoryPath = InstrumentationRegistry.getArguments().getString("additionalTestOutputDir");
        File directory = directoryPath == null ? instrumentation.getTargetContext().getExternalFilesDir("screenshots") : new File(directoryPath, "screenshots"); assertNotNull(directory); directory.mkdirs();
        try (FileOutputStream output = new FileOutputStream(new File(directory, name + '_' + suffix + ".png"))) { assertTrue(image.compress(Bitmap.CompressFormat.PNG, 100, output)); }
        image.recycle();
    }
}
