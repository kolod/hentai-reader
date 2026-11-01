// :quickjs — vendored QuickJS + JNI shim (CMakeLists.txt). The Android library packs
// libhrquickjs.so for the APK; buildDesktopNative builds the same sources for the development
// machine so :host tests load the engine the APK ships (research R2, R11).
plugins {
    alias(libs.plugins.android.library)
}

android {
    namespace = "hentaireader.quickjs"
    compileSdk = libs.versions.compileSdk.get().toInt()
    ndkVersion = libs.versions.ndk.get()
    defaultConfig {
        minSdk = libs.versions.minSdk.get().toInt()
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64") }
        externalNativeBuild { cmake { arguments += "-DANDROID_STL=none" } }
    }
    externalNativeBuild {
        cmake { path = file("CMakeLists.txt") }
    }
}

val desktopDir = layout.buildDirectory.dir("desktop")
val windowsHost = System.getProperty("os.name").startsWith("Windows")

val configureDesktopNative = tasks.register<Exec>("configureDesktopNative") {
    inputs.file("CMakeLists.txt")
    outputs.file(desktopDir.map { it.file("CMakeCache.txt") })
    val args = mutableListOf("cmake", "-S", projectDir.absolutePath, "-B", desktopDir.get().asFile.absolutePath, "-DCMAKE_BUILD_TYPE=Release")
    if (windowsHost) args += listOf("-G", "Ninja", "-DCMAKE_C_COMPILER=gcc")
    commandLine(args)
}

tasks.register<Exec>("buildDesktopNative") {
    dependsOn(configureDesktopNative)
    inputs.dir("src/main/cpp")
    outputs.dir(desktopDir)
    commandLine("cmake", "--build", desktopDir.get().asFile.absolutePath, "--config", "Release")
}
