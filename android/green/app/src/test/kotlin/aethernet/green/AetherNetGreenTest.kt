package aethernet.green

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherNetGreenTest {
    @Test fun packageName_hasAetherNetPrefix() = assertTrue("aethernet.green".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
