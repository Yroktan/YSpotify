package app.yspotify;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;

final class PageAccumulator {
    private final LinkedHashMap<String, Song> rows = new LinkedHashMap<>();
    private int total = -1, offset;
    int offset() { return offset; }
    int total() { return total; }
    boolean accept(int pageOffset, int limit, int pageTotal, int received, List<Song> parsed, boolean playlist) {
        if (pageTotal < 0 || pageTotal > 10000 || limit < 1 || limit > 1000 || pageOffset != offset) throw new IllegalStateException("CatalogPageInvalid");
        if (total == -1) total = pageTotal;
        if (total != pageTotal) throw new IllegalStateException("CatalogChangedDuringLoad");
        int expected = Math.min(limit, total - offset);
        if (received != expected) throw new IllegalStateException("CatalogIncomplete");
        for (Song song : parsed) rows.put(playlist ? song.uri + '#' + song.uid : song.uri, song);
        offset += expected;
        if (offset < total && expected == 0) throw new IllegalStateException("CatalogIncomplete");
        return offset == total;
    }
    List<Song> rows() { return new ArrayList<>(rows.values()); }
}
