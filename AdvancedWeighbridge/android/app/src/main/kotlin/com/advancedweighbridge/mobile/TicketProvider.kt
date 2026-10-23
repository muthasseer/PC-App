package com.advancedweighbridge.mobile

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File
import java.io.FileNotFoundException

/**
 * Read-only access to downloaded ticket PDFs for the app the user shares a ticket with. Only plain file names inside
 * the app's ticket cache folder are served; access is granted per share (FLAG_GRANT_READ_URI_PERMISSION).
 */
class TicketProvider : ContentProvider() {
    override fun onCreate() = true

    private fun file(uri: Uri): File {
        val context = context ?: throw FileNotFoundException()
        val name = uri.pathSegments.singleOrNull() ?: throw FileNotFoundException()
        if (!NAME.matches(name)) throw FileNotFoundException()
        val file = File(directory(context), name)
        if (!file.isFile) throw FileNotFoundException()
        return file
    }

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        if (mode != "r") throw SecurityException("Tickets are read-only")
        return ParcelFileDescriptor.open(file(uri), ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun getType(uri: Uri): String = "application/pdf"

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor {
        val file = file(uri)
        val columns = projection?.filter { it == OpenableColumns.DISPLAY_NAME || it == OpenableColumns.SIZE }?.toTypedArray()
            ?: arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)
        return MatrixCursor(columns, 1).apply {
            addRow(columns.map { if (it == OpenableColumns.DISPLAY_NAME) file.name else file.length() })
        }
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? = throw UnsupportedOperationException()

    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException()

    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException()

    companion object {
        private val NAME = Regex("[A-Za-z0-9_-]{1,100}\\.pdf")

        /** The folder for downloaded tickets (cache: Android may clear it; tickets can always be downloaded again). */
        fun directory(context: Context): File = File(context.cacheDir, "tickets").apply { mkdirs() }

        fun uri(context: Context, file: File): Uri =
            Uri.Builder().scheme("content").authority("${context.packageName}.tickets").appendPath(file.name).build()
    }
}
