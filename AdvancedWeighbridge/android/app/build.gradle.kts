import java.util.Properties
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Release signing: keystore.properties next to this file (never committed) or AWMS_KEYSTORE_* environment variables
// (CI secrets). Without either, the release APK is left unsigned and only the debug APK can be installed.
val signing = Properties().apply {
    val file = rootProject.file("keystore.properties")
    if (file.exists()) file.inputStream().use { load(it) }
    System.getenv("AWMS_KEYSTORE_FILE")?.let { setProperty("storeFile", it) }
    System.getenv("AWMS_KEYSTORE_PASSWORD")?.let { setProperty("storePassword", it) }
    System.getenv("AWMS_KEY_ALIAS")?.let { setProperty("keyAlias", it) }
    System.getenv("AWMS_KEY_PASSWORD")?.let { setProperty("keyPassword", it) }
}
val canSign = listOf("storeFile", "storePassword", "keyAlias", "keyPassword").all { !signing.getProperty(it).isNullOrBlank() }

android {
    namespace = "com.advancedweighbridge.mobile"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.advancedweighbridge.mobile"
        minSdk = 26
        targetSdk = 36
        versionCode = (System.getenv("AWMS_VERSION_CODE") ?: "1").toInt()
        versionName = System.getenv("AWMS_VERSION") ?: "1.0.0"
    }

    signingConfigs {
        if (canSign) {
            create("release") {
                storeFile = file(signing.getProperty("storeFile"))
                storePassword = signing.getProperty("storePassword")
                keyAlias = signing.getProperty("keyAlias")
                keyPassword = signing.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        getByName("debug") {
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-debug"
        }
        getByName("release") {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            if (canSign) signingConfig = signingConfigs.getByName("release")
        }
    }

    sourceSets {
        // The platform-independent core (API client, JSON, display rules) is shared with the stand-alone core build,
        // where it is unit-tested on the JVM.
        getByName("main").java.srcDirs("src/main/kotlin", "../core/src/main/kotlin")
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    lint {
        abortOnError = true
        warningsAsErrors = false
        checkReleaseBuilds = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    // Deliberately small: the Android framework plus two widely used libraries.
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}
