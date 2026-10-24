package com.advancedweighbridge.mobile

import android.annotation.SuppressLint
import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import android.util.Log
import com.advancedweighbridge.core.TokenStore
import java.security.GeneralSecurityException
import java.security.KeyStore
import java.security.ProviderException
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Keeps the refresh token encrypted with an AES-256-GCM key that never leaves the Android Keystore. Passwords are
 * never stored. If the key becomes unusable (for example after a device restore) the token is discarded and the user
 * signs in again.
 */
@SuppressLint("ApplySharedPref") // commit is intended: tokens are written from background threads and must be durable
class KeystoreTokenStore(context: Context) : TokenStore {
    private val prefs = context.getSharedPreferences("session", Context.MODE_PRIVATE)
    private val lock = Any()

    fun hasToken(): Boolean = prefs.contains(KEY)

    override fun load(): String? = synchronized(lock) {
        val stored = prefs.getString(KEY, null) ?: return null
        try {
            val bytes = Base64.decode(stored, Base64.NO_WRAP)
            val ivLength = bytes[0].toInt()
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes, 1, ivLength))
            String(cipher.doFinal(bytes, 1 + ivLength, bytes.size - 1 - ivLength), Charsets.UTF_8)
        } catch (e: GeneralSecurityException) {
            discard(e)
        } catch (e: IllegalArgumentException) {
            discard(e)
        } catch (e: IndexOutOfBoundsException) {
            discard(e)
        } catch (e: ProviderException) {
            discard(e)
        }
    }

    override fun save(refreshToken: String?) {
        synchronized(lock) {
            if (refreshToken == null) {
                prefs.edit().remove(KEY).commit()
                return
            }
            try {
                val cipher = Cipher.getInstance(TRANSFORMATION)
                cipher.init(Cipher.ENCRYPT_MODE, key())
                val iv = cipher.iv
                val sealed = cipher.doFinal(refreshToken.toByteArray(Charsets.UTF_8))
                val out = ByteArray(1 + iv.size + sealed.size)
                out[0] = iv.size.toByte()
                iv.copyInto(out, 1)
                sealed.copyInto(out, 1 + iv.size)
                // commit (not apply): a rotated token must reach storage before the old one is used up.
                prefs.edit().putString(KEY, Base64.encodeToString(out, Base64.NO_WRAP)).commit()
            } catch (e: GeneralSecurityException) {
                // Without a working keystore the session lasts only while the app runs; nothing is stored in clear text.
                Log.e(TAG, "The session token could not be encrypted and is not stored", e)
                prefs.edit().remove(KEY).commit()
            } catch (e: ProviderException) {
                Log.e(TAG, "The session token could not be encrypted and is not stored", e)
                prefs.edit().remove(KEY).commit()
            }
        }
    }

    private fun discard(e: Exception): String? {
        Log.w(TAG, "Stored session could not be decrypted; signing in again is required", e)
        prefs.edit().remove(KEY).commit()
        return null
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance(PROVIDER).apply { load(null) }
        (store.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, PROVIDER)
        generator.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    private companion object {
        const val TAG = "AwmsSession"
        const val PROVIDER = "AndroidKeyStore"
        const val ALIAS = "awms-session"
        const val KEY = "refresh"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
    }
}
