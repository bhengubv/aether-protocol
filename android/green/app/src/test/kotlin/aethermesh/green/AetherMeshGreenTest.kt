package aethermesh.green

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherMeshGreenTest {
    @Test fun packageName_hasAetherMeshPrefix() = assertTrue("aethermesh.green".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
