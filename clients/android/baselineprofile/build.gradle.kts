plugins {
    alias(libs.plugins.android.test)
    alias(libs.plugins.baselineprofile)
}

// Makes the app's baseline profile: drives the app through starting, Home and a library's grid on a connected device
// signed in to a real server, and records what runs, so installs compile it ahead of time. See BaselineProfileGenerator.
android {
    namespace = "io.github.flexarr.baselineprofile"
    compileSdk = 37

    defaultConfig {
        minSdk = 28
        targetSdk = 36
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    targetProjectPath = ":app"

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

baselineProfile {
    useConnectedDevices = true
}

dependencies {
    implementation(libs.androidx.test.junit)
    implementation(libs.androidx.uiautomator)
    implementation(libs.androidx.benchmark.macro)
}
