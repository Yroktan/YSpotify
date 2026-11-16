package app.yspotify;

import android.content.Context;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

final class Catalog {
    private final SpotifyApi api;
    private final File file;
    private final JSONObject definitions;
    private JSONObject entries = new JSONObject();
    private String username;
    Catalog(Context context, SpotifyApi api) {
        this.api = api;
        String identity = J.text(api.session.snapshot(), "cookie");
        file = new File(context.getFilesDir(), "catalog-" + hash(identity).substring(0, 24) + ".json");
        try (java.io.InputStream input = context.getAssets().open("operations.json")) { definitions = J.parse(J.read(input)); }
        catch (Exception error) { throw new IllegalStateException(error); }
        try { if (file.exists() && file.length() < 8 * 1024 * 1024) entries = J.parse(new String(Files.readAllBytes(file.toPath()), StandardCharsets.UTF_8)); }
        catch (Exception ignored) { }
    }
    private static String hash(String value) {
        try { byte[] bytes = MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8)); StringBuilder result = new StringBuilder(); for (byte b : bytes) result.append(String.format(java.util.Locale.ROOT, "%02x", b)); return result.toString(); }
        catch (Exception error) { throw new IllegalStateException(error); }
    }
    private String key(String kind, String argument) { return kind + ':' + (argument == null ? "" : argument); }
    private List<Song> songs(JSONArray values) { List<Song> result = new ArrayList<>(); for (int i = 0; i < values.length(); i++) { JSONObject row = values.optJSONObject(i); if (row != null) result.add(new Song(row)); } return result; }
    synchronized List<Song> cached(String kind, String argument) { JSONObject entry = entries.optJSONObject(key(kind, argument)); return entry == null ? null : songs(J.array(entry, "rows")); }
    synchronized Song track(String uri) {
        java.util.Iterator<String> keys = entries.keys();
        while (keys.hasNext()) for (Song row : songs(J.array(entries.optJSONObject(keys.next()), "rows"))) if (row.uri.equals(uri)) return row;
        return null;
    }
    JSONObject operation(String kind, JSONObject variables) throws Exception {
        JSONArray definition = definitions.optJSONArray(kind);
        if (definition == null) throw new IllegalArgumentException("CatalogOperationMissing");
        JSONObject result = api.request("https://api-partner.spotify.com/pathfinder/v2/query", "POST", J.o("operationName", definition.optString(0), "variables", variables, "extensions", J.o("persistedQuery", J.o("version", 1, "sha256Hash", definition.optString(1)))));
        if (J.array(result, "errors").length() > 0) throw new IllegalStateException("CatalogQueryChanged");
        return J.obj(result, "data");
    }
    private JSONObject variables(String kind, String argument, int offset) {
        if (kind.equals("search")) return J.o("query", argument, "limit", 50, "offset", 0, "numberOfTopResults", 50, "includeArtistHasConcertsField", false, "includeAudiobooks", true, "includeAuthors", true, "includePreReleases", true, "includeAlbumPreReleases", false, "includeEpisodeContentRatingsV2", true, "isPrefix", JSONObject.NULL, "sectionFilters", new JSONArray(java.util.Arrays.asList("GENERIC", "VIDEO_CONTENT")));
        if (kind.equals("library")) return J.o("filters", new JSONArray(), "order", JSONObject.NULL, "textFilter", "", "features", new JSONArray(java.util.Arrays.asList("LIKED_SONGS", "YOUR_EPISODES_V2", "PRERELEASES", "EVENTS")), "limit", 100, "offset", offset, "flatten", true, "expandedFolders", new JSONArray(), "folderUri", JSONObject.NULL, "includeFoldersWhenFlattening", true);
        if (kind.equals("playlist")) return J.o("uri", argument, "offset", offset, "limit", 100, "enableWatchFeedEntrypoint", true, "includeEpisodeContentRatingsV2", true);
        return J.o("offset", offset, "limit", 100);
    }
    private JSONObject container(JSONObject data, String kind) {
        if (kind.equals("liked")) return J.obj(J.obj(J.obj(data, "me"), "library"), "tracks");
        if (kind.equals("playlist")) return J.obj(J.obj(data, "playlistV2"), "content");
        if (kind.equals("library")) return J.obj(J.obj(data, "me"), "libraryV3");
        return new JSONObject();
    }
    List<Song> query(String kind, String argument, boolean force) throws Exception {
        synchronized (this) {
            JSONObject existing = entries.optJSONObject(key(kind, argument));
            if (!force && existing != null && existing.optBoolean("complete") && System.currentTimeMillis() - existing.optLong("at") < 45000) return songs(J.array(existing, "rows"));
        }
        if (kind.equals("library") && username == null) username = J.text(J.obj(J.obj(operation("profile", new JSONObject()), "me"), "profile"), "username");
        List<Song> rows = null;
        for (int attempt = 0; attempt < 2; attempt++) {
            try {
                if (kind.equals("search")) rows = parse(operation(kind, variables(kind, argument, 0)), kind);
                else {
                    PageAccumulator pages = new PageAccumulator();
                    for (int n = 0; n < 200; n++) {
                        JSONObject data = operation(kind, variables(kind, argument, pages.offset()));
                        JSONObject container = container(data, kind);
                        JSONArray raw = container.optJSONArray("items");
                        if (raw == null) throw new IllegalStateException("CatalogPageInvalid");
                        JSONObject paging = J.obj(container, "pagingInfo");
                        List<Song> parsed = parse(data, kind);
                        if (pages.accept(paging.optInt("offset", pages.offset()), paging.optInt("limit", 100), container.optInt("totalCount", -1), raw.length(), parsed, kind.equals("playlist"))) { rows = pages.rows(); break; }
                    }
                    if (rows == null) throw new IllegalStateException("CatalogIncomplete");
                }
                break;
            } catch (IllegalStateException error) {
                if (attempt == 1 || !(error.getMessage().equals("CatalogChangedDuringLoad") || error.getMessage().equals("CatalogIncomplete"))) throw error;
            }
        }
        JSONArray saved = new JSONArray(); for (Song row : rows) saved.put(row.json());
        synchronized (this) {
            entries.put(key(kind, argument), J.o("at", System.currentTimeMillis(), "complete", true, "rows", saved));
            while (entries.length() > 32) { String oldest = null; long time = Long.MAX_VALUE; java.util.Iterator<String> iterator = entries.keys(); while (iterator.hasNext()) { String name = iterator.next(); long at = J.obj(entries, name).optLong("at"); if (at < time) { time = at; oldest = name; } } if (oldest == null) break; entries.remove(oldest); }
            File temporary = new File(file.getPath() + ".tmp"); Files.write(temporary.toPath(), entries.toString().getBytes(StandardCharsets.UTF_8)); Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING);
        }
        return rows;
    }
    List<Song> parse(JSONObject root, String kind) {
        LinkedHashMap<String, Song> rows = new LinkedHashMap<>(); collect(root, kind, rows, 0);
        return new ArrayList<>(rows.values());
    }
    private void collect(Object value, String kind, LinkedHashMap<String, Song> rows, int depth) {
        if (depth > 22 || rows.size() > 100) return;
        if (value instanceof JSONArray) { JSONArray array = (JSONArray) value; for (int i = 0; i < array.length(); i++) collect(array.opt(i), kind, rows, depth + 1); return; }
        if (!(value instanceof JSONObject)) return;
        JSONObject object = (JSONObject) value;
        JSONObject wrapper = object.optJSONObject("track"); if (wrapper == null) wrapper = object.optJSONObject("itemV2"); if (wrapper == null) wrapper = object.optJSONObject("item");
        if (wrapper != null && wrapper.optJSONObject("data") != null) {
            JSONObject track = J.parse(wrapper.optJSONObject("data").toString()); String uri = J.text(track, "uri"); if (uri.isEmpty()) uri = J.text(wrapper, "_uri");
            try { track.put("uri", uri); track.put("uid", J.text(object, "uid").isEmpty() ? J.text(wrapper, "uid") : J.text(object, "uid")); } catch (Exception ignored) { }
            collect(track, kind, rows, depth + 1); return;
        }
        String uri = J.text(object, "uri"), name = J.text(object, "name");
        if (uri.startsWith(kind.equals("library") ? "spotify:playlist:" : "spotify:track:") && !name.isEmpty()) {
            JSONArray artists = J.array(J.obj(object, "artists"), "items"); if (artists.length() == 0) artists = J.array(J.obj(object, "artistsV2"), "items");
            StringBuilder artist = new StringBuilder();
            for (int i = 0; i < artists.length(); i++) { JSONObject entry = artists.optJSONObject(i); String text = J.text(J.obj(entry, "profile"), "name"); if (text.isEmpty()) text = J.text(entry, "name"); if (!text.isEmpty()) { if (artist.length() > 0) artist.append(", "); artist.append(text); } }
            JSONObject album = J.obj(object, "albumOfTrack"); if (album.length() == 0) album = J.obj(object, "album");
            JSONArray covers = J.array(J.obj(album, "coverArt"), "sources"); if (covers.length() == 0) covers = J.array(album, "images");
            if (covers.length() == 0) { JSONArray groups = J.array(J.obj(object, "images"), "items"); covers = new JSONArray(); for (int group = 0; group < groups.length(); group++) { JSONArray sources = J.array(groups.optJSONObject(group), "sources"); for (int i = 0; i < sources.length(); i++) covers.put(sources.opt(i)); } }
            String cover = ""; int distance = Integer.MAX_VALUE;
            for (int i = 0; i < covers.length(); i++) { JSONObject image = covers.optJSONObject(i); String url = J.text(image, "url"); int delta = Math.abs(image.optInt("width", 64) - 64); if (url.startsWith("https://") && delta < distance) { cover = url; distance = delta; } }
            long milliseconds = J.obj(object, "duration").optLong("totalMilliseconds", J.obj(object, "trackDuration").optLong("totalMilliseconds"));
            String owner = J.text(J.obj(J.obj(object, "ownerV2"), "data"), "username");
            boolean editable = object.optBoolean("isOwnedBySelf") || !owner.isEmpty() && owner.equals(username);
            Song row = new Song(J.o("uri", uri, "uid", J.text(object, "uid"), "name", name, "artist", kind.equals("library") ? "Плейлист" : artist.toString(), "album", J.text(album, "name"), "cover", cover, "duration", milliseconds == 0 ? "" : (milliseconds / 60000) + ":" + String.format(java.util.Locale.ROOT, "%02d", milliseconds / 1000 % 60), "saved", kind.equals("liked"), "owner", owner, "editable", editable));
            rows.put(kind.equals("playlist") ? uri + '#' + row.uid : uri, row);
            return;
        }
        java.util.Iterator<String> iterator = object.keys(); while (iterator.hasNext()) collect(object.opt(iterator.next()), kind, rows, depth + 1);
    }
    boolean saved(String uri) throws Exception { JSONArray found = J.array(operation("saved", J.o("uris", new JSONArray(java.util.Collections.singletonList(uri)))), "lookup"); return J.obj(found.optJSONObject(0), "data").optBoolean("saved"); }
    void favorite(String uri, boolean save) throws Exception {
        JSONObject variables = J.o("libraryItemUris", new JSONArray(java.util.Collections.singletonList(uri))); if (save) variables.put("interactionId", JSONObject.NULL);
        operation(save ? "addFavorite" : "removeFavorite", variables);
        if (saved(uri) != save) throw new IllegalStateException("CatalogChangeNotConfirmed");
        synchronized (this) { entries.remove("liked:"); }
    }
    void addToPlaylist(String uri, String playlist) throws Exception {
        java.util.Set<String> previous = new java.util.HashSet<>(); for (Song row : query("playlist", playlist, true)) previous.add(row.uid); Song expected = track(uri);
        JSONObject mutation = operation("addPlaylist", J.o("playlistItemUris", new JSONArray(java.util.Collections.singletonList(uri)), "playlistUri", playlist, "newPosition", J.o("moveType", "BOTTOM_OF_PLAYLIST", "fromUid", JSONObject.NULL), "interactionId", JSONObject.NULL));
        if (mutation.toString().contains("NotFound") || mutation.toString().contains("PermissionDenied")) throw new IllegalStateException("PlaylistPermissionDenied");
        for (int retry = 0; retry < 4; retry++) { for (Song row : query("playlist", playlist, true)) if (!row.uid.isEmpty() && !previous.contains(row.uid) && (row.uri.equals(uri) || expected != null && row.name.equals(expected.name) && row.artist.equals(expected.artist))) return; Thread.sleep(500); }
        throw new IllegalStateException("CatalogChangeNotConfirmed");
    }
    void removeFromPlaylist(Song song, String playlist) throws Exception { if (song.uid.isEmpty()) throw new IllegalStateException("CatalogItemMissing"); operation("removePlaylist", J.o("playlistUri", playlist, "uids", new JSONArray(java.util.Collections.singletonList(song.uid)))); List<Song> rows = query("playlist", playlist, true); for (Song row : rows) if (row.uid.equals(song.uid)) throw new IllegalStateException("CatalogChangeNotConfirmed"); }
}
