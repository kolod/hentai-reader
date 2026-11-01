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
        root.resolve("sources").listFiles()?.filter { it.resolve("${it.name}.js").exists() }?.forEach { dir ->
            dir.resolve("${dir.name}.js").copyTo(out.resolve("sources/${dir.name}/${dir.name}.js"))
        }
    }
}

val packAssets = tasks.register<PackAssets>("packAssets") {
    repo.set(repoRoot)
    sourceFiles.from(repoRoot.resolve("contract/prelude.js"), repoRoot.resolve("contract/host-glue.js"))
    sourceFiles.from(fileTree(repoRoot.resolve("sources")) { include("*/*.js") })
    output.set(layout.buildDirectory.dir("generated/hrassets"))
}

// Release signing: environment (CI secrets) or a git-ignored android/keystore.properties.
val keystoreProps = Properties().apply {
    rootDir.resolve("keystore.properties").takeIf { it.exists() }?.inputStream()?.use { load(it) }
}
fun signing(name: String, env: String): String? = System.getenv(env) ?: keystoreProps.getProperty(name)

android {
    namespace = "hentaireader.app"
    compileSdk = libs.versions.compileSdk.get().toInt()
    ndkVersion = libs.versions.ndk.get()

    defaultConfig {
        applicationId = "hentaireader.app"
        minSdk = libs.versions.minSdk.get().toInt()
        targetSdk = libs.versions.targetSdk.get().toInt()
        versionCode = providers.gradleProperty("versionCode").get().toInt()
        versionName = providers.gradleProperty("versionName").get()
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64") }
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        val store = signing("storeFile", "HR_KEYSTORE")
        if (store != null) {
            create("release") {
                storeFile = file(store)
                storePassword = signing("storePassword", "HR_KEYSTORE_PASSWORD")
                keyAlias = signing("keyAlias", "HR_KEY_ALIAS")
                keyPassword = signing("keyPassword", "HR_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release")
        }
    }

    buildFeatures {
        compose = true
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
}
