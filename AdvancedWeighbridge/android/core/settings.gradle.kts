// Stand-alone build of the platform-independent core (API client, JSON, display rules) so that it can be
// unit-tested on any JVM:  gradle -p android/core test
// The Android app compiles the same sources (see app/build.gradle.kts).
rootProject.name = "awms-mobile-core"
