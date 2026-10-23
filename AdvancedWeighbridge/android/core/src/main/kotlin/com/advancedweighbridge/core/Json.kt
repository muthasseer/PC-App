package com.advancedweighbridge.core

/**
 * Minimal JSON reader/writer (RFC 8259) for the AWMS API. Kept dependency-free so that the same code runs on Android
 * and in plain JVM unit tests. Numbers are kept as their text and converted on access (weights are integer grams).
 */
sealed class Json {
    data class Obj(val fields: Map<String, Json>) : Json()
    data class Arr(val items: List<Json>) : Json()
    data class Str(val value: String) : Json()
    data class Num(val text: String) : Json()
    data class Bool(val value: Boolean) : Json()
    data object Null : Json()

    operator fun get(key: String): Json = (this as? Obj)?.fields?.get(key) ?: Null

    val str: String? get() = when (this) { is Str -> value; is Num -> text; is Bool -> value.toString(); else -> null }
    val long: Long? get() = when (this) { is Num -> text.toLongOrNull() ?: text.toDoubleOrNull()?.toLong(); is Str -> value.toLongOrNull(); else -> null }
    val int: Int? get() = long?.toInt()
    val double: Double? get() = when (this) { is Num -> text.toDoubleOrNull(); is Str -> value.toDoubleOrNull(); else -> null }
    val bool: Boolean? get() = when (this) { is Bool -> value; is Str -> value.equals("true", ignoreCase = true); else -> null }
    val list: List<Json> get() = (this as? Arr)?.items ?: emptyList()
    val isNull: Boolean get() = this is Null

    fun stringify(): String = StringBuilder().also { write(it) }.toString()

    private fun write(sb: StringBuilder) {
        when (this) {
            is Obj -> {
                sb.append('{')
                fields.entries.forEachIndexed { i, (k, v) -> if (i > 0) sb.append(','); quote(k, sb); sb.append(':'); v.write(sb) }
                sb.append('}')
            }
            is Arr -> { sb.append('['); items.forEachIndexed { i, v -> if (i > 0) sb.append(','); v.write(sb) }; sb.append(']') }
            is Str -> quote(value, sb)
            is Num -> sb.append(text)
            is Bool -> sb.append(value)
            Null -> sb.append("null")
        }
    }

    companion object {
        fun parse(text: String): Json = Parser(text).run()

        fun obj(vararg pairs: Pair<String, Any?>): Obj = Obj(pairs.associate { (k, v) -> k to of(v) })

        fun of(v: Any?): Json = when (v) {
            null -> Null
            is Json -> v
            is String -> Str(v)
            is Boolean -> Bool(v)
            is Int, is Long -> Num(v.toString())
            is Number -> Num(v.toString())
            is List<*> -> Arr(v.map { of(it) })
            is Map<*, *> -> Obj(v.entries.associate { (k, x) -> k.toString() to of(x) })
            else -> Str(v.toString())
        }

        private fun quote(s: String, sb: StringBuilder) {
            sb.append('"')
            for (c in s) {
                when (c) {
                    '"' -> sb.append("\\\"")
                    '\\' -> sb.append("\\\\")
                    '\n' -> sb.append("\\n")
                    '\r' -> sb.append("\\r")
                    '\t' -> sb.append("\\t")
                    else -> if (c < ' ') sb.append("\\u%04x".format(c.code)) else sb.append(c)
                }
            }
            sb.append('"')
        }
    }

    private class Parser(private val s: String) {
        private var i = 0

        fun run(): Json {
            val v = value()
            ws()
            if (i != s.length) fail("unexpected trailing characters")
            return v
        }

        private fun value(): Json {
            ws()
            if (i >= s.length) fail("unexpected end")
            return when (val c = s[i]) {
                '{' -> obj()
                '[' -> arr()
                '"' -> Str(string())
                't' -> literal("true", Bool(true))
                'f' -> literal("false", Bool(false))
                'n' -> literal("null", Null)
                else -> if (c == '-' || c.isDigit()) number() else fail("unexpected '$c'")
            }
        }

        private fun obj(): Json {
            i++
            val fields = LinkedHashMap<String, Json>()
            ws()
            if (peek() == '}') { i++; return Obj(fields) }
            while (true) {
                ws()
                if (peek() != '"') fail("expected a property name")
                val key = string()
                ws()
                expect(':')
                fields[key] = value()
                ws()
                when (next()) {
                    ',' -> continue
                    '}' -> return Obj(fields)
                    else -> fail("expected ',' or '}'")
                }
            }
        }

        private fun arr(): Json {
            i++
            val items = ArrayList<Json>()
            ws()
            if (peek() == ']') { i++; return Arr(items) }
            while (true) {
                items.add(value())
                ws()
                when (next()) {
                    ',' -> continue
                    ']' -> return Arr(items)
                    else -> fail("expected ',' or ']'")
                }
            }
        }

        private fun string(): String {
            expect('"')
            val sb = StringBuilder()
            while (true) {
                if (i >= s.length) fail("unterminated string")
                when (val c = s[i++]) {
                    '"' -> return sb.toString()
                    '\\' -> {
                        when (val e = next()) {
                            '"' -> sb.append('"'); '\\' -> sb.append('\\'); '/' -> sb.append('/')
                            'b' -> sb.append('\b'); 'f' -> sb.append('\u000C'); 'n' -> sb.append('\n'); 'r' -> sb.append('\r'); 't' -> sb.append('\t')
                            'u' -> {
                                if (i + 4 > s.length) fail("bad unicode escape")
                                sb.append(s.substring(i, i + 4).toInt(16).toChar())
                                i += 4
                            }
                            else -> fail("bad escape '\\$e'")
                        }
                    }
                    else -> sb.append(c)
                }
            }
        }

        private fun number(): Json {
            val start = i
            if (peek() == '-') i++
            while (i < s.length && (s[i].isDigit() || s[i] in ".eE+-")) i++
            val text = s.substring(start, i)
            if (text.toDoubleOrNull() == null) fail("bad number '$text'")
            return Num(text)
        }

        private fun literal(word: String, v: Json): Json {
            if (!s.startsWith(word, i)) fail("unexpected token")
            i += word.length
            return v
        }

        private fun ws() { while (i < s.length && s[i].isWhitespace()) i++ }
        private fun peek(): Char = if (i < s.length) s[i] else '\u0000'
        private fun next(): Char = if (i < s.length) s[i++] else fail("unexpected end")
        private fun expect(c: Char) { if (next() != c) fail("expected '$c'") }
        private fun fail(message: String): Nothing = throw JsonException("Invalid JSON at $i: $message")
    }
}

class JsonException(message: String) : Exception(message)
