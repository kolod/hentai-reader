// :host — the UI-free core of the Android app (engine bridge, HTTP, HTML, storage, reading,
// archives, database, library, downloads). Plain Kotlin/JVM, so the parity suite and the unit
// tests run on the development machine and in CI without a device (research R1, R11).
plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
}

kotlin {
    jvmToolchain(21)
}

java {
    // The APK runs this code on Android 8.0+ (ART reads Java 17 bytecode with desugaring).
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>().configureEach {
    compilerOptions.jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
}

dependencies {
    implementation(libs.jsoup)
    implementation(libs.okhttp)
    implementation(libs.sqlite.bundled)
    implementation(libs.coroutines.core)
    api(libs.serialization.json)

    testImplementation(platform(libs.junit.bom))
    testImplementation(libs.junit.jupiter)
    testImplementation(libs.coroutines.test)
    testRuntimeOnly(libs.junit.launcher)
}

val repoRoot = rootDir.parentFile
val desktopNative = project(":quickjs").layout.buildDirectory.dir("desktop")

// Contract version this host implements, taken from contract/package.json at build time.
val generateHostInfo = tasks.register("generateHostInfo") {
    val packageJson = repoRoot.resolve("contract/package.json")
    val out = layout.buildDirectory.dir("generated/hostinfo")
    inputs.file(packageJson)
    outputs.dir(out)
    doLast {
        val version = Regex(""""version"\s*:\s*"([^"]+)"""").find(packageJson.readText())!!.groupValues[1]
        val file = out.get().file("hentaireader/host/HostInfo.kt").asFile
        file.parentFile.mkdirs()
        file.writeText(
            """
            |package hentaireader.host
            |
            |/** Contract version this host implements (contract/package.json, generated). */
            |object HostInfo {
            |    const val CONTRACT_VERSION = "$version"
            |}
            |""".trimMargin()
        )
    }
}
kotlin.sourceSets["main"].kotlin.srcDir(generateHostInfo)

tasks.test {
    useJUnitPlatform()
    dependsOn(":quickjs:buildDesktopNative")
    // The parity suite reads the shared inputs in place; a changed script, fixture or snapshot
    // must re-run it.
    inputs.files(fileTree(repoRoot.resolve("contract")) { exclude("node_modules/**", "schema/**") })
        .withPropertyName("contract").withPathSensitivity(PathSensitivity.RELATIVE)
    inputs.dir(repoRoot.resolve("sources")).withPropertyName("sources").withPathSensitivity(PathSensitivity.RELATIVE)
    inputs.dir(repoRoot.resolve("windows/src/HentaiReader.App/Strings")).withPropertyName("strings").withPathSensitivity(PathSensitivity.RELATIVE)
    systemProperty("repoRoot", repoRoot.absolutePath)
    systemProperty("java.library.path", desktopNative.get().asFile.absolutePath)
    maxHeapSize = "2g"
    testLogging {
        events("failed")
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}
