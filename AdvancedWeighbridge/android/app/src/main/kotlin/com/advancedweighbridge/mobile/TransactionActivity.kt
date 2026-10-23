package com.advancedweighbridge.mobile

import android.app.AlertDialog
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.os.Bundle
import android.os.ParcelFileDescriptor
import android.view.ContextThemeWrapper
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Toolbar
import com.advancedweighbridge.core.ApiClient
import com.advancedweighbridge.core.Display
import com.advancedweighbridge.core.Permission
import com.advancedweighbridge.core.TransactionDetail
import java.io.File

/** Details of one transaction: weights (manual entries clearly marked), remote requests, the ticket PDF and cancellation. */
class TransactionActivity : BaseActivity() {
    private lateinit var api: ApiClient
    private lateinit var uid: String
    private lateinit var body: LinearLayout
    private lateinit var ticketPages: LinearLayout
    private var detail: TransactionDetail? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val client = app.api
        val id = intent.getStringExtra(EXTRA_UID)
        if (client?.session == null || id == null) {
            startActivity(Intent(this, MainActivity::class.java))
            finish()
            return
        }
        api = client
        uid = id

        val root = column()
        val toolbar = Toolbar(ContextThemeWrapper(this, android.R.style.ThemeOverlay_Material_Dark_ActionBar)).apply {
            setBackgroundColor(getColor(R.color.brand))
            title = getString(R.string.transaction)
            setNavigationIcon(R.drawable.ic_arrow_back)
            navigationContentDescription = getString(R.string.back)
            setNavigationOnClickListener { finish() }
        }
        root.add(toolbar)
        body = column(16)
        ticketPages = column()
        val scroll = ScrollView(this).apply {
            addView(column().apply {
                add(body)
                add(ticketPages)
            })
        }
        root.add(scroll, height = 0, weight = 1f)
        setContentView(root)
        edgeToEdge(root, toolbar)
        load()
    }

    private fun load() {
        body.removeAllViews()
        body.add(text(getString(R.string.loading)))
        io(work = { api.transaction(uid) }, error = { body.removeAllViews(); body.add(text(it.message ?: getString(R.string.error_generic), color = getColor(R.color.state_error))) }) {
            detail = it
            render(it)
        }
    }

    private fun render(d: TransactionDetail) {
        val t = d.summary
        body.removeAllViews()
        val head = body.add(card())
        val top = head.add(row())
        top.add(text(t.ticketNo, TextKind.TITLE), width = 0, weight = 1f)
        top.add(badge(Texts.status(this, t.status), Texts.statusColor(t.status)), width = WRAP)
        head.add(text("${t.siteCode} · ${d.weighbridgeCode} · ${Texts.mode(this, t.operatingMode)}", TextKind.CAPTION, color = getColor(R.color.text_secondary)), topDp = 2)
        if (t.hasManualWeight) {
            head.add(badge(getString(R.string.manual_weight_notice), R.color.state_motion), topDp = 8)
        }

        val weights = body.add(card(), topDp = 12)
        weights.add(field(getString(R.string.gross), Display.kg(t.grossG)))
        weights.add(field(getString(R.string.tare), Display.kg(t.tareG) + (d.tareMethod?.takeIf { it == "StoredTare" }?.let { " · " + getString(R.string.stored_tare) } ?: "")))
        weights.add(row().apply {
            add(text(getString(R.string.net), TextKind.SUBHEAD, color = getColor(R.color.text_secondary)), width = 0, weight = 0.4f)
            add(text(Display.kg(t.netG), TextKind.HEADLINE, bold = true), width = 0, weight = 0.6f)
        }, topDp = 4)

        val info = body.add(card(), topDp = 12)
        info.add(field(getString(R.string.vehicle), t.vehicleReg))
        info.add(field(getString(R.string.direction), Texts.direction(this, d.direction)))
        info.add(field(getString(R.string.customer), t.customer))
        info.add(field(getString(R.string.supplier), t.supplier))
        info.add(field(getString(R.string.material), t.material))
        info.add(field(getString(R.string.driver), d.driverName))
        info.add(field(getString(R.string.delivery_note), d.deliveryNoteNo))
        info.add(field(getString(R.string.reference), d.referenceNo))
        info.add(field(getString(R.string.remarks), d.remarks))
        info.add(field(getString(R.string.created), "${Display.time(t.createdUtc)} · ${d.createdBy}"))
        if (t.completedUtc != null) info.add(field(getString(R.string.completed), "${Display.time(t.completedUtc)} · ${d.completedBy.orEmpty()}"))
        if (d.cancelledBy != null) info.add(field(getString(R.string.cancelled), "${d.cancelledBy} · ${d.cancelReason.orEmpty()}"))

        body.add(text(getString(R.string.weighings), TextKind.SUBHEAD, bold = true), topDp = 16)
        for (m in d.measurements.sortedBy { it.stage }) {
            val card = body.add(card(), topDp = 6)
            val line = card.add(row())
            line.add(text(getString(R.string.weighing_n, m.stage), TextKind.BODY, bold = true), width = 0, weight = 1f)
            line.add(text(Display.kg(m.weightG), TextKind.SUBHEAD, bold = true), width = WRAP)
            card.add(text("${Texts.source(this, m)} · ${Display.time(m.capturedUtc)} · ${m.operatorName}", TextKind.CAPTION, color = getColor(R.color.text_secondary)))
            if (m.isManual) {
                card.add(text(getString(R.string.manual_entry_warning, m.manualReason ?: "—"), TextKind.BODY, color = getColor(R.color.state_motion)), topDp = 4)
            }
            if (m.unstableOverride) {
                card.add(text(getString(R.string.unstable_override, m.overrideReason ?: "—"), TextKind.BODY, color = getColor(R.color.state_motion)), topDp = 4)
            }
            m.rawText?.takeIf { it.isNotBlank() }?.let { card.add(text(getString(R.string.raw_data, it), TextKind.CAPTION, color = getColor(R.color.text_secondary)).apply { typeface = android.graphics.Typeface.MONOSPACE }, topDp = 2) }
        }

        if (d.commands.isNotEmpty()) {
            body.add(text(getString(R.string.remote_requests), TextKind.SUBHEAD, bold = true), topDp = 16)
            for (cmd in d.commands) {
                val card = body.add(card(), topDp = 6)
                card.add(text(getString(R.string.cancel_request_line, cmd.requestedBy, Display.time(cmd.requestedUtc)), TextKind.BODY))
                card.add(text("${Texts.commandStatus(this, cmd.status)}${cmd.resultMessage?.let { " · $it" } ?: ""}", TextKind.CAPTION, color = getColor(R.color.text_secondary)))
                cmd.reason?.let { card.add(text(getString(R.string.reason_value, it), TextKind.CAPTION, color = getColor(R.color.text_secondary))) }
            }
        }

        val actions = body.add(column(), topDp = 16)
        actions.add(button(getString(R.string.view_ticket)) { showTicket(share = false) })
        actions.add(button(getString(R.string.share_ticket), primary = false) { showTicket(share = true) })
        val pendingCancel = d.commands.any { it.type == "CancelTransaction" && it.status in setOf("Pending", "Delivered") }
        if (api.user?.can(Permission.CANCEL) == true && t.status != "Cancelled" && !pendingCancel) {
            actions.add(button(getString(R.string.request_cancel), primary = false) { requestCancel() })
        } else if (pendingCancel) {
            actions.add(text(getString(R.string.cancel_pending), TextKind.CAPTION, color = getColor(R.color.text_secondary)))
        }
    }

    private fun ticketFile(): File {
        val name = (detail?.summary?.ticketNo ?: uid).replace(Regex("[^A-Za-z0-9_-]"), "_") + ".pdf"
        return File(TicketProvider.directory(this), name)
    }

    private fun showTicket(share: Boolean) {
        val file = ticketFile()
        toast(getString(R.string.downloading_ticket))
        io(work = {
            file.writeBytes(api.ticketPdf(uid))
            file
        }) { pdf ->
            if (share) {
                val uri = TicketProvider.uri(this, pdf)
                val send = Intent(Intent.ACTION_SEND).setType("application/pdf").putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                startActivity(Intent.createChooser(send, getString(R.string.share_ticket)))
            } else {
                renderPdf(pdf)
            }
        }
    }

    /** Shows the ticket inside the app (no external PDF viewer needed). */
    private fun renderPdf(pdf: File) {
        ticketPages.removeAllViews()
        val width = resources.displayMetrics.widthPixels
        try {
            ParcelFileDescriptor.open(pdf, ParcelFileDescriptor.MODE_READ_ONLY).use { fd ->
                PdfRenderer(fd).use { renderer ->
                    for (i in 0 until minOf(renderer.pageCount, 5)) {
                        renderer.openPage(i).use { page ->
                            val height = (width.toLong() * page.height / page.width).toInt()
                            val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
                            bitmap.eraseColor(Color.WHITE)
                            page.render(bitmap, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                            ticketPages.add(ImageView(this).apply {
                                setImageBitmap(bitmap)
                                adjustViewBounds = true
                                contentDescription = getString(R.string.ticket_page, i + 1)
                            }, topDp = 8)
                        }
                    }
                }
            }
        } catch (e: java.io.IOException) {
            toast(getString(R.string.ticket_unreadable, e.message))
        } catch (e: SecurityException) {
            toast(getString(R.string.ticket_unreadable, e.message))
        }
        ticketPages.visibility = View.VISIBLE
    }

    private fun requestCancel() {
        val form = column(20)
        form.add(text(getString(R.string.cancel_explanation), TextKind.BODY))
        val reason = form.add(input(getString(R.string.reason), Inputs.MULTILINE), topDp = 8)
        val dialog = AlertDialog.Builder(this)
            .setTitle(R.string.request_cancel)
            .setView(form)
            .setPositiveButton(R.string.send_request, null)
            .setNegativeButton(R.string.cancel, null)
            .create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val text = reason.text.toString().trim()
                if (text.length < 5) {
                    reason.error = getString(R.string.reason_too_short)
                    return@setOnClickListener
                }
                io(work = { api.requestCancel(uid, text) }) {
                    dialog.dismiss()
                    toast(getString(R.string.cancel_requested))
                    load()
                }
            }
        }
        dialog.show()
    }

    companion object {
        const val EXTRA_UID = "uid"
    }
}
