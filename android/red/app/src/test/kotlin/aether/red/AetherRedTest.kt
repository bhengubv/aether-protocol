package aether.red

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherRedTest {
    @Test fun packageName_hasAetherPrefix() = assertTrue("aether.red".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
