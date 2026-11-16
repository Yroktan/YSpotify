package app.yspotify;

import org.junit.Test;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import static org.junit.Assert.*;

public class PageAccumulatorTest {
    private List<Song> songs(int from, int count) { List<Song> rows = new ArrayList<>(); for (int i = from; i < from + count; i++) rows.add(new Song(J.o("uri", "spotify:track:" + String.format(java.util.Locale.ROOT, "%022d", i), "name", "Song " + i))); return rows; }
    @Test public void unavailableSongsDoNotEndPagination() {
        PageAccumulator pages = new PageAccumulator(); List<Song> first = songs(0, 100); first.remove(15);
        assertFalse(pages.accept(0, 100, 256, 100, first, false)); assertEquals(100, pages.offset());
        assertFalse(pages.accept(100, 100, 256, 100, Collections.emptyList(), false));
        assertTrue(pages.accept(200, 100, 256, 56, songs(200, 56), false)); assertEquals(155, pages.rows().size());
    }
    @Test public void serverLimitsAreRespected() { PageAccumulator pages = new PageAccumulator(); assertFalse(pages.accept(0, 50, 66, 50, songs(0, 50), false)); assertTrue(pages.accept(50, 50, 66, 16, songs(50, 16), false)); assertEquals(66, pages.rows().size()); }
    @Test public void incompletePagesAreRejected() { PageAccumulator pages = new PageAccumulator(); assertThrows(IllegalStateException.class, () -> pages.accept(0, 100, 256, 20, songs(0, 20), false)); }
    @Test public void changedTotalIsRejected() { PageAccumulator pages = new PageAccumulator(); pages.accept(0, 100, 256, 100, songs(0, 100), false); assertThrows(IllegalStateException.class, () -> pages.accept(100, 100, 255, 100, songs(100, 100), false)); }
    @Test public void repeatedPlaylistTracksKeepDistinctUids() { PageAccumulator pages = new PageAccumulator(); Song a = new Song(J.o("uri", "spotify:track:0000000000000000000001", "uid", "first", "name", "Same song")); Song b = new Song(J.o("uri", a.uri, "uid", "second", "name", a.name)); assertTrue(pages.accept(0, 100, 2, 2, java.util.Arrays.asList(a, b), true)); assertEquals(2, pages.rows().size()); }
    @Test public void emptyLibraryIsACompleteResult() { PageAccumulator pages = new PageAccumulator(); assertTrue(pages.accept(0, 100, 0, 0, Collections.emptyList(), false)); assertTrue(pages.rows().isEmpty()); }
}
