package aether.white

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherWhiteTest {
    @Test fun packageName_hasAetherPrefix() = assertTrue("aether.white".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
