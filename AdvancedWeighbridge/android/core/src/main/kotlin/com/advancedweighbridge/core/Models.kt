package com.advancedweighbridge.core

/** Mirrors the AWMS cloud API v1 contracts (Weighbridge.Contracts). Weights are integer grams, times are ISO-8601 UTC. */

data class UserInfo(
    val id: Long,
    val username: String,
    val displayName: String,
    val role: String,
    val permissions: Set<String>,
    val sites: List<String>,
    val mfaEnabled: Boolean,
    val mustChangePassword: Boolean,
    val mfaSetupRequired: Boolean,
) {
    fun can(permission: String) = permission in permissions

    companion object {
        fun from(j: Json) = UserInfo(
            id = j["id"].long ?: 0,
            username = j["username"].str.orEmpty(),
            displayName = j["displayName"].str.orEmpty(),
            role = j["role"].str.orEmpty(),
            permissions = j["permissions"].list.mapNotNull { it.str }.toSet(),
            sites = j["sites"].list.mapNotNull { it.str },
            mfaEnabled = j["mfaEnabled"].bool ?: false,
            mustChangePassword = j["mustChangePassword"].bool ?: false,
            mfaSetupRequired = j["mfaSetupRequired"].bool ?: false,
        )
    }
}

object Permission {
    const val DASHBOARD = "dashboard.view"
    const val HISTORY = "history.view"
    const val REPORTS = "reports.view"
    const val EXPORT = "reports.export"
    const val CANCEL = "transaction.cancel"
    const val AUDIT = "audit.view"
}

data class Session(val accessToken: String, val expiresUtc: String, val refreshToken: String, val user: UserInfo) {
    companion object {
        fun from(j: Json) = Session(j["accessToken"].str.orEmpty(), j["expiresUtc"].str.orEmpty(), j["refreshToken"].str.orEmpty(), UserInfo.from(j["user"]))
    }
}

data class Site(val id: Long, val code: String, val name: String, val lastHeartbeatUtc: String?, val online: Boolean, val appVersion: String?) {
    companion object {
        fun from(j: Json) = Site(j["id"].long ?: 0, j["code"].str.orEmpty(), j["name"].str.orEmpty(), j["lastHeartbeatUtc"].str, j["online"].bool ?: false, j["appVersion"].str)
    }
}

data class WeighbridgeStatus(
    val code: String,
    val name: String,
    val connection: String,
    val liveG: Long?,
    val stable: Boolean?,
    val measuredUtc: String?,
    val displayStatus: String?,
    val operatingMode: String,
    val automationState: String?,
    val lastError: String?,
    val pendingTransactions: Int,
) {
    companion object {
        fun from(j: Json) = WeighbridgeStatus(
            j["code"].str.orEmpty(), j["name"].str.orEmpty(), j["connection"].str.orEmpty(), j["liveG"].long, j["stable"].bool, j["measuredUtc"].str,
            j["displayStatus"].str, j["operatingMode"].str.orEmpty(), j["automationState"].str, j["lastError"].str, j["pendingTransactions"].int ?: 0,
        )
    }
}

data class SiteHealth(val diskFreeGb: Double, val lastBackupUtc: String?, val syncPending: Long, val syncFailed: Long, val warnings: List<String>) {
    companion object {
        fun from(j: Json) = SiteHealth(j["diskFreeGb"].double ?: 0.0, j["lastBackupUtc"].str, j["syncPending"].long ?: 0, j["syncFailed"].long ?: 0, j["warnings"].list.mapNotNull { it.str })
    }
}

data class SiteStatus(val site: Site, val weighbridges: List<WeighbridgeStatus>, val health: SiteHealth?, val heartbeatAgeSeconds: Double?, val liveDataStale: Boolean) {
    companion object {
        fun from(j: Json) = SiteStatus(
            Site.from(j["site"]), j["weighbridges"].list.map { WeighbridgeStatus.from(it) },
            if (j["health"].isNull) null else SiteHealth.from(j["health"]), j["heartbeatAgeSeconds"].double, j["liveDataStale"].bool ?: true,
        )
    }
}

data class TransactionSummary(
    val uid: String,
    val ticketNo: String,
    val siteCode: String,
    val status: String,
    val vehicleReg: String,
    val customer: String?,
    val supplier: String?,
    val material: String?,
    val grossG: Long?,
    val tareG: Long?,
    val netG: Long?,
    val hasManualWeight: Boolean,
    val createdUtc: String,
    val completedUtc: String?,
    val operatingMode: String,
) {
    companion object {
        fun from(j: Json) = TransactionSummary(
            j["uid"].str.orEmpty(), j["ticketNo"].str.orEmpty(), j["siteCode"].str.orEmpty(), j["status"].str.orEmpty(), j["vehicleReg"].str.orEmpty(),
            j["customer"].str, j["supplier"].str, j["material"].str, j["grossG"].long, j["tareG"].long, j["netG"].long, j["hasManualWeight"].bool ?: false,
            j["createdUtc"].str.orEmpty(), j["completedUtc"].str, j["operatingMode"].str.orEmpty(),
        )
    }
}

