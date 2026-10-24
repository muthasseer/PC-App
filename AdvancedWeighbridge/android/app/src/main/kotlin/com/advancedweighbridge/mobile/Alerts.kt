package com.advancedweighbridge.mobile

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import com.advancedweighbridge.core.ApiException
import com.advancedweighbridge.core.AppNotification
import com.advancedweighbridge.core.Display
import java.time.Instant
import kotlin.concurrent.thread

/** System notifications for new site alerts, checked in the background about every 15 minutes (Android's minimum). */
object Alerts {
    const val CHANNEL = "alerts"
    private const val SUMMARY_ID = 1
    private const val GROUP = "awms-alerts"
    private const val MAX_INDIVIDUAL = 3

    fun createChannel(context: Context) {
        val channel = NotificationChannel(CHANNEL, context.getString(R.string.channel_alerts), NotificationManager.IMPORTANCE_DEFAULT).apply {
            description = context.getString(R.string.channel_alerts_description)
        }
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    /** One background check. Returns normally when there is nothing to do; API errors propagate to the caller. */
    fun check(app: AwmsApp) {
        val prefs = app.prefs
        if (!prefs.alertsEnabled) return
        val api = app.api ?: return
        if (api.session == null && !api.resume()) {
            // The session ended (signed out elsewhere, password reset, ...): stop until the user signs in again.
            prefs.alertsEnabled = false
            AlertJobService.cancel(app)
            return
        }
        val last = prefs.lastAlertId
        if (last < 0) {
            prefs.lastAlertId = api.notifications(limit = 1).firstOrNull()?.id ?: 0
            return
        }
        val fresh = api.notifications(afterId = last, unreadOnly = true, limit = 20).sortedBy { it.id }
        if (fresh.isEmpty()) return
        post(app, fresh)
        prefs.lastAlertId = fresh.maxOf { it.id }
    }

    private fun post(context: Context, items: List<AppNotification>) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }
        val manager = context.getSystemService(NotificationManager::class.java)
        val open = PendingIntent.getActivity(
            context, 0,
            Intent(context, MainActivity::class.java).putExtra(HomeActivity.EXTRA_TAB, HomeActivity.TAB_ALERTS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        for (n in items.takeLast(MAX_INDIVIDUAL)) {
            val notification = Notification.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_alert)
                .setContentTitle("${n.siteCode}: ${n.title}")
                .setContentText(n.message)
                .setStyle(Notification.BigTextStyle().bigText(n.message))
                .setSubText(Texts.severity(context, n.severity))
                .setWhen(runCatching { Instant.parse(n.atUtc.replace("+00:00", "Z")).toEpochMilli() }.getOrDefault(System.currentTimeMillis()))
                .setShowWhen(true)
                .setContentIntent(open)
                .setAutoCancel(true)
                .setGroup(GROUP)
                .build()
            manager.notify((n.id % Int.MAX_VALUE).toInt() + SUMMARY_ID + 1, notification)
        }
        if (items.size > 1) {
            val summary = Notification.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_alert)
                .setContentTitle(context.getString(R.string.alerts_new, items.size))
                .setContentText(items.last().title + " · " + Display.time(items.last().atUtc))
                .setContentIntent(open)
                .setAutoCancel(true)
                .setGroup(GROUP)
                .setGroupSummary(true)
                .build()
            manager.notify(SUMMARY_ID, summary)
        }
    }
}

class AlertJobService : JobService() {
    @Volatile
    private var worker: Thread? = null

    override fun onStartJob(params: JobParameters): Boolean {
        worker = thread(name = "awms-alerts") {
            try {
                Alerts.check(AwmsApp.of(this))
            } catch (e: ApiException) {
                Log.i("AwmsAlerts", "Alert check skipped: ${e.message}")
            } catch (e: Exception) {
                Log.w("AwmsAlerts", "Alert check failed", e)
            }
            jobFinished(params, false)
        }
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean {
        worker?.interrupt()
        return true
    }

    companion object {
        private const val JOB_ID = 4711

        fun schedule(context: Context) {
            val job = JobInfo.Builder(JOB_ID, ComponentName(context, AlertJobService::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setPeriodic(15 * 60 * 1000L)
                .setPersisted(true)
                .build()
            context.getSystemService(JobScheduler::class.java).schedule(job)
        }

        fun cancel(context: Context) {
            context.getSystemService(JobScheduler::class.java).cancel(JOB_ID)
        }
    }
}
