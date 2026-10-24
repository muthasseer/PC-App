package com.advancedweighbridge.core

import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

class JsonTest {
    @Test
    fun `parses and writes the API shapes`() {
        val j = Json.parse("""{"a":1,"b":[true,false,null],"c":"x\"yA\n","d":-12.5e2,"e":{"f":20000000}}""")
        assertEquals(1L, j["a"].long)
        assertEquals(listOf(true, false), j["b"].list.mapNotNull { it.bool })
        assertTrue(j["b"].list[2].isNull)
        assertEquals("x\"yA\n", j["c"].str)
        assertEquals(-1250.0, j["d"].double)
        assertEquals(20_000_000L, j["e"]["f"].long)
        assertTrue(j["missing"].isNull)
        assertEquals(j, Json.parse(j.stringify()))
        assertEquals("""{"r":"Wrong customer","n":null}""", Json.obj("r" to "Wrong customer", "n" to null).stringify())
        assertEquals("\"ශ්‍රී லங்கா\"", Json.of("ශ්‍රී லங்கா").stringify())
    }

    @Test
    fun `rejects malformed input`() {
        for (bad in listOf("{", "[1,]", "{\"a\" 1}", "tru", "\"abc", "1 2", "{\"a\":01x}")) {
            assertFailsWith<JsonException>(bad) { Json.parse(bad) }
        }
    }
}

class DisplayTest {
    private fun site(age: Double?, stale: Boolean = false, wb: WeighbridgeStatus) =
        SiteStatus(Site(1, "QRY", "Quarry", null, true, "1.0.0"), listOf(wb), null, age, stale)

    private val wb = WeighbridgeStatus("WB1", "Bridge 1", "Connected", 31_240_000, true, null, "Stable", "Automatic", null, null, 2)

    @Test
    fun `fresh data is live`() {
        val v = LiveView.of(site(5.0, wb = wb), wb)
        assertEquals(LiveState.LIVE_STABLE, v.state)
        assertEquals("31,240 kg", v.weight)
        assertTrue(v.isLive)
        assertEquals(LiveState.LIVE_MOTION, LiveView.of(site(5.0, wb = wb.copy(stable = false)), wb.copy(stable = false)).state)
    }

    @Test
    fun `delayed data is never shown as live`() {
        for (s in listOf(site(120.0, wb = wb), site(5.0, stale = true, wb = wb), site(null, wb = wb))) {
            val v = LiveView.of(s, wb)
            assertEquals(LiveState.NOT_LIVE, v.state)
            assertFalse(v.isLive)
            assertTrue(v.note!!.contains("not a current reading"))
        }
        assertEquals(LiveState.NOT_LIVE, LiveView.of(site(5.0, wb = wb), wb.copy(displayStatus = "NotLive")).state)
    }

    @Test
    fun `a refresh that stopped arriving makes the value not live`() {
        val fetched = java.time.Instant.parse("2026-10-23T10:00:00Z")
        val s = site(5.0, wb = wb)
        assertEquals(LiveState.LIVE_STABLE, LiveView.at(s, wb, fetched, fetched.plusSeconds(30)).state)
        val later = LiveView.at(s, wb, fetched, fetched.plusSeconds(120))
        assertEquals(LiveState.NOT_LIVE, later.state)
        assertEquals(125.0, later.ageSeconds)
    }

    @Test
    fun `report periods follow the local calendar`() {
        val now = java.time.ZonedDateTime.of(2026, 3, 1, 9, 30, 0, 0, java.time.ZoneId.of("Asia/Colombo"))
        assertEquals(TimeRange("2026-02-28T18:30:00Z", "2026-03-01T18:30:00Z"), Periods.range(Period.TODAY, now))
        assertEquals(TimeRange("2026-02-27T18:30:00Z", "2026-02-28T18:30:00Z"), Periods.range(Period.YESTERDAY, now))
        assertEquals(TimeRange("2026-01-31T18:30:00Z", "2026-02-28T18:30:00Z"), Periods.range(Period.LAST_MONTH, now))
        assertEquals("2026-02-22T18:30:00Z", Periods.range(Period.LAST_7_DAYS, now).fromUtc)
        assertEquals(TimeRange(null, null), Periods.range(Period.ALL, now))
    }

    @Test
    fun `disconnected and unshared weights`() {
        val off = wb.copy(connection = "Silent", liveG = null)
        assertEquals(LiveState.DISCONNECTED, LiveView.of(site(3.0, wb = off), off).state)
        val hidden = wb.copy(liveG = null)
        assertEquals(LiveState.NOT_SHARED, LiveView.of(site(3.0, wb = hidden), hidden).state)
    }

    @Test
    fun formats() {
        assertEquals("—", Display.kg(null))
        assertEquals("20,000 kg", Display.kg(20_000_000))
        assertEquals("45 s ago", Display.age(45.4))
        assertEquals("3 min ago", Display.age(200.0))
        assertEquals("never", Display.age(null))
        val age = Display.ageOf("2026-10-23T19:20:05.3025762+00:00", java.time.Instant.parse("2026-10-23T19:21:05.3025762Z"))
        assertEquals(60.0, age)
    }
}

class ApiClientTest {
    private lateinit var server: MockWebServer
    private val store = object : TokenStore {
        var token: String? = null
        override fun load() = token
        override fun save(refreshToken: String?) { token = refreshToken }
    }

