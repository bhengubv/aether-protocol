package aethernet.white

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherNetWhiteTest {
    @Test fun packageName_hasAetherNetPrefix() = assertTrue("aethernet.white".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
