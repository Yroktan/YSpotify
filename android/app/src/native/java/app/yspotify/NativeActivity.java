package app.yspotify;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.TextView;
import android.widget.Toast;
import java.util.ArrayList;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

public final class NativeActivity extends Activity {
    private static final String SPOTIFY = "com.spotify.music";
    private static final Pattern LINK = Pattern.compile(
        "(?<![A-Za-z0-9@/])(?:spotify:|https://open\\.spotify\\.com/(?:intl-[a-z-]+/)?)(track|playlist|album|artist)[:/]([A-Za-z0-9]{22})(?![A-Za-z0-9/_-])");
    private static final int MAX_LINKS = 500;
    private final ArrayList<Bookmark> bookmarks = new ArrayList<>();
    private final ArrayList<Bookmark> visible = new ArrayList<>();
    private ArrayAdapter<Bookmark> adapter;
    private EditText filter;
    private TextView empty;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        load();
        build();
        if (state == null) receive(getIntent());
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        receive(intent);
    }

    private void build() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setFocusableInTouchMode(true);
        root.setBackgroundColor(0xff101a14);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                android.graphics.Insets ime = insets.getInsets(WindowInsets.Type.ime());
                view.setPadding(bars.left + dp(16), bars.top + dp(12), bars.right + dp(16), Math.max(bars.bottom, ime.bottom) + dp(12));
            } else {
                view.setPadding(insets.getSystemWindowInsetLeft() + dp(16), insets.getSystemWindowInsetTop() + dp(12),
                    insets.getSystemWindowInsetRight() + dp(16), insets.getSystemWindowInsetBottom() + dp(12));
            }
            return insets;
        });

        TextView title = new TextView(this);
        title.setText("YSpotify");
        title.setTextSize(27);
        title.setTextColor(0xff91dba8);
        root.addView(title);

        LinearLayout actions = new LinearLayout(this);
        actions.setGravity(Gravity.CENTER_VERTICAL);
        Button open = button("Открыть Spotify");
        open.setOnClickListener(view -> openSpotify(null));
        Button add = button("Добавить ссылку");
        add.setOnClickListener(view -> edit(null, ""));
        actions.addView(open, new LinearLayout.LayoutParams(0, -2, 1));
        actions.addView(add, new LinearLayout.LayoutParams(0, -2, 1));
        root.addView(actions);

        filter = new EditText(this);
        filter.setId(android.R.id.input);
        filter.setHint("Поиск по сохранённому");
        filter.setSingleLine(true);
        root.addView(filter);

        ListView list = new ListView(this);
        list.setId(android.R.id.list);
        adapter = new ArrayAdapter<>(this, android.R.layout.simple_list_item_1, visible);
        list.setAdapter(adapter);
        list.setOnItemClickListener((parent, item, position, id) -> openSpotify(visible.get(position)));
        list.setOnItemLongClickListener((parent, item, position, id) -> {
            Bookmark bookmark = visible.get(position);
            new AlertDialog.Builder(this).setTitle(bookmark.toString())
                .setItems(new String[]{"Поделиться", "Переименовать", "Удалить"}, (dialog, action) -> {
                    if (action == 0) share(bookmark);
                    else if (action == 1) edit(bookmark, bookmark.url());
                    else if (action == 2) {
                        bookmarks.remove(bookmark);
                        save();
                        refresh();
                    }
                }).show();
            return true;
        });
        root.addView(list, new LinearLayout.LayoutParams(-1, 0, 1));
        empty = new TextView(this);
        empty.setGravity(Gravity.CENTER);
        empty.setTextSize(15);
        empty.setTextColor(0xff96af9f);
        root.addView(empty);
        filter.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence text, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence text, int start, int before, int count) { refresh(); }
            @Override public void afterTextChanged(Editable text) { }
        });
        setContentView(root);
        root.requestFocus();
        refresh();
    }

    private void receive(Intent intent) {
        if (!Intent.ACTION_SEND.equals(intent.getAction())) return;
        CharSequence text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT);
        String supplied = text == null ? "" : text.subSequence(0, Math.min(text.length(), 8192)).toString();
        Bookmark bookmark = parse(supplied, intent.getStringExtra(Intent.EXTRA_SUBJECT));
        if (bookmark == null) {
            Toast.makeText(this, "В сообщении нет ссылки на музыку Spotify", Toast.LENGTH_LONG).show();
        } else {
            showEditor(null, bookmark.url(), bookmark.name);
        }
    }

    private void edit(Bookmark previous, String url) {
        showEditor(previous, url, previous == null ? "" : previous.name);
    }

    private void showEditor(Bookmark previous, String url, String name) {
        LinearLayout fields = new LinearLayout(this);
        fields.setOrientation(LinearLayout.VERTICAL);
        fields.setPadding(dp(20), 0, dp(20), 0);
        EditText link = new EditText(this);
        link.setSingleLine(true);
        link.setHint("Ссылка из Spotify");
        link.setText(url);
        link.setEnabled(previous == null);
        fields.addView(link);
        EditText label = new EditText(this);
        label.setSingleLine(true);
        label.setHint("Название");
        label.setText(name);
        fields.addView(label);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle(previous == null ? "Сохранить ссылку" : "Переименовать")
            .setView(fields).setNegativeButton("Отмена", null).setPositiveButton("Сохранить", null).create();
        dialog.show();
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            Bookmark value = parse(link.getText().toString(), label.getText().toString());
            if (value == null) {
                link.setError("Вставь ссылку Spotify на трек, альбом, артиста или плейлист");
                return;
            }
            bookmarks.removeIf(entry -> entry.uri.equals(value.uri));
            bookmarks.add(0, value);
            if (bookmarks.size() > MAX_LINKS) bookmarks.remove(MAX_LINKS);
            save();
            refresh();
            dialog.dismiss();
        });
    }

    private void openSpotify(Bookmark bookmark) {
        try {
            if (bookmark == null) {
                Intent intent = getPackageManager().getLaunchIntentForPackage(SPOTIFY);
                if (intent == null) throw new ActivityNotFoundException();
                startActivity(intent);
            } else {
                try {
                    startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(bookmark.uri)).setPackage(SPOTIFY));
                } catch (ActivityNotFoundException missingUriHandler) {
                    startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(bookmark.url())).setPackage(SPOTIFY));
                }
            }
        } catch (ActivityNotFoundException | SecurityException failure) {
            new AlertDialog.Builder(this).setMessage("Установи или обнови Spotify для Android и войди в свой аккаунт Free.")
                .setPositiveButton("Понятно", null).show();
        }
    }

    private void share(Bookmark bookmark) {
        Intent message = new Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, bookmark.url());
        startActivity(Intent.createChooser(message, "Поделиться"));
    }

    private static Bookmark parse(String text, String name) {
        Matcher match = LINK.matcher(text);
        if (!match.find()) return null;
        String label = name == null ? "" : name.trim();
        return new Bookmark("spotify:" + match.group(1) + ":" + match.group(2), label.substring(0, Math.min(100, label.length())));
    }

    private void load() {
        try {
            JSONArray rows = new JSONArray(getPreferences(MODE_PRIVATE).getString("bookmarks", "[]"));
            for (int i = 0; i < rows.length() && bookmarks.size() < MAX_LINKS; i++) {
                JSONObject row = rows.optJSONObject(i);
                if (row != null) {
                    Bookmark bookmark = parse(row.optString("uri"), row.optString("name"));
                    if (bookmark != null) bookmarks.add(bookmark);
                }
            }
        } catch (JSONException ignored) { }
    }

    private void save() {
        JSONArray rows = new JSONArray();
        for (Bookmark bookmark : bookmarks) {
            try {
                rows.put(new JSONObject().put("uri", bookmark.uri).put("name", bookmark.name));
            } catch (JSONException impossible) {
                throw new IllegalStateException(impossible);
            }
        }
        getPreferences(MODE_PRIVATE).edit().putString("bookmarks", rows.toString()).apply();
    }

    private void refresh() {
        String query = filter.getText().toString().trim().toLowerCase(Locale.ROOT);
        visible.clear();
        for (Bookmark bookmark : bookmarks) {
            if (bookmark.toString().toLowerCase(Locale.ROOT).contains(query)) visible.add(bookmark);
        }
        adapter.notifyDataSetChanged();
        empty.setVisibility(visible.isEmpty() ? View.VISIBLE : View.GONE);
        empty.setText(bookmarks.isEmpty() ? "Добавь ссылку или выбери YSpotify в меню «Поделиться»." : "Ничего не найдено.");
    }

    private int dp(int size) { return Math.round(size * getResources().getDisplayMetrics().density); }

    private Button button(String title) {
        Button button = new Button(this);
        button.setText(title);
        button.setAllCaps(false);
        return button;
    }

    private static final class Bookmark {
        final String uri;
        final String name;

        Bookmark(String uri, String name) {
            this.uri = uri;
            this.name = name;
        }

        String url() { return "https://open.spotify.com/" + uri.substring(8).replace(':', '/'); }

        @Override public String toString() {
            if (!name.isEmpty()) return name;
            if (uri.startsWith("spotify:track:")) return "Трек Spotify";
            if (uri.startsWith("spotify:album:")) return "Альбом Spotify";
            if (uri.startsWith("spotify:artist:")) return "Артист Spotify";
            return "Плейлист Spotify";
        }
    }
}
