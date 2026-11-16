package app.yspotify;

import org.json.JSONArray;
import org.json.JSONObject;

final class J {
    static String read(java.io.InputStream input) throws java.io.IOException {
        java.io.ByteArrayOutputStream output = new java.io.ByteArrayOutputStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = input.read(buffer)) != -1) output.write(buffer, 0, count);
        return output.toString("UTF-8");
    }
    static JSONObject o(Object... values) {
        JSONObject result = new JSONObject();
        try { for (int i = 0; i < values.length; i += 2) result.put((String) values[i], values[i + 1]); }
        catch (Exception error) { throw new IllegalArgumentException(error); }
        return result;
    }
    static JSONObject parse(String value) {
        try { return new JSONObject(value); } catch (Exception error) { throw new IllegalArgumentException(error); }
    }
    static JSONObject obj(JSONObject object, String key) {
        JSONObject result = object == null ? null : object.optJSONObject(key);
        return result == null ? new JSONObject() : result;
    }
    static JSONArray array(JSONObject object, String key) {
        JSONArray result = object == null ? null : object.optJSONArray(key);
        return result == null ? new JSONArray() : result;
    }
    static String text(JSONObject object, String key) { return object == null ? "" : object.optString(key, ""); }
}