    private fun session(access: String, refresh: String) =
        """{"accessToken":"$access","expiresUtc":"2026-10-23T20:00:00Z","refreshToken":"$refresh","user":{"id":1,"username":"admin","displayName":"Admin","role":"Administrator","permissions":["dashboard.view","transaction.cancel"],"sites":["QRY"],"mfaEnabled":false}}"""

    @BeforeTest
    fun start() {
        server = MockWebServer()
        server.start()
    }

    @AfterTest
    fun stop() = server.shutdown()

    private fun client() = ApiClient(server.url("/").toString(), store)

    @Test
    fun `only https or local development addresses are accepted`() {
        assertEquals("https://awms.example.lk/", ApiClient.validate("https://awms.example.lk").toString())
        ApiClient.validate("http://10.0.2.2:5080")
        assertFailsWith<ApiException> { ApiClient.validate("http://awms.example.lk") }
        assertFailsWith<ApiException> { ApiClient.validate("not a url") }
    }

    @Test
    fun `sign-in, MFA prompt and permissions`() {
        server.enqueue(MockResponse().setResponseCode(401).setBody("""{"error":"Enter the 6-digit code from your authenticator app.","mfaRequired":true}"""))
        server.enqueue(MockResponse().setBody(session("a1", "r1")))
        val api = client()
        val first = api.login("admin", "pw", null)
        assertIs<LoginOutcome.NeedsCode>(first)
        val second = api.login("admin", "pw", "123456")
        assertIs<LoginOutcome.Success>(second)
        assertEquals("r1", store.token)
        assertTrue(api.user!!.can(Permission.CANCEL))
        server.takeRequest()
        val body = server.takeRequest().body.readUtf8()
        assertTrue(body.contains("\"totpCode\":\"123456\""))
    }

    @Test
    fun `an expired access token is renewed once with the refresh token`() {
        server.enqueue(MockResponse().setBody(session("a1", "r1")))
        server.enqueue(MockResponse().setResponseCode(401).setBody("""{"error":"Your session has expired. Sign in again."}"""))
        server.enqueue(MockResponse().setBody(session("a2", "r2")))
        server.enqueue(MockResponse().setBody("""[{"id":1,"code":"QRY","name":"Quarry","online":true}]"""))
        val api = client()
        api.login("admin", "pw", null)
        val sites = api.sites()
        assertEquals("QRY", sites.single().code)
        assertEquals("r2", store.token)
        server.takeRequest()
        assertEquals("Bearer a1", server.takeRequest().getHeader("Authorization"))
        assertTrue(server.takeRequest().body.readUtf8().contains("r1"))
        assertEquals("Bearer a2", server.takeRequest().getHeader("Authorization"))
    }

    @Test
    fun `a revoked session asks to sign in again and forgets the token`() {
        store.token = "old"
        server.enqueue(MockResponse().setResponseCode(401).setBody("""{"error":"Session expired. Sign in again."}"""))
        val api = client()
        assertFalse(api.resume())
        assertNull(store.token)
        val e = assertFailsWith<ApiException> { api.sites() }
        assertEquals(401, e.status)
    }

    @Test
    fun `server errors and unreachable servers become readable messages`() {
        server.enqueue(MockResponse().setBody(session("a1", "r1")))
        server.enqueue(MockResponse().setResponseCode(403).setBody("""{"error":"You do not have the permission for this action."}"""))
        val api = client()
        api.login("admin", "pw", null)
        val denied = assertFailsWith<ApiException> { api.requestCancel("u", "Wrong customer") }
        assertEquals(403, denied.status)
        assertEquals("You do not have the permission for this action.", denied.message)
        server.shutdown()
        val offline = assertFailsWith<ApiException> { api.sites() }
        assertTrue(offline.isNetwork)
    }

    @Test
    fun `status, transactions and ticket pdf`() {
        server.enqueue(MockResponse().setBody(session("a1", "r1")))
        server.enqueue(MockResponse().setBody("""[{"site":{"id":1,"code":"QRY","name":"Quarry","online":true},"weighbridges":[{"code":"WB1","name":"Bridge","connection":"Connected","liveG":12340000,"stable":true,"operatingMode":"Automatic","pendingTransactions":1}],"heartbeatAgeSeconds":4.2,"liveDataStale":false}]"""))
        server.enqueue(MockResponse().setBody("""{"items":[{"uid":"u1","ticketNo":"QRY-000001","siteCode":"QRY","status":"Completed","vehicleReg":"WP CAB-1234","netG":20000000,"hasManualWeight":true,"createdUtc":"2026-10-23T10:00:00Z","operatingMode":"SemiAutomatic"}],"total":51,"page":1,"pageSize":50}"""))
        server.enqueue(MockResponse().setBody("%PDF-1.7 test").setHeader("Content-Type", "application/pdf"))
        val api = client()
        api.login("admin", "pw", null)
        val status = api.status("QRY").single()
        assertEquals(LiveState.LIVE_STABLE, LiveView.of(status, status.weighbridges.single()).state)
        val page = api.transactions(TransactionQuery(site = "QRY", text = "CAB 1234", manualOnly = true))
        assertTrue(page.hasMore)
        assertTrue(page.items.single().hasManualWeight)
        assertTrue(String(api.ticketPdf("u1")).startsWith("%PDF"))
        server.takeRequest()
        assertEquals("/api/v1/status?site=QRY", server.takeRequest().path)
        val txPath = server.takeRequest().path!!
        assertTrue(txPath.startsWith("/api/v1/transactions?site=QRY&q=CAB%201234&manualOnly=true"), txPath)
    }
}
