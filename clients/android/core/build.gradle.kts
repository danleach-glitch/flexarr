// The API client and models, shared by the phone and (later) TV screens. Plain Kotlin, so its tests run without a
// device: Android's own pieces (where the sign-in is kept, the player) live in the app.
plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
}

// Built with whichever JDK runs Gradle (Android Studio's own), for Java 17 like the app.
java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    api(libs.okhttp)
    api(libs.kotlinx.serialization.json)
    api(libs.kotlinx.coroutines.core)

    testImplementation(libs.junit)
    testImplementation(libs.okhttp.mockwebserver)
    testImplementation(libs.kotlinx.coroutines.test)
}

// The API's real answers, shared with FlexarrKit's tests, so both apps are checked against the same responses.
sourceSets.test {
    resources.srcDir("../../apple/FlexarrKit/Tests/FlexarrKitTests/Fixtures")
}
