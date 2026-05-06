plugins {
    kotlin("jvm") version "1.9.21"
    application
}

group = "dev.aether"
version = "2.0.0"

repositories {
    mavenCentral()
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
    implementation("org.slf4j:slf4j-api:2.0.17")
    implementation("org.slf4j:slf4j-simple:2.0.17")

    // Testing
    testImplementation(kotlin("test"))
    testImplementation("org.junit.jupiter:junit-jupiter:5.10.0")
}

kotlin {
    jvmToolchain(17)
}

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

application {
    mainClass.set("aether.DemoKt")
}

tasks.test {
    useJUnitPlatform()
}
