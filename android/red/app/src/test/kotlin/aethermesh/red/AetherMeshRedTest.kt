package aethermesh.red

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherMeshRedTest {
    @Test fun packageName_hasAetherMeshPrefix() = assertTrue("aethermesh.red".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
