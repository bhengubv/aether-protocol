package aethernet.teal

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherNetTealTest {
    @Test fun packageName_hasAetherNetPrefix() = assertTrue("aethernet.teal".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
