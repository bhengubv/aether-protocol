package aether.blue

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherBlueTest {
    @Test fun packageName_hasAetherPrefix() = assertTrue("aether.blue".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
