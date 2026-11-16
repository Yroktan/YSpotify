package app.yspotify;

import android.app.Activity;
import android.graphics.Color;
import android.os.Bundle;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

public final class LoginActivity extends Activity {
    private AuthCapture capture;
    @Override protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        LinearLayout root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL); root.setBackgroundColor(0xff101a14);
        root.setOnApplyWindowInsetsListener((view, insets) -> { view.setPadding(0, insets.getSystemWindowInsetTop(), 0, insets.getSystemWindowInsetBottom()); return insets; });
        LinearLayout bar = new LinearLayout(this); bar.setPadding(dp(16), dp(8), dp(8), dp(8)); bar.setGravity(android.view.Gravity.CENTER_VERTICAL);
        TextView title = new TextView(this); title.setText("Вход в Spotify"); title.setTextColor(Color.WHITE); title.setTextSize(19); title.setGravity(android.view.Gravity.CENTER_VERTICAL); bar.addView(title, new LinearLayout.LayoutParams(0, dp(48), 1));
        Button done = new Button(this); done.setText("Я вошёл"); done.setTextColor(0xff132b1b); done.setBackgroundTintList(android.content.res.ColorStateList.valueOf(0xff91dba8)); bar.addView(done); root.addView(bar);
        TextView message = new TextView(this); message.setText("Войди на официальной странице Spotify. Вход сохранится на этом устройстве."); message.setTextColor(0xff9db5a5); message.setPadding(dp(16), 0, dp(16), dp(10)); root.addView(message);
        capture = new AuthCapture(this, new SessionStore(this).snapshot(), session -> {
            try { new SessionStore(this).save(session); setResult(RESULT_OK); finish(); }
            catch (Exception failure) { message.setText("Не удалось сохранить вход. Попробуй ещё раз."); }
        }, failure -> message.setText("Не удалось завершить вход. Попробуй ещё раз."));
        done.setOnClickListener(view -> { message.setText("Проверяю вход…"); capture.validate(); });
        root.addView(capture.web, new LinearLayout.LayoutParams(-1, 0, 1)); setContentView(root); capture.start();
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    @Override protected void onDestroy() { if (capture != null) capture.close(); super.onDestroy(); }
}
