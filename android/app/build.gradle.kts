import java.util.Properties

// :app — the Android application: Compose UI and the Android glue around :host
// (specs/007-android-app plan). Shared inputs (contract scripts, bundled sources) are packed into
// the APK's assets from their canonical places at build time, never copied into src/.
plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

val repoRoot = rootDir.parentFile

/** Copies contract/prelude.js, contract/host-glue.js and every sources/<id>/<id>.js into generated assets. */
abstract class PackAssets : DefaultTask() {
    @get:InputFiles
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val sourceFiles: ConfigurableFileCollection

    @get:Internal
    abstract val repo: DirectoryProperty

    @get:OutputDirectory
    abstract val output: DirectoryProperty

    @TaskAction
    fun pack() {
        val out = output.get().asFile
        out.deleteRecursively()
        val root = repo.get().asFile
        for (name in listOf("prelude.js", "host-glue.js")) {
            root.resolve("contract/$name").copyTo(out.resolve("contract/$name"))
        }
        // Shown under Settings → About (licence texts that must travel with the app).
        root.resolve("android/THIRD-PARTY-NOTICES.md").copyTo(out.resolve("THIRD-PARTY-NOTICES.md"))
        root.resolve("sources").listFiles()?.filter { it.resolve("${it.name}.js").exists() }?.forEach { dir ->
            dir.resolve("${dir.name}.js").copyTo(out.resolve("sources/${dir.name}/${dir.name}.js"))
        }
    }
}

val packAssets = tasks.register<PackAssets>("packAssets") {
    repo.set(repoRoot)
    sourceFiles.from(repoRoot.resolve("contract/prelude.js"), repoRoot.resolve("contract/host-glue.js"))
    sourceFiles.from(repoRoot.resolve("android/THIRD-PARTY-NOTICES.md"))
    sourceFiles.from(fileTree(repoRoot.resolve("sources")) { include("*/*.js") })
    output.set(layout.buildDirectory.dir("generated/hrassets"))
}

// Written by .github/workflows/android-release.yml; never committed (see .gitignore). The same
// release system as the other Android projects (circuitopedia).
val keystorePropertiesFile = file("keystore.properties")

android {
    namespace = "hentaireader.app"
    compileSdk = libs.versions.compileSdk.get().toInt()
    ndkVersion = libs.versions.ndk.get()

    defaultConfig {
        applicationId = "io.github.kolod.hentaireader"
        minSdk = libs.versions.minSdk.get().toInt()
        targetSdk = libs.versions.targetSdk.get().toInt()
        // From the release tag (android-vX.Y.Z) and the workflow run number; local builds are dev builds.
        versionCode = providers.gradleProperty("appVersionCode").orNull?.toInt() ?: 1
        versionName = providers.gradleProperty("appVersionName").orNull ?: "0.1.0-dev"
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64") }
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        if (keystorePropertiesFile.isFile) {
            val keystore = Properties().apply { keystorePropertiesFile.inputStream().use(::load) }
            create("release") {
                storeFile = file(keystore.getProperty("storeFile"))
                storePassword = keystore.getProperty("storePassword")
                keyAlias = keystore.getProperty("keyAlias")
                keyPassword = keystore.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfigs.findByName("release")?.let { signingConfig = it }
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    lint {
        // Generated shared strings carry Windows-only keys (F11, Explorer); they are unused here.
        disable += "UnusedResources"
        // Distributed as an APK, not an app bundle: no language splits to download.
        disable += "AppBundleLocaleChanges"
        abortOnError = true
    }
}

androidComponents {
    onVariants { variant ->
        variant.sources.assets?.addGeneratedSourceDirectory(packAssets, PackAssets::output)
    }
}

dependencies {
    implementation(project(":host"))
    implementation(project(":quickjs"))

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons)
    implementation(libs.activity.compose)
    implementation(libs.lifecycle.runtime.compose)
    implementation(libs.lifecycle.viewmodel.compose)
    implementation(libs.navigation.compose)
    implementation(libs.coroutines.android)
    implementation(libs.serialization.json)
    debugImplementation(libs.compose.ui.tooling)
    debugImplementation(libs.compose.ui.test.manifest)
    androidTestImplementation(platform(libs.compose.bom))
    androidTestImplementation(libs.compose.ui.test.junit4)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.test.junit)
    // Compose's UI test pulls an older Espresso that fails on API 36 (InputManager.getInstance).
    androidTestImplementation(libs.androidx.test.espresso)
}
