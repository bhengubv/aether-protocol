package aethernet.red

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherNetRedTest {
    @Test fun packageName_hasAetherNetPrefix() = assertTrue("aethernet.red".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
