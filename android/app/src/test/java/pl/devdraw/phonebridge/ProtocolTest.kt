package pl.devdraw.phonebridge

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SasTest {
    @Test
    fun `matches cross-platform test vector`() {
        // Ten sam wektor sprawdza PhoneBridge.Tests (PairingTests.cs) – obie strony muszą pokazać ten sam kod.
        val phoneNonce = ByteArray(32) { it.toByte() }
        val serverNonce = ByteArray(32) { (it + 32).toByte() }

        val code = Sas.compute("a".repeat(64), "b".repeat(64), phoneNonce, serverNonce)

        assertEquals("747696", code)
        assertEquals("747 696", Sas.format(code))
    }

    @Test
    fun `hex round trip`() {
        val bytes = ByteArray(256) { it.toByte() }
        assertTrue(Hex.encode(bytes).startsWith("000102"))
        assertEquals(bytes.toList(), Hex.decode(Hex.encode(bytes)).toList())
        assertTrue(Hex.isHex("AB".repeat(32)))
        assertTrue(!Hex.isHex("zz".repeat(32)))
    }
}

class FileNamesTest {
    @Test
    fun `sanitizes forbidden characters`() {
        assertEquals("a_b_c.txt", FileNames.sanitize("a:b?c.txt"))
        assertEquals("faktura_gpj.exe", FileNames.sanitize("faktura‮gpj.exe"))
        assertEquals("plik", FileNames.sanitize(".."))
        assertEquals("plik", FileNames.sanitize("   "))
        assertEquals("nazwa", FileNames.sanitize("nazwa. ."))
        assertEquals("zdjęcie.jpg", FileNames.sanitize("zdjęcie.jpg"))
    }

    @Test
    fun `relative paths cannot escape destination`() {
        assertEquals(listOf("plik", "plik", "etc", "passwd"), FileNames.segments("../../etc/passwd"))
        assertEquals(listOf("Zdjęcia", "2026", "a.jpg"), FileNames.segments("Zdjęcia/2026/a.jpg"))
        assertEquals(listOf("a", "b.txt"), FileNames.segments("a\\.\\b.txt"))
        assertEquals(listOf("plik"), FileNames.segments("///"))
    }

    @Test
    fun `limits depth and length`() {
        val deep = (1..50).joinToString("/") { "d$it" } + "/x.txt"
        val segments = FileNames.segments(deep)
        assertEquals(Protocol.MAX_DEPTH, segments.size)
        assertEquals("x.txt", segments.last())

        val long = FileNames.sanitize("x".repeat(500) + ".jpeg")
        assertEquals(200, long.length)
        assertTrue(long.endsWith(".jpeg"))
    }
}

class OfferParsingTest {
    private val id = "0123456789abcdef0123456789abcdef"

    @Test
    fun `parses valid offer`() {
        val json = JSONObject("""{"type":"offer","id":"$id","files":[{"name":"Folder/a.jpg","size":10},{"name":"b.txt","size":0}]}""")
        val offer = Protocol.parseOffer(json)
        assertNotNull(offer)
        assertEquals(2, offer!!.files.size)
        assertEquals(10L, offer.totalBytes)
    }

    @Test
    fun `rejects malformed offers`() {
        assertNull(Protocol.parseOffer(JSONObject("""{"id":"short","files":[{"name":"a","size":1}]}""")))
        assertNull(Protocol.parseOffer(JSONObject("""{"id":"$id","files":[]}""")))
        assertNull(Protocol.parseOffer(JSONObject("""{"id":"$id","files":[{"name":"a","size":-1}]}""")))
        assertNull(Protocol.parseOffer(JSONObject("""{"id":"$id","files":[{"name":"","size":1}]}""")))
        assertNull(Protocol.parseOffer(JSONObject("""{"id":"$id"}""")))
    }
}

class ScreenshotPathTest {
    @Test
    fun `recognizes screenshot folders of different vendors`() {
        assertTrue(ScreenshotWatcher.isScreenshotPath("Pictures/Screenshots/"))
        assertTrue(ScreenshotWatcher.isScreenshotPath("DCIM/Screenshots/"))
        assertTrue(ScreenshotWatcher.isScreenshotPath("DCIM/screenshot/"))
        assertTrue(!ScreenshotWatcher.isScreenshotPath("DCIM/Camera/"))
        assertTrue(!ScreenshotWatcher.isScreenshotPath(null))
    }
}

class FormatTest {
    @Test
    fun `polish plurals`() {
        assertEquals("1 plik", Format.files(1))
        assertEquals("3 pliki", Format.files(3))
        assertEquals("5 plików", Format.files(5))
        assertEquals("12 plików", Format.files(12))
        assertEquals("22 pliki", Format.files(22))
        assertEquals("112 plików", Format.files(112))
    }

    @Test
    fun `byte sizes`() {
        assertEquals("512 B", Format.bytes(512))
        assertEquals("1,5 KB", Format.bytes(1536))
        assertEquals("10 MB", Format.bytes(10L * 1024 * 1024))
    }
}
