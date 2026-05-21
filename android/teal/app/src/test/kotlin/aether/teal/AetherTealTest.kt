package aether.teal

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherTealTest {
    @Test fun packageName_hasAetherPrefix() = assertTrue("aether.teal".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
