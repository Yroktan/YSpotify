package app.yspotify;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.Dialog;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.res.ColorStateList;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.inputmethod.InputMethodManager;
import android.widget.BaseAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.TextView;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

@androidx.media3.common.util.UnstableApi
public final class MainActivity extends Activity implements PlaybackService.Listener {
    private static final int BG = 0xff101a14, SURFACE = 0xff17291d, INK = 0xffe5efe8, MUTED = 0xff96af9f, ACCENT = 0xff91dba8;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService queries = Executors.newSingleThreadExecutor();
    private PlaybackService service;
    private Covers covers;
    private boolean bound, binding, preview, landscape, largeText, loginChanged, started;
    private String kind = "liked", argument, heading = "Любимое", rowState = "", footerCover = "";
    private long queryVersion;
    private List<Song> rows = new ArrayList<>();
    private SongAdapter adapter;
    private ListView list;
    private TextView title, count, status, empty, now, artist;
    private EditText search;
    private ImageView cover;
    private Glyph play, next, previous, account;
    private View navSearch, navLiked, navLibrary;
    private ProgressBar progress;
    private Dialog playerDialog;
    private PlayerPanel playerPanel;
    private JSONObject previewState;
    private final ServiceConnection connection = new ServiceConnection() {
        @Override public void onServiceConnected(ComponentName name, IBinder binder) {
            service = ((PlaybackService.LocalBinder) binder).service(); bound = true; if (covers != null) covers.close(); covers = new Covers(MainActivity.this, service.api.http); if (started) service.addListener(MainActivity.this);
            if (loginChanged) reloadSession(); else load(false);
        }
        @Override public void onServiceDisconnected(ComponentName name) { bound = false; service = null; status.setText("Плеер остановлен. Открой приложение ещё раз."); }
    };
    @Override protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        preview = BuildConfig.DEBUG && getIntent().getBooleanExtra("preview", false);
        landscape = getResources().getConfiguration().orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE;
        largeText = getResources().getConfiguration().fontScale > 1.2f;
        if (saved != null) { kind = saved.getString("kind", "liked"); argument = saved.getString("argument"); heading = saved.getString("heading", "Любимое"); }
        build();
        if (preview) { covers = new Covers(this, new okhttp3.OkHttpClient()); demo(); }
        else binding = bindService(new Intent(this, PlaybackService.class), connection, BIND_AUTO_CREATE);
    }
    private int dp(float value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private LinearLayout column() { LinearLayout view = new LinearLayout(this); view.setOrientation(LinearLayout.VERTICAL); return view; }
    private LinearLayout row() { LinearLayout view = new LinearLayout(this); view.setOrientation(LinearLayout.HORIZONTAL); view.setGravity(Gravity.CENTER_VERTICAL); return view; }
    private TextView text(String value, float size, int color) { TextView view = new TextView(this); view.setText(value); view.setTextColor(color); view.setTextSize(size); view.setSingleLine(true); view.setEllipsize(TextUtils.TruncateAt.END); return view; }
    private GradientDrawable shape(int color, int radius) { GradientDrawable drawable = new GradientDrawable(); drawable.setColor(color); drawable.setCornerRadius(dp(radius)); return drawable; }
    private void rounded(ImageView image) { image.setBackground(shape(0xff233b2b, 8)); image.setClipToOutline(true); image.setScaleType(ImageView.ScaleType.CENTER_CROP); }
    private Glyph glyph(String role, String description, int size, Runnable action) { Glyph icon = new Glyph(this, role); icon.setContentDescription(description); icon.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size))); icon.setOnClickListener(view -> action.run()); return icon; }
    private void build() {
        LinearLayout root = landscape ? row() : column(); root.setGravity(Gravity.TOP); root.setBackgroundColor(BG);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) { android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout()); android.graphics.Insets ime = insets.getInsets(WindowInsets.Type.ime()); view.setPadding(bars.left, bars.top, bars.right, Math.max(bars.bottom, ime.bottom)); }
            else view.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(), insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
            return insets;
        });
        LinearLayout body = column();
        if (landscape) {
            LinearLayout side = column(); side.setPadding(dp(12), dp(8), dp(12), dp(8)); side.setBackgroundColor(0xff0c1510); root.addView(side, new LinearLayout.LayoutParams(dp(148), -1));
            TextView brand = text("YSpotify", 22, INK); brand.setTypeface(Typeface.DEFAULT, Typeface.BOLD); side.addView(brand, new LinearLayout.LayoutParams(-1, dp(44)));
            account = glyph("account", "Аккаунт", 40, this::login); account.setId(R.id.account_button); side.addView(account);
            side.addView(navigation(true), new LinearLayout.LayoutParams(-1, -2)); root.addView(body, new LinearLayout.LayoutParams(0, -1, 1));
        } else {
            FrameLayout header = new FrameLayout(this); header.setPadding(dp(20), 0, dp(16), 0); root.addView(header, new LinearLayout.LayoutParams(-1, dp(largeText ? 68 : 86)));
            header.addView(new HeaderArt(this), new FrameLayout.LayoutParams(-1, -1));
            TextView brand = text("YSpotify", largeText ? 22 : 27, INK); brand.setTypeface(Typeface.DEFAULT, Typeface.BOLD); FrameLayout.LayoutParams brandLayout = new FrameLayout.LayoutParams(-2, -2, Gravity.CENTER_VERTICAL | Gravity.START); brandLayout.leftMargin = dp(4); header.addView(brand, brandLayout);
            account = glyph("account", "Аккаунт", 44, this::login); account.setId(R.id.account_button); FrameLayout.LayoutParams accountLayout = new FrameLayout.LayoutParams(dp(44), dp(44), Gravity.END | Gravity.CENTER_VERTICAL); header.addView(account, accountLayout);
            root.addView(body, new LinearLayout.LayoutParams(-1, 0, 1));
        }
        LinearLayout searchRow = row(); searchRow.setPadding(dp(16), 0, dp(12), dp(4));
        search = new EditText(this); search.setId(R.id.search_input); search.setTextSize(15); search.setSingleLine(true); search.setTextColor(INK); search.setHintTextColor(0xff789381); search.setHint("Трек, исполнитель или альбом"); search.setBackground(shape(0xff1d3023, 14)); search.setPadding(dp(14), 0, dp(10), 0); search.setImeOptions(android.view.inputmethod.EditorInfo.IME_ACTION_SEARCH); search.setInputType(android.text.InputType.TYPE_CLASS_TEXT);
        search.setOnEditorActionListener((view, action, event) -> { if (action == android.view.inputmethod.EditorInfo.IME_ACTION_SEARCH || event != null && event.getKeyCode() == android.view.KeyEvent.KEYCODE_ENTER) { submitSearch(); return true; } return false; }); searchRow.addView(search, new LinearLayout.LayoutParams(0, dp(44), 1));
        Glyph find = glyph("search", "Найти", 48, this::submitSearch); find.setId(R.id.search_button); searchRow.addView(find); body.addView(searchRow, new LinearLayout.LayoutParams(-1, dp(52)));
        LinearLayout headings = column(); headings.setPadding(dp(20), dp(landscape ? 0 : 8), dp(20), dp(6));
        title = text(heading, landscape ? 20 : 25, INK); title.setTypeface(Typeface.DEFAULT, Typeface.BOLD); headings.addView(title);
        count = text("Твоя музыка", 12, MUTED); if (!landscape && !largeText) { LinearLayout.LayoutParams sub = new LinearLayout.LayoutParams(-1, -2); sub.topMargin = dp(4); headings.addView(count, sub); } body.addView(headings); if (landscape && largeText) headings.setVisibility(View.GONE);
        FrameLayout content = new FrameLayout(this); body.addView(content, new LinearLayout.LayoutParams(-1, 0, 1));
        list = new ListView(this); list.setId(R.id.song_list); list.setBackgroundColor(BG); list.setDivider(null); list.setCacheColorHint(BG); list.setPadding(dp(8), 0, dp(8), dp(12)); list.setClipToPadding(true); list.setFastScrollEnabled(true); list.setScrollingCacheEnabled(false); list.setSelector(android.R.color.transparent);
        if (Build.VERSION.SDK_INT >= 29) list.setVerticalScrollbarThumbDrawable(shape(0xff537960, 8));
        adapter = new SongAdapter(); list.setAdapter(adapter); content.addView(list, new FrameLayout.LayoutParams(-1, -1));
        list.setOnScrollListener(new android.widget.AbsListView.OnScrollListener() { @Override public void onScrollStateChanged(android.widget.AbsListView view, int state) { if (covers != null) covers.defer(state != SCROLL_STATE_IDLE); if (state == SCROLL_STATE_IDLE) adapter.notifyDataSetChanged(); } @Override public void onScroll(android.widget.AbsListView view, int first, int visible, int total) { } });
        empty = text("Войди в Spotify, чтобы открыть свою музыку", 16, MUTED); empty.setSingleLine(false); empty.setMaxLines(3); empty.setGravity(Gravity.CENTER); empty.setPadding(dp(28), 0, dp(28), 0); content.addView(empty, new FrameLayout.LayoutParams(-1, -1));
        float[] touchStart = {0}; list.setOnTouchListener((view, event) -> { if (event.getActionMasked() == android.view.MotionEvent.ACTION_DOWN) touchStart[0] = event.getY(); if (event.getActionMasked() == android.view.MotionEvent.ACTION_UP && list.getFirstVisiblePosition() == 0 && event.getY() - touchStart[0] > dp(110)) load(true); return false; });
        status = text("", 11, MUTED); status.setId(R.id.status_text); status.setPadding(dp(20), dp(3), dp(20), dp(3)); body.addView(status);
        status.setVisibility(View.GONE); status.addTextChangedListener(new android.text.TextWatcher() { @Override public void beforeTextChanged(CharSequence value, int start, int count, int after) { } @Override public void onTextChanged(CharSequence value, int start, int before, int count) { } @Override public void afterTextChanged(android.text.Editable value) { status.setVisibility(value.length() == 0 ? View.GONE : View.VISIBLE); } });
        body.addView(footer(), new LinearLayout.LayoutParams(-1, -2));
        if (!landscape) root.addView(navigation(false), new LinearLayout.LayoutParams(-1, dp(64)));
        setContentView(root); setNavigation();
    }
    private View navigation(boolean vertical) {
        LinearLayout bar = vertical ? column() : row(); bar.setBackgroundColor(0xff0d1711);
        navSearch = tab("search", "Поиск", R.id.tab_search, () -> page("search", null, "Поиск"), vertical);
        navLiked = tab("heart", "Любимое", R.id.tab_liked, () -> page("liked", null, "Любимое"), vertical);
        navLibrary = tab("library", "Плейлисты", R.id.tab_library, () -> page("library", null, "Плейлисты"), vertical);
        for (View tab : new View[]{navSearch, navLiked, navLibrary}) bar.addView(tab, vertical ? new LinearLayout.LayoutParams(-1, dp(48)) : new LinearLayout.LayoutParams(0, -1, 1));
        return bar;
    }
    private View tab(String icon, String label, int id, Runnable action, boolean vertical) {
        LinearLayout tab = vertical ? row() : column(); tab.setId(id); tab.setGravity(Gravity.CENTER); tab.setPadding(dp(4), 0, dp(4), 0); tab.setClickable(true); tab.setFocusable(true); tab.setContentDescription(label); tab.setBackground(new android.graphics.drawable.RippleDrawable(ColorStateList.valueOf(0x3091dba8), null, shape(0xffffffff, 12))); tab.setOnClickListener(view -> action.run());
        Glyph glyph = new Glyph(this, icon); glyph.setClickable(false); glyph.setFocusable(false); tab.addView(glyph, new LinearLayout.LayoutParams(dp(32), dp(32))); TextView name = text(label, 12, MUTED); name.setGravity(Gravity.CENTER); tab.addView(name, vertical ? new LinearLayout.LayoutParams(0, -2, 1) : new LinearLayout.LayoutParams(-1, -2)); return tab;
    }
    private void setNavigation() {
        View selected = kind.equals("search") ? navSearch : kind.equals("liked") ? navLiked : navLibrary;
        for (View view : new View[]{navSearch, navLiked, navLibrary}) if (view instanceof LinearLayout) { LinearLayout tab = (LinearLayout) view; ((Glyph) tab.getChildAt(0)).active(view == selected); ((TextView) tab.getChildAt(1)).setTextColor(view == selected ? ACCENT : MUTED); }
    }
    private View footer() {
        LinearLayout footer = row(); footer.setBackgroundColor(SURFACE); footer.setPadding(dp(12), dp(10), dp(8), dp(10)); footer.setMinimumHeight(dp(76));
        cover = new ImageView(this); rounded(cover); footer.addView(cover, new LinearLayout.LayoutParams(dp(44), dp(44))); cover.setOnClickListener(view -> showPlayer());
        LinearLayout info = column(); info.setGravity(Gravity.CENTER_VERTICAL); info.setPadding(dp(10), 0, dp(4), 0); now = text("Ничего не играет", 14, INK); now.setId(R.id.current_title); now.setTypeface(Typeface.DEFAULT, Typeface.BOLD); artist = text("Выбери трек", 12, MUTED); info.addView(now); info.addView(artist); footer.addView(info, new LinearLayout.LayoutParams(0, -2, 1)); info.setOnClickListener(view -> showPlayer());
        previous = glyph("previous", "Предыдущий трек", 40, () -> { if (service != null) service.previous(); }); previous.setId(R.id.previous_button); footer.addView(previous);
        play = glyph("play", "Воспроизведение / пауза", 44, () -> { if (service != null) { wakePlayback(); service.toggle(); } }); play.setId(R.id.play_button); footer.addView(play);
        next = glyph("next", "Следующий трек", 40, () -> { if (service != null) service.next(); }); next.setId(R.id.next_button); footer.addView(next);
        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal); progress.setProgressTintList(ColorStateList.valueOf(ACCENT)); progress.setProgressBackgroundTintList(ColorStateList.valueOf(0xff2d4735)); progress.setMax(1000);
        LinearLayout holder = column(); holder.addView(footer); holder.addView(progress, new LinearLayout.LayoutParams(-1, dp(2))); play.setEnabled(false); previous.setEnabled(false); next.setEnabled(false); return holder;
    }
    private void page(String nextKind, String nextArgument, String name) { kind = nextKind; argument = nextArgument; heading = name; title.setText(name); setNavigation(); rows = new ArrayList<>(); adapter.notifyDataSetChanged(); empty.setVisibility(View.VISIBLE); load(false); }
    private void submitSearch() {
        String query = search.getText().toString().trim(); if (query.isEmpty()) return;
        ((InputMethodManager) getSystemService(INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(search.getWindowToken(), 0); search.clearFocus(); page("search", query, "Результаты поиска");
    }
    private void load(boolean force) {
        if (preview) return;
        if (service == null) return;
        long version = ++queryVersion; String type = kind, arg = argument;
        if (!service.api.session.exists()) { empty.setText("Войди в Spotify, чтобы открыть свою музыку"); empty.setVisibility(View.VISIBLE); return; }
        if (type.equals("search") && (arg == null || arg.isEmpty())) { empty.setText("Введи название трека или исполнителя"); empty.setVisibility(View.VISIBLE); return; }
        Catalog catalog = service.catalog; List<Song> cached = catalog.cached(type, arg);
        if (cached != null && rows.isEmpty()) applyRows(cached);
        status.setText(rows.isEmpty() ? "Загружаю…" : "Обновляю список…");
        queries.execute(() -> {
            try { List<Song> result = catalog.query(type, arg, force); main.post(() -> { if (version != queryVersion || isFinishing()) return; applyRows(result); status.setText(""); }); }
            catch (Exception error) { main.post(() -> { if (version != queryVersion || isFinishing()) return; status.setText(SpotifyApi.message(error)); if (rows.isEmpty()) { empty.setText("Не удалось загрузить список. Потяни вниз, чтобы повторить."); empty.setVisibility(View.VISIBLE); } }); }
        });
    }
    private void applyRows(List<Song> incoming) {
        int position = list.getFirstVisiblePosition(); View first = list.getChildAt(0); int top = first == null ? 0 : first.getTop(); String anchor = position < rows.size() ? rows.get(position).uri + '#' + rows.get(position).uid : "";
        rows = new ArrayList<>(incoming); adapter.notifyDataSetChanged(); empty.setVisibility(rows.isEmpty() ? View.VISIBLE : View.GONE); empty.setText("Здесь пока нет треков"); count.setText(rows.size() + (kind.equals("library") ? " плейлистов" : " треков"));
        for (int i = 0; i < rows.size() && !anchor.isEmpty(); i++) if ((rows.get(i).uri + '#' + rows.get(i).uid).equals(anchor)) { list.setSelectionFromTop(i, top); break; }
    }
    private void select(Song song) {
        if (song.playlist()) { page("playlist", song.uri, song.name); return; }
        if (service == null) return; wakePlayback();
        String source = kind.equals("playlist") ? argument : kind.equals("liked") ? "spotify:collection:tracks" : song.uri;
        service.play(song, rows, source);
    }
    private void wakePlayback() { startForegroundService(new Intent(this, PlaybackService.class).setAction("wake")); }
    @Override protected void onSaveInstanceState(Bundle state) { state.putString("kind", kind); state.putString("argument", argument); state.putString("heading", heading); super.onSaveInstanceState(state); }
    @Override protected void onDestroy() { queryVersion++; if (service != null) service.removeListener(this); if (binding) unbindService(connection); if (covers != null) covers.close(); queries.shutdownNow(); main.removeCallbacksAndMessages(null); if (playerDialog != null) playerDialog.dismiss(); super.onDestroy(); }
    private void login() {
        if (preview) return;
        if (service == null || !service.api.session.exists()) { startActivityForResult(new Intent(this, LoginActivity.class), 11); return; }
        new AlertDialog.Builder(this).setTitle("Аккаунт Spotify").setMessage("Вход сохранён на этом устройстве.")
                .setPositiveButton("Готово", null).setNeutralButton("Обновить вход", (dialog, which) -> startActivityForResult(new Intent(this, LoginActivity.class), 11))
                .setNegativeButton("Выйти", (dialog, which) -> { queryVersion++; service.logout(); android.webkit.CookieManager.getInstance().removeAllCookies(null); android.webkit.CookieManager.getInstance().flush(); rows.clear(); adapter.notifyDataSetChanged(); empty.setText("Войди в Spotify, чтобы открыть свою музыку"); empty.setVisibility(View.VISIBLE); }).show();
    }
    @Override protected void onActivityResult(int request, int result, Intent data) { super.onActivityResult(request, result, data); if (request == 11 && result == RESULT_OK) { loginChanged = true; if (service != null) reloadSession(); } }
    private void reloadSession() {
        try { service.api.session.save(new SessionStore(this).snapshot()); service.sessionChanged(); loginChanged = false; rows.clear(); rowState = ""; load(true); changed(); }
        catch (Exception failure) { status.setText("Не удалось восстановить вход. Открой аккаунт ещё раз."); }
    }
    @Override public void changed() {
        if (service == null || isFinishing()) return;
        JSONObject state = service.snapshot(), track = J.obj(state, "track"); String uri = J.text(track, "requestedUri"); if (uri.isEmpty()) uri = J.text(track, "uri"); if (state.optBoolean("busy") && !J.text(state, "pendingUri").isEmpty()) uri = J.text(state, "pendingUri");
        now.setText(J.text(track, "name").isEmpty() ? "Ничего не играет" : J.text(track, "name")); artist.setText(J.text(track, "artist").isEmpty() ? "Выбери трек" : J.text(track, "artist"));
        play.playing(state.optBoolean("playing")); play.busy(state.optBoolean("busy")); play.setEnabled(!J.text(track, "uri").isEmpty()); previous.setEnabled(state.optBoolean("canPrevious")); next.setEnabled(state.optBoolean("canNext")); account.active(state.optBoolean("loggedIn"));
        long duration = state.optLong("duration"); progress.setProgress(duration > 0 ? (int) Math.min(1000, state.optLong("position") * 1000 / duration) : 0);
        String image = J.text(track, "cover"); if (!image.equals(footerCover)) { footerCover = image; if (covers != null) covers.bind(cover, image, 128); }
        String signature = uri + ':' + state.optBoolean("playing") + ':' + state.optBoolean("busy"); if (!signature.equals(rowState)) { rowState = signature; adapter.notifyDataSetChanged(); }
        if (!J.text(state, "error").isEmpty()) status.setText(J.text(state, "error"));
        if (playerDialog != null && playerDialog.isShowing() && playerPanel != null) playerPanel.update(state);
    }
    private void songMenu(Song song) {
        if (preview || service == null) return;
        String playlist = kind.equals("playlist") ? argument : null;
        boolean canRemove = false; List<Song> playlists = service.catalog.cached("library", null); if (playlists != null) for (Song row : playlists) if (row.uri.equals(playlist)) canRemove = row.editable;
        LinearLayout options = column(); options.setPadding(dp(16), dp(8), dp(16), dp(12));
        Button favorite = new Button(this); favorite.setAllCaps(false); favorite.setText("Проверяю избранное…"); favorite.setEnabled(false); options.addView(favorite);
        Button add = new Button(this); add.setAllCaps(false); add.setText("Добавить в плейлист"); options.addView(add);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle(song.name).setView(options).setNegativeButton("Закрыть", null).create();
        add.setOnClickListener(view -> { dialog.dismiss(); choosePlaylist(song); });
        if (playlist != null && canRemove) { Button remove = new Button(this); remove.setAllCaps(false); remove.setText("Убрать из этого плейлиста"); options.addView(remove); remove.setOnClickListener(view -> { dialog.dismiss(); status.setText("Убираю из плейлиста…"); queries.execute(() -> { try { service.catalog.removeFromPlaylist(song, playlist); main.post(() -> load(true)); } catch (Exception error) { main.post(() -> status.setText(SpotifyApi.message(error))); } }); }); }
        dialog.show(); Catalog catalog = service.catalog;
        queries.execute(() -> { try { boolean saved = catalog.saved(song.uri); main.post(() -> { if (!dialog.isShowing()) return; favorite.setText(saved ? "Убрать из любимого" : "Добавить в любимое"); favorite.setEnabled(true); favorite.setOnClickListener(view -> { dialog.dismiss(); status.setText("Сохраняю…"); queries.execute(() -> { try { catalog.favorite(song.uri, !saved); main.post(() -> { status.setText("Сохранено в Spotify"); if (kind.equals("liked")) load(true); }); } catch (Exception error) { main.post(() -> status.setText(SpotifyApi.message(error))); } }); }); }); } catch (Exception error) { main.post(() -> { if (dialog.isShowing()) favorite.setText(SpotifyApi.message(error)); }); } });
    }
    private void choosePlaylist(Song song) {
        status.setText("Загружаю плейлисты…"); queries.execute(() -> {
            try {
                List<Song> editable = new ArrayList<>(); for (Song playlist : service.catalog.query("library", null, false)) if (playlist.editable) editable.add(playlist);
                main.post(() -> {
                    if (isFinishing()) return; status.setText(""); if (editable.isEmpty()) { new AlertDialog.Builder(this).setMessage("Нет плейлистов для добавления").setPositiveButton("Готово", null).show(); return; }
                    String[] names = new String[editable.size()]; for (int i = 0; i < names.length; i++) names[i] = editable.get(i).name;
                    new AlertDialog.Builder(this).setTitle("Добавить в плейлист").setItems(names, (dialog, index) -> { status.setText("Сохраняю…"); queries.execute(() -> { try { service.catalog.addToPlaylist(song.uri, editable.get(index).uri); main.post(() -> status.setText("Добавлено в плейлист")); } catch (Exception error) { main.post(() -> status.setText(SpotifyApi.message(error))); } }); }).show();
                });
            } catch (Exception error) { main.post(() -> status.setText(SpotifyApi.message(error))); }
        });
    }
    private void showPlayer() {
        if (service == null && !preview) return; JSONObject current = service == null ? previewState : service.snapshot(); if (current == null || J.text(J.obj(current, "track"), "uri").isEmpty()) return;
        playerDialog = new Dialog(this); playerPanel = new PlayerPanel(); playerDialog.setContentView(playerPanel); playerDialog.getWindow().setBackgroundDrawableResource(android.R.color.transparent); playerDialog.show(); playerDialog.getWindow().setLayout(-1, -1); playerPanel.update(current);
    }
    private final class PlayerPanel extends ScrollView {
        private final TextView name, author, times, mode;
        private final ImageView art;
        private final Glyph largePlay, back, forward, mix, again;
        private final SeekBar seek, volume;
        private boolean seeking, changingVolume;
        private String image = "";
        PlayerPanel() {
            super(MainActivity.this); setFillViewport(true); setBackgroundColor(BG);
            setOnApplyWindowInsetsListener((view, insets) -> { view.setPadding(0, insets.getSystemWindowInsetTop(), 0, insets.getSystemWindowInsetBottom()); return insets; });
            LinearLayout content = column(); content.setPadding(dp(24), dp(10), dp(24), dp(24)); addView(content, new ScrollView.LayoutParams(-1, -2));
            LinearLayout bar = row(); Glyph close = glyph("back", "Закрыть плеер", 44, () -> playerDialog.dismiss()); bar.addView(close); TextView caption = text("Сейчас играет", 15, MUTED); caption.setGravity(Gravity.CENTER); bar.addView(caption, new LinearLayout.LayoutParams(0, dp(44), 1)); Glyph actions = glyph("menu", "Действия с треком", 44, () -> { if (service == null) return; JSONObject current = J.obj(service.snapshot(), "track"); String uri = J.text(current, "requestedUri"); if (uri.isEmpty()) uri = J.text(current, "uri"); Song song = service.catalog.track(uri); if (song != null) songMenu(song); }); bar.addView(actions); content.addView(bar);
            art = new ImageView(MainActivity.this); rounded(art); int artSize = largeText ? 96 : landscape ? 140 : 244; LinearLayout.LayoutParams artwork = new LinearLayout.LayoutParams(dp(artSize), dp(artSize)); artwork.gravity = Gravity.CENTER_HORIZONTAL; artwork.topMargin = dp(largeText ? 12 : 24); artwork.bottomMargin = dp(largeText ? 12 : 24); content.addView(art, artwork);
            name = text("", 24, INK); name.setTypeface(Typeface.DEFAULT, Typeface.BOLD); author = text("", 15, MUTED); content.addView(name); LinearLayout.LayoutParams artistLayout = new LinearLayout.LayoutParams(-1, -2); artistLayout.topMargin = dp(6); content.addView(author, artistLayout);
            seek = new SeekBar(MainActivity.this); seek.setMax(1000); seek.setProgressTintList(ColorStateList.valueOf(ACCENT)); seek.setThumbTintList(ColorStateList.valueOf(ACCENT)); LinearLayout.LayoutParams seekingLayout = new LinearLayout.LayoutParams(-1, dp(42)); seekingLayout.topMargin = dp(18); content.addView(seek, seekingLayout);
            times = text("0:00 / 0:00", 12, MUTED); content.addView(times);
            seek.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() { @Override public void onStartTrackingTouch(SeekBar bar) { seeking = true; } @Override public void onProgressChanged(SeekBar bar, int value, boolean user) { } @Override public void onStopTrackingTouch(SeekBar bar) { seeking = false; if (service != null) { JSONObject state = service.snapshot(); service.seek(state.optLong("duration") * bar.getProgress() / 1000); } } });
            LinearLayout controls = row(); controls.setGravity(Gravity.CENTER); mix = glyph("shuffle", "Перемешать", 48, () -> { if (service != null) service.shuffle(); }); back = glyph("previous", "Предыдущий трек", 48, () -> { if (service != null) service.previous(); }); largePlay = glyph("play", "Воспроизведение / пауза", 64, () -> { if (service != null) { wakePlayback(); service.toggle(); } }); forward = glyph("next", "Следующий трек", 48, () -> { if (service != null) service.next(); }); again = glyph("repeat", "Повтор", 48, () -> { if (service != null) service.repeat(); });
            for (View control : new View[]{mix, back, largePlay, forward, again}) controls.addView(control); LinearLayout.LayoutParams controlLayout = new LinearLayout.LayoutParams(-1, dp(88)); controlLayout.topMargin = dp(10); content.addView(controls, controlLayout);
            mode = text("", 12, MUTED); mode.setGravity(Gravity.CENTER); content.addView(mode);
            TextView volumeCaption = text("Громкость", 13, MUTED); LinearLayout.LayoutParams volumeLabel = new LinearLayout.LayoutParams(-1, -2); volumeLabel.topMargin = dp(22); content.addView(volumeCaption, volumeLabel);
            volume = new SeekBar(MainActivity.this); volume.setMax(100); volume.setProgressTintList(ColorStateList.valueOf(ACCENT)); volume.setThumbTintList(ColorStateList.valueOf(ACCENT)); content.addView(volume, new LinearLayout.LayoutParams(-1, dp(44)));
            volume.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() { @Override public void onStartTrackingTouch(SeekBar bar) { changingVolume = true; } @Override public void onProgressChanged(SeekBar bar, int value, boolean user) { if (user && service != null) service.setVolume(value / 100f); } @Override public void onStopTrackingTouch(SeekBar bar) { changingVolume = false; } });
        }
        void update(JSONObject state) {
            JSONObject current = J.obj(state, "track"); name.setText(J.text(current, "name")); author.setText(J.text(current, "artist")); String nextImage = J.text(current, "cover"); if (!image.equals(nextImage)) { image = nextImage; covers.bind(art, image, 384); }
            if (preview) art.setImageResource(R.mipmap.ic_launcher); largePlay.playing(state.optBoolean("playing")); largePlay.busy(state.optBoolean("busy")); back.setEnabled(state.optBoolean("canPrevious")); forward.setEnabled(state.optBoolean("canNext")); seek.setEnabled(state.optBoolean("canSeek")); long duration = state.optLong("duration"), position = state.optLong("position");
            if (!seeking) seek.setProgress(duration > 0 ? (int) (position * 1000 / duration) : 0); times.setText(time(position) + " / " + time(duration)); if (!changingVolume) volume.setProgress((int) (state.optDouble("volume", .8) * 100)); mix.active(state.optBoolean("shuffle")); again.active(!J.text(state, "repeat").equals("off")); mode.setText(J.text(state, "repeat").equals("track") ? "Повтор одного трека" : J.text(state, "repeat").equals("context") ? "Повтор списка" : state.optBoolean("shuffle") ? "В случайном порядке" : "По порядку");
        }
    }
    private static String time(long milliseconds) { long seconds = Math.max(0, milliseconds / 1000); return (seconds / 60) + ":" + String.format(java.util.Locale.ROOT, "%02d", seconds % 60); }
    @Override protected void onStart() { super.onStart(); started = true; if (service != null) service.addListener(this); }
    @Override protected void onStop() { started = false; if (service != null) service.removeListener(this); super.onStop(); }
    private final class SongAdapter extends BaseAdapter {
        @Override public int getCount() { return rows.size(); }
        @Override public Song getItem(int position) { return rows.get(position); }
        @Override public long getItemId(int position) { return getItem(position).uri.hashCode(); }
        @Override public int getViewTypeCount() { return 2; }
        @Override public int getItemViewType(int position) { return getItem(position).playlist() ? 1 : 0; }
        @Override public View getView(int position, View recycled, ViewGroup parent) {
            Song song = getItem(position); Holder holder;
            if (recycled == null) { holder = new Holder(song.playlist()); recycled = holder.root; recycled.setTag(holder); } else holder = (Holder) recycled.getTag();
            holder.name.setText(song.name); holder.artist.setText(song.artist); holder.duration.setText(song.duration); holder.root.setContentDescription(song.name + ", " + song.artist); holder.root.setOnClickListener(view -> select(song)); holder.root.setOnLongClickListener(view -> { if (!song.playlist()) songMenu(song); return true; }); holder.listen.setOnClickListener(view -> select(song)); holder.menu.setOnClickListener(view -> songMenu(song)); holder.menu.setVisibility(song.playlist() ? View.GONE : View.VISIBLE);
            boolean selected = false, busy = false;
            if (service != null) { JSONObject state = service.snapshot(), current = J.obj(state, "track"); selected = song.uri.equals(J.text(current, "uri")) || song.uri.equals(J.text(current, "requestedUri")); busy = state.optBoolean("busy") && song.uri.equals(J.text(state, "pendingUri")); selected = selected || busy; }
            holder.root.setBackground(new android.graphics.drawable.RippleDrawable(ColorStateList.valueOf(0x3091dba8), shape(selected ? 0xff203b2a : BG, 10), null)); holder.listen.active(selected); holder.listen.busy(busy); if (covers != null) covers.bind(holder.cover, song.cover, 128); return recycled;
        }
        private final class Holder {
            final LinearLayout root; final ImageView cover; final TextView name, artist, duration; final Glyph listen, menu;
            Holder(boolean playlist) {
                root = row(); root.setPadding(dp(8), dp(10), dp(2), dp(10)); root.setMinimumHeight(dp(Math.max(72, 42 * getResources().getConfiguration().fontScale + 20))); root.setDescendantFocusability(ViewGroup.FOCUS_BLOCK_DESCENDANTS);
                cover = new ImageView(MainActivity.this); rounded(cover); root.addView(cover, new LinearLayout.LayoutParams(dp(46), dp(46)));
                LinearLayout information = column(); information.setGravity(Gravity.CENTER_VERTICAL); information.setPadding(dp(12), 0, dp(4), 0); name = text("", 15, INK); name.setTypeface(Typeface.DEFAULT, Typeface.BOLD); artist = text("", 12, MUTED); information.addView(name); LinearLayout.LayoutParams detail = new LinearLayout.LayoutParams(-1, -2); detail.topMargin = dp(4); information.addView(artist, detail); root.addView(information, new LinearLayout.LayoutParams(0, -2, 1));
                duration = text("", 11, MUTED); duration.setGravity(Gravity.END); root.addView(duration, new LinearLayout.LayoutParams(dp(34), -2)); listen = glyph(playlist ? "library" : "listen", playlist ? "Открыть плейлист" : "Воспроизвести трек", 40, () -> { }); listen.setFocusable(false); root.addView(listen); menu = glyph("menu", "Действия с треком", 32, () -> { }); menu.setFocusable(false); root.addView(menu);
            }
        }
    }
    private void demo() {
        String[][] songs = {{"Resonance", "HOME", "3:32"}, {"After Dark", "Mr.Kitty", "4:17"}, {"Nightcall", "Kavinsky", "4:18"}, {"A Walk", "Tycho", "5:17"}, {"Sunset Lover", "Petit Biscuit", "3:58"}, {"Weightless", "Marconi Union", "8:09"}, {"Nights", "Frank Ocean", "5:07"}, {"Awake", "Tycho", "4:43"}, {"Veridis Quo", "Daft Punk", "5:44"}, {"Dayvan Cowboy", "Boards of Canada", "5:00"}};
        List<Song> fixtures = new ArrayList<>(); for (int i = 0; i < songs.length; i++) fixtures.add(new Song(J.o("uri", "spotify:track:" + String.format(java.util.Locale.ROOT, "%022d", i), "name", songs[i][0], "artist", songs[i][1], "duration", songs[i][2], "saved", true)));
        applyRows(fixtures); now.setText("Resonance"); artist.setText("HOME"); cover.setImageResource(R.mipmap.ic_launcher); play.setEnabled(true); next.setEnabled(true); previous.setEnabled(true); play.setOnClickListener(view -> play.playing(true)); progress.setProgress(340);
        previewState = J.o("track", J.o("uri", fixtures.get(0).uri, "name", "Resonance", "artist", "HOME", "cover", ""), "playing", false, "busy", false, "canPrevious", true, "canNext", true, "canSeek", true, "duration", 212000, "position", 72000, "volume", .8, "shuffle", false, "repeat", "off");
    }
    private final class HeaderArt extends View {
        private final Bitmap bitmap; private final Paint paint = new Paint(3);
        HeaderArt(Context context) { super(context); BitmapFactory.Options options = new BitmapFactory.Options(); options.inSampleSize = 4; bitmap = BitmapFactory.decodeResource(getResources(), R.drawable.utsuho_outline, options); paint.setAlpha(110); }
        @Override protected void onDraw(Canvas canvas) { if (bitmap == null) return; float height = getHeight() * .95f, width = height * bitmap.getWidth() / bitmap.getHeight(); canvas.drawBitmap(bitmap, null, new RectF(getWidth() - width - dp(24), (getHeight() - height) / 2, getWidth() - dp(24), (getHeight() + height) / 2), paint); }
    }
}