data class Measurement(
    val stage: Int,
    val source: String,
    val weightG: Long,
    val isStable: Boolean,
    val unstableOverride: Boolean,
    val overrideReason: String?,
    val manualReason: String?,
    val capturedUtc: String,
    val rawText: String?,
    val operatorName: String,
) {
    val isManual get() = source == "Manual"

    companion object {
        fun from(j: Json) = Measurement(
            j["stage"].int ?: 0, j["source"].str.orEmpty(), j["weightG"].long ?: 0, j["isStable"].bool ?: false, j["unstableOverride"].bool ?: false,
            j["overrideReason"].str, j["manualReason"].str, j["capturedUtc"].str.orEmpty(), j["rawText"].str, j["operatorName"].str.orEmpty(),
        )
    }
}

data class RemoteCommand(val id: Long, val type: String, val reason: String?, val requestedBy: String, val requestedUtc: String, val status: String, val resultMessage: String?) {
    companion object {
        fun from(j: Json) = RemoteCommand(
            j["id"].long ?: 0, j["type"].str.orEmpty(), j["reason"].str, j["requestedBy"].str.orEmpty(), j["requestedUtc"].str.orEmpty(), j["status"].str.orEmpty(), j["resultMessage"].str,
        )
    }
}

data class TransactionDetail(
    val summary: TransactionSummary,
    val direction: String,
    val weighbridgeCode: String,
    val driverName: String?,
    val deliveryNoteNo: String?,
    val referenceNo: String?,
    val remarks: String?,
    val tareMethod: String?,
    val hasOverride: Boolean,
    val createdBy: String,
    val completedBy: String?,
    val cancelledBy: String?,
    val cancelReason: String?,
    val version: Int,
    val measurements: List<Measurement>,
    val commands: List<RemoteCommand>,
) {
    companion object {
        fun from(j: Json): TransactionDetail {
            val t = j["transaction"]
            return TransactionDetail(
                TransactionSummary.from(j["summary"]), t["direction"].str.orEmpty(), t["weighbridgeCode"].str.orEmpty(), t["driverName"].str, t["deliveryNoteNo"].str,
                t["referenceNo"].str, t["remarks"].str, t["tareMethod"].str, t["hasOverride"].bool ?: false, t["createdBy"].str.orEmpty(), t["completedBy"].str,
                t["cancelledBy"].str, t["cancelReason"].str, t["version"].int ?: 1, t["measurements"].list.map { Measurement.from(it) }, j["commands"].list.map { RemoteCommand.from(it) },
            )
        }
    }
}

data class Page<T>(val items: List<T>, val total: Long, val page: Int, val pageSize: Int) {
    val hasMore get() = page.toLong() * pageSize < total
}

data class SummaryRow(val key: String, val count: Long, val netG: Long)

data class SummaryReport(val groupBy: String, val from: String, val to: String, val rows: List<SummaryRow>, val totalCount: Long, val totalNetG: Long) {
    companion object {
        fun from(j: Json) = SummaryReport(
            j["groupBy"].str.orEmpty(), j["from"].str.orEmpty(), j["to"].str.orEmpty(),
            j["rows"].list.map { SummaryRow(it["key"].str.orEmpty(), it["count"].long ?: 0, it["netG"].long ?: 0) }, j["totalCount"].long ?: 0, j["totalNetG"].long ?: 0,
        )
    }
}

data class AppNotification(val id: Long, val siteCode: String, val atUtc: String, val severity: String, val category: String, val title: String, val message: String, val entityId: String?, val isRead: Boolean) {
    companion object {
        fun from(j: Json) = AppNotification(
            j["id"].long ?: 0, j["siteCode"].str.orEmpty(), j["atUtc"].str.orEmpty(), j["severity"].str.orEmpty(), j["category"].str.orEmpty(), j["title"].str.orEmpty(),
            j["message"].str.orEmpty(), j["entityId"].str, j["isRead"].bool ?: false,
        )
    }
}

data class TransactionQuery(
    val site: String? = null,
    val fromUtc: String? = null,
    val toUtc: String? = null,
    val text: String? = null,
    val status: String? = null,
    val manualOnly: Boolean = false,
    val page: Int = 1,
    val pageSize: Int = 50,
)
