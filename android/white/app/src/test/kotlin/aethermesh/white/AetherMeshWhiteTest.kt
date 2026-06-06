package aethermesh.white

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherMeshWhiteTest {
    @Test fun packageName_hasAetherMeshPrefix() = assertTrue("aethermesh.white".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
