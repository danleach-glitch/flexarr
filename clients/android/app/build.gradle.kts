plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.baselineprofile)
}

// Notifications come through Firebase, set up from the Firebase project's google-services.json beside this file (its
// API key only identifies the project, so it can be committed). Without it the app builds and runs, and Settings says
// notifications aren't available in this build.
if (file("google-services.json").exists()) apply(plugin = "com.google.gms.google-services")

// The app's version is the server's (Directory.Build.props), so it isn't one more place release.sh has to change:
// 0.52.0 is versionName "0.52.0" and versionCode 52000.
val serverVersion: String = Regex("<VersionPrefix>([^<]+)</VersionPrefix>")
    .find(rootDir.resolve("../../Directory.Build.props").readText())!!.groupValues[1]
val versionParts = serverVersion.split(".").map { it.toInt() }

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
    }

    buildTypes {
        debug {
            buildConfigField("boolean", "LAUNCH_ARGUMENTS", "true")
        }
        // The baseline profile plugin's builds of release, made only to profile on a device: signed with the debug key.
        configureEach {
            if (name == "nonMinifiedRelease" || name == "benchmarkRelease") signingConfig = signingConfigs.getByName("debug")
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
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
}

// The baseline profile's builds take launch arguments too, to sign in (the plugin copies release's fields over them, so
// they're set per variant).
androidComponents {
    onVariants { variant ->
        if (variant.name == "nonMinifiedRelease" || variant.name == "benchmarkRelease") {
            variant.buildConfigFields?.put("LAUNCH_ARGUMENTS", com.android.build.api.variant.BuildConfigField("boolean", "true", null))
        }
    }
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
    implementation(libs.androidx.work.runtime)
    implementation(libs.sentry.android)
    implementation(libs.glance.appwidget)
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)
    implementation(libs.kotlinx.coroutines.play.services)
    implementation(libs.kotlinx.coroutines.guava)
    implementation(libs.coil.compose)
    implementation(libs.coil.network.okhttp)
    implementation(libs.kotlinx.coroutines.android)
    debugImplementation(libs.compose.ui.tooling)
    androidTestImplementation(libs.androidx.test.junit)
    androidTestImplementation(libs.androidx.test.runner)
}
