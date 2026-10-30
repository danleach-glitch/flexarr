import com.github.triplet.gradle.androidpublisher.ResolutionStrategy
import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.baselineprofile)
    alias(libs.plugins.play.publisher)
    alias(libs.plugins.sentry)
}

// Notifications come through Firebase, set up from the Firebase project's google-services.json beside this file (its
// API key only identifies the project, so it can be committed). Without it the app builds and runs, and Settings says
// notifications aren't available in this build.
if (file("google-services.json").exists()) apply(plugin = "com.google.gms.google-services")

// The app's version is the server's (Directory.Build.props), so it isn't one more place release.sh has to change:
// 0.52.0 is versionName "0.52.0" and versionCode 52000.
// -Pflexarr.version=0.53.0 builds as that version instead: Google Play builds from main between releases pass the next one.
val serverVersion: String = providers.gradleProperty("flexarr.version").orNull
    ?: Regex("<VersionPrefix>([^<]+)</VersionPrefix>")
        .find(rootDir.resolve("../../Directory.Build.props").readText())!!.groupValues[1]
val versionParts = serverVersion.split(".").map { it.toInt() }

// Google Play builds are signed with the upload key, and Google signs what people download with the app's own key. The
// keystore and its passwords stay out of the repo: ~/.flexarr-release/android-upload.properties (storeFile, storePassword,
// keyAlias, keyPassword), or the file FLEXARR_ANDROID_SIGNING names. Without one, release builds come out unsigned.
val uploadSigning = Properties().apply {
    val path = System.getenv("FLEXARR_ANDROID_SIGNING") ?: "${System.getProperty("user.home")}/.flexarr-release/android-upload.properties"
    File(path).takeIf { it.exists() }?.reader()?.use { load(it) }
}

