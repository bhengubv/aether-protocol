package aethermesh.blue

import org.junit.Assert.assertTrue
import org.junit.Test

class AetherMeshBlueTest {
    @Test fun packageName_hasAetherMeshPrefix() = assertTrue("aethermesh.blue".startsWith("aether"))
    @Test fun versionName_isNonEmpty() = assertTrue("1.0".isNotEmpty())
}
