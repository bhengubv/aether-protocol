plugins {
    kotlin("jvm") version "1.9.21"
    application
    // kotlinx-benchmark — the Kotlin-native bench harness. Mirrors the
    // tinybench (TS), pytest-benchmark (Python), Go testing.B, and
    // BenchmarkDotNet (C#) harnesses across the implementation family.
    // Configures a `benchmark` source set, registers tasks via the
    // benchmarks {} extension below, and runs the JMH machinery
    // underneath so the numbers are JMH-comparable.
    id("org.jetbrains.kotlinx.benchmark") version "0.4.10"
    // Required by kotlinx-benchmark for JVM bench source sets — the
    // plugin uses the allopen plugin to relax the `final` restriction
    // on @State-annotated bench classes (JMH instantiates them).
    kotlin("plugin.allopen") version "1.9.21"
}

group = "dev.aether"
version = "2.0.0"

repositories {
    mavenCentral()
}

// ─── Source sets ─────────────────────────────────────────────────────────
//
// `benchmark` is a third source set that compiles against `main` only.
// Production runtime code never depends on it — it lives outside the
// main classpath and is only assembled when the `benchmark` task runs.
sourceSets {
    create("benchmark") {
        kotlin.srcDir("src/jmh/kotlin")
        compileClasspath += sourceSets.main.get().output
        runtimeClasspath += sourceSets.main.get().output
    }
}

val benchmarkImplementation: Configuration by configurations.getting {
    extendsFrom(configurations.implementation.get())
}

dependencies {
    // BouncyCastle for Ed25519 and crypto operations
    implementation("org.bouncycastle:bcprov-jdk18on:1.76")
    implementation("org.bouncycastle:bcpkix-jdk18on:1.76")

    // Coroutines for async operations
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3")

    // Kotlin stdlib
    implementation(kotlin("stdlib"))

    // Logging
    implementation("org.slf4j:slf4j-api:2.0.9")
    implementation("org.slf4j:slf4j-simple:2.0.9")

    // Testing
    testImplementation(kotlin("test"))
    testImplementation("org.junit.jupiter:junit-jupiter:5.10.0")

    // Property-based testing — kotest-property mirrors the fast-check
    // (TS), Hypothesis (Python), and quickcheck-style harnesses used
    // across the implementation family. We only depend on kotest-property
    // + the JUnit5 runner; we do NOT pull in kotest-assertions because
    // the existing tests use the kotlin.test assertion surface and we
    // don't want two dialects in the same suite.
    testImplementation("io.kotest:kotest-runner-junit5:5.8.0")
    testImplementation("io.kotest:kotest-property:5.8.0")

    // Benchmark runtime — kotlinx-benchmark layers a friendly DSL over
    // JMH and ships the JMH runtime jars transitively.
    "benchmarkImplementation"("org.jetbrains.kotlinx:kotlinx-benchmark-runtime:0.4.10")
}

kotlin {
    // Targeting JVM 11 bytecode (minimum supported by the protocol family).
    // We do NOT use jvmToolchain(N) here — that requires an exact JDK N
    // installation registered with Gradle's toolchain scanner, which may
    // not be available in all local dev environments.  Instead we configure
    // the Kotlin compiler's jvmTarget directly so any JDK 11+ running the
    // build will produce 11-compatible bytecode.
    // CI installs Java 21 (see .github/workflows/ci.yml) which works fine.
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11
    }
}

java {
    sourceCompatibility = JavaVersion.VERSION_11
    targetCompatibility = JavaVersion.VERSION_11
}

application {
    mainClass.set("aether.DemoKt")
}

tasks.test {
    useJUnitPlatform()
}

// ─── kotlinx-benchmark wiring ────────────────────────────────────────────
//
// `benchmarks { targets { register("benchmark") {} } }` tells the plugin
// to generate JMH harness wiring for the `benchmark` source set. Once
// generated, run the suite with:
//
//     ./gradlew benchmark
//
// The plugin prints a JMH-format summary table to stdout — same shape as
// the TS / Python / Go / C tables so cross-language regression diffs
// stay apples-to-apples.
//
// allOpen makes @State classes effectively-open so JMH can subclass
// them for measurement instrumentation. Without this, kotlinx-benchmark's
// generated code fails to compile against `final` Kotlin classes.
benchmark {
    targets {
        register("benchmark") {
            this as kotlinx.benchmark.gradle.JvmBenchmarkTarget
            jmhVersion = "1.37"
        }
    }
    configurations {
        named("main") {
            warmups = 3
            iterations = 5
            iterationTime = 500
            iterationTimeUnit = "ms"
            outputTimeUnit = "us"
            mode = "avgt"
            reportFormat = "text"
        }
    }
}

allOpen {
    annotation("org.openjdk.jmh.annotations.State")
}