android {
    namespace = "io.github.flexarr"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.danleach.flexarr"
        minSdk = 26
        targetSdk = 36
        versionName = serverVersion
        versionCode = versionParts[0] * 1_000_000 + versionParts[1] * 1_000 + versionParts[2]
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        // Signing in from launch arguments (MainActivity): debug builds, and the builds the baseline profile is made with.
        buildConfigField("boolean", "LAUNCH_ARGUMENTS", "false")
        // The TV interface (ui/tv), while it's being built: debug builds only, and only debug's manifest offers the app
        // to TVs (src/debug/AndroidManifest.xml). docs/android-tv.md.
        buildConfigField("boolean", "TV_UI", "false")
    }

    signingConfigs {
        if (!uploadSigning.isEmpty) create("upload") {
            storeFile = file(uploadSigning.getProperty("storeFile"))
            storePassword = uploadSigning.getProperty("storePassword")
            keyAlias = uploadSigning.getProperty("keyAlias")
            keyPassword = uploadSigning.getProperty("keyPassword")
        }
    }

    buildTypes {
        debug {
            buildConfigField("boolean", "LAUNCH_ARGUMENTS", "true")
            buildConfigField("boolean", "TV_UI", "true")
        }
        // The baseline profile plugin's builds of release, made only to profile on a device: signed with the debug key.
        configureEach {
            if (name == "nonMinifiedRelease" || name == "benchmarkRelease") signingConfig = signingConfigs.getByName("debug")
        }
        release {
            signingConfig = signingConfigs.findByName("upload")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    // English only for now, so the libraries' own words (Material's, say) don't turn up in other languages beside it.
    androidResources {
        localeFilters += "en"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // Unit tests (src/test, `./gradlew :app:testDebugUnitTest`) run on the computer, without a device: plain JVM tests of
    // the app's own logic, and Robolectric for the few that need Android's classes (a Uri, an Intent). They read the
    // API's real answers shared with FlexarrKit's tests, as the core's do.
    sourceSets.getByName("test").resources.srcDir("../../apple/FlexarrKit/Tests/FlexarrKitTests/Fixtures")

    // Lint fails the build on new errors; the ones already there when it joined CI are listed in lint-baseline.xml
    // (`./gradlew :app:updateLintBaseline` lists them again, after fixing some).
    lint {
        baseline = file("lint-baseline.xml")
        abortOnError = true
    }
}

// Release builds are minified, so their stack traces in Sentry are readable only with that build's ProGuard mapping.
// Each release build carries an id for its mapping, and uploads the mapping to Sentry (dan-leach/flexarr-android) when
// there's a token for it, in SENTRY_AUTH_TOKEN or ~/.sentryclirc (as make ios-dsyms). Without one (CI, forks) nothing is
// uploaded and nothing needs the network, and those builds' reports stay obfuscated. -Pflexarr.sentryUpload=false skips
// the upload anyway. The plugin only does this: the SDK stays the one in the dependencies below, set up by ErrorReports.
val sentryToken = !System.getenv("SENTRY_AUTH_TOKEN").isNullOrBlank() ||
    File(System.getProperty("user.home"), ".sentryclirc").let { it.exists() && it.readLines().any { line -> line.startsWith("token=") } }
sentry {
    org.set("dan-leach")
    projectName.set("flexarr-android")
    includeProguardMapping.set(true)
    autoUploadProguardMapping.set(sentryToken && providers.gradleProperty("flexarr.sentryUpload").orNull != "false")
    uploadNativeSymbols.set(false)
    includeNativeSources.set(false)
    includeSourceContext.set(false)
    includeDependenciesReport.set(false)
    telemetry.set(false)
    // Debug builds aren't minified, and the baseline profile's builds are only for profiling.
    ignoredBuildTypes.set(setOf("debug"))
    ignoredVariants.set(setOf("nonMinifiedRelease", "benchmarkRelease"))
    autoInstallation {
        enabled.set(false)
    }
    tracingInstrumentation {
        enabled.set(false)
    }
}

// The baseline profile's builds take launch arguments too, to sign in (the plugin copies release's fields over them, so
// they're set per variant). They have the TV interface as well: benchmarkRelease is release as it ships (R8, no
// debugging) but profileable, so it's the build to measure the TV interface with on a real stick
// (`./gradlew :app:installBenchmarkRelease`, then Android Studio's profiler or Perfetto), and the TV journey in the
// baseline profile is recorded with it.
androidComponents {
    onVariants { variant ->
        if (variant.name == "nonMinifiedRelease" || variant.name == "benchmarkRelease") {
            variant.buildConfigFields?.put("LAUNCH_ARGUMENTS", com.android.build.api.variant.BuildConfigField("boolean", "true", null))
            variant.buildConfigFields?.put("TV_UI", com.android.build.api.variant.BuildConfigField("boolean", "true", null))
        }
    }
}

// Uploads to Google Play (make android-upload) and the store listing in src/main/play. Play Console signs in with a
// service account's JSON key: ~/.flexarr-release/play-service-account.json, or its contents in ANDROID_PUBLISHER_CREDENTIALS.
// Each upload needs a higher version code than any before it, so one that's taken is raised past the highest. That asks
// Play on every release build, so without a key the plugin stays off and release builds keep their own version code.
val playKey = File(System.getProperty("user.home"), ".flexarr-release/play-service-account.json")
play {
    enabled.set(playKey.exists() || System.getenv("ANDROID_PUBLISHER_CREDENTIALS") != null)
    if (playKey.exists()) serviceAccountCredentials.set(playKey)
    defaultToAppBundles.set(true)
    track.set(providers.gradleProperty("flexarr.track").orElse("internal"))
    resolutionStrategy.set(ResolutionStrategy.AUTO)
}

dependencies {
    implementation(project(":core"))

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.splashscreen)
    implementation(libs.androidx.profileinstaller)
    baselineProfile(project(":baselineprofile"))
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.process)
    implementation(libs.androidx.navigation.compose)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons)
    implementation(libs.media3.exoplayer)
    implementation(libs.media3.exoplayer.hls)
    implementation(libs.media3.ui)
    implementation(libs.media3.session)
    implementation(libs.media3.cast)
    implementation(libs.androidx.work.runtime)
    implementation(libs.sentry.android)
    implementation(libs.glance.appwidget)
    implementation(libs.tv.material)
    implementation(libs.zxing.core)
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)
    implementation(libs.kotlinx.coroutines.play.services)
    // Google Play's in-app rating prompt (ReviewPrompt).
    implementation(libs.play.review)
    implementation(libs.kotlinx.coroutines.guava)
    implementation(libs.coil.compose)
    implementation(libs.coil.network.okhttp)
    implementation(libs.kotlinx.coroutines.android)
    debugImplementation(libs.compose.ui.tooling)
    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.robolectric)
    androidTestImplementation(libs.androidx.test.junit)
    androidTestImplementation(libs.androidx.test.runner)
}
