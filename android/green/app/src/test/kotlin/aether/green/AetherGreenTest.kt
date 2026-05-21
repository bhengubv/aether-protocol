package aether.green

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherGreenTest {
    @Test fun packageName_hasAetherPrefix() = assertTrue("aether.green".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
