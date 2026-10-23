// Android companion app for the Advanced Weighbridge Management System.
//   ./gradlew assembleDebug            debug APK (app/build/outputs/apk/debug)
//   ./gradlew assembleRelease          release APK, signed when keystore.properties or AWMS_KEYSTORE_* are set
//   ./gradlew -p core test             unit tests of the platform-independent core
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "AdvancedWeighbridgeMobile"
include(":app")
