package com.advancedweighbridge.mobile

import android.content.Context
import com.advancedweighbridge.core.LiveState
import com.advancedweighbridge.core.LiveView
import com.advancedweighbridge.core.Measurement
import com.advancedweighbridge.core.WeighbridgeStatus

/** Localised wording for values that arrive from the API as English identifiers. Unknown values are shown as sent. */
internal object Texts {
    fun age(c: Context, seconds: Double?): String = when {
        seconds == null -> c.getString(R.string.age_never)
        seconds < 90 -> c.getString(R.string.age_seconds, seconds.toLong())
        seconds < 5400 -> c.getString(R.string.age_minutes, (seconds / 60).toLong())
        seconds < 172800 -> c.getString(R.string.age_hours, (seconds / 3600).toLong())
        else -> c.getString(R.string.age_days, (seconds / 86400).toLong())
    }

    fun status(c: Context, value: String): String = when (value) {
        "Pending" -> c.getString(R.string.status_pending)
        "Completed" -> c.getString(R.string.status_completed)
        "Cancelled" -> c.getString(R.string.status_cancelled)
        else -> value
    }

    fun statusColor(value: String): Int = when (value) {
        "Completed" -> R.color.state_stable
        "Cancelled" -> R.color.state_error
        else -> R.color.state_info
    }

    fun mode(c: Context, value: String): String = when (value) {
        "Automatic" -> c.getString(R.string.mode_automatic)
        "SemiAutomatic" -> c.getString(R.string.mode_semi)
        "Manual" -> c.getString(R.string.mode_manual)
        else -> value
    }

    fun direction(c: Context, value: String): String = when (value) {
        "Inbound" -> c.getString(R.string.direction_inbound)
        "Outbound" -> c.getString(R.string.direction_outbound)
        else -> value
    }

    fun connection(c: Context, value: String): String = when (value) {
        "Connected" -> c.getString(R.string.conn_connected)
        "Connecting" -> c.getString(R.string.conn_connecting)
        "Disconnected" -> c.getString(R.string.conn_disconnected)
        "Silent" -> c.getString(R.string.conn_silent)
        "Error" -> c.getString(R.string.conn_error)
        "Disabled" -> c.getString(R.string.conn_disabled)
        else -> value
    }

    fun source(c: Context, m: Measurement): String = when (m.source) {
        "Indicator" -> if (m.isStable) c.getString(R.string.source_indicator_stable) else c.getString(R.string.source_indicator_unstable)
        "Manual" -> c.getString(R.string.source_manual)
        "StoredTare" -> c.getString(R.string.source_stored_tare)
        else -> m.source
    }

    fun severity(c: Context, value: String): String = when (value) {
        "Error", "Critical" -> c.getString(R.string.severity_error)
        "Warning" -> c.getString(R.string.severity_warning)
        else -> c.getString(R.string.severity_info)
    }

    fun severityColor(value: String): Int = when (value) {
        "Error", "Critical" -> R.color.state_error
        "Warning" -> R.color.state_motion
        else -> R.color.state_info
    }

    fun commandStatus(c: Context, value: String): String = when (value) {
        "Pending" -> c.getString(R.string.command_pending)
        "Delivered" -> c.getString(R.string.command_delivered)
        "Completed", "Succeeded" -> c.getString(R.string.command_completed)
        "Failed", "Rejected" -> c.getString(R.string.command_failed)
        else -> value
    }

    /** The headline of a weighbridge tile. */
    fun liveLabel(c: Context, view: LiveView, wb: WeighbridgeStatus): String = when (view.state) {
        LiveState.LIVE_STABLE -> c.getString(R.string.live_stable)
        LiveState.LIVE_MOTION -> c.getString(R.string.live_motion)
        LiveState.NOT_LIVE -> c.getString(R.string.live_not_live)
        LiveState.DISCONNECTED -> c.getString(R.string.live_indicator, connection(c, wb.connection))
        LiveState.NOT_SHARED -> c.getString(R.string.live_not_shared)
    }

    /** The explanation under a tile, or null when the value is live. */
    fun liveNote(c: Context, view: LiveView, wb: WeighbridgeStatus): String? = when (view.state) {
        LiveState.NOT_LIVE -> c.getString(R.string.live_not_live_note, age(c, view.ageSeconds))
        LiveState.DISCONNECTED -> wb.lastError
        LiveState.NOT_SHARED -> c.getString(R.string.live_not_shared_note)
        else -> null
    }
}
