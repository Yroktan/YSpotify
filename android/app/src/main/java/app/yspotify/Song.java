package app.yspotify;

import org.json.JSONObject;

final class Song {
    final String uri, uid, name, artist, album, cover, duration, owner;
    final boolean saved, editable;
    Song(JSONObject value) {
        uri = J.text(value, "uri"); uid = J.text(value, "uid"); name = J.text(value, "name"); artist = J.text(value, "artist");
        album = J.text(value, "album"); cover = J.text(value, "cover"); duration = J.text(value, "duration"); owner = J.text(value, "owner");
        saved = value.optBoolean("saved"); editable = value.optBoolean("editable");
    }
    JSONObject json() { return J.o("uri", uri, "uid", uid, "name", name, "artist", artist, "album", album, "cover", cover, "duration", duration, "owner", owner, "saved", saved, "editable", editable); }
    boolean playlist() { return uri.startsWith("spotify:playlist:"); }
}
