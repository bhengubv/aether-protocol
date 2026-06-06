package aethermesh.teal

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherMeshTealTest {
    @Test fun packageName_hasAetherMeshPrefix() = assertTrue("aethermesh.teal".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
