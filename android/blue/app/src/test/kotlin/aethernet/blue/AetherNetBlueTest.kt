package aethernet.blue

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherNetBlueTest {
    @Test fun packageName_hasAetherNetPrefix() = assertTrue("aethernet.blue".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
