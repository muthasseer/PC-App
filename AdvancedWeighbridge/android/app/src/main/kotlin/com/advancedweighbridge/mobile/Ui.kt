package com.advancedweighbridge.mobile

import android.content.Context
import android.content.res.Configuration
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.text.InputType
import android.text.TextUtils
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.Spinner
import android.widget.TextView
import com.advancedweighbridge.core.LiveState

// Small helpers for building screens in code. Layouts are simple columns and rows, so code keeps them readable
// without XML layout files.

internal const val MATCH = ViewGroup.LayoutParams.MATCH_PARENT
internal const val WRAP = ViewGroup.LayoutParams.WRAP_CONTENT

internal fun Context.dp(value: Int): Int = (value * resources.displayMetrics.density + 0.5f).toInt()

internal val Context.isNight: Boolean
    get() = resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES

internal fun Context.column(paddingDp: Int = 0): LinearLayout = LinearLayout(this).apply {
    orientation = LinearLayout.VERTICAL
    val p = dp(paddingDp)
    setPadding(p, p, p, p)
}

internal fun Context.row(): LinearLayout = LinearLayout(this).apply {
    orientation = LinearLayout.HORIZONTAL
    gravity = Gravity.CENTER_VERTICAL
}

/** Adds [view] to a LinearLayout with the given size, weight and top margin; returns the view. */
internal fun <T : View> LinearLayout.add(view: T, width: Int = MATCH, height: Int = WRAP, weight: Float = 0f, topDp: Int = 0): T {
    addView(view, LinearLayout.LayoutParams(width, height, weight).apply { topMargin = context.dp(topDp) })
    return view
}

internal enum class TextKind(val appearance: Int) {
    DISPLAY(android.R.style.TextAppearance_Material_Display1),
    HEADLINE(android.R.style.TextAppearance_Material_Headline),
    TITLE(android.R.style.TextAppearance_Material_Title),
    SUBHEAD(android.R.style.TextAppearance_Material_Subhead),
    BODY(android.R.style.TextAppearance_Material_Body1),
    CAPTION(android.R.style.TextAppearance_Material_Caption),
}

internal fun Context.text(value: CharSequence = "", kind: TextKind = TextKind.BODY, bold: Boolean = false, color: Int? = null): TextView =
    TextView(this).apply {
        setTextAppearance(kind.appearance)
        text = value
        if (bold) setTypeface(typeface, Typeface.BOLD)
        color?.let { setTextColor(it) }
    }

internal object Inputs {
    const val TEXT = InputType.TYPE_CLASS_TEXT
    const val NAME = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
    const val URL = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
    const val PASSWORD = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
    const val CODE = InputType.TYPE_CLASS_NUMBER
    const val MULTILINE = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
}

internal fun Context.input(hint: String, type: Int = Inputs.TEXT, value: String = "", autofill: String? = null): EditText = EditText(this).apply {
    this.hint = hint
    inputType = type
    setText(value)
    contentDescription = hint
    if (autofill != null) {
        setAutofillHints(autofill)
        importantForAutofill = View.IMPORTANT_FOR_AUTOFILL_YES
    } else {
        importantForAutofill = View.IMPORTANT_FOR_AUTOFILL_NO
    }
}

internal fun Context.button(label: String, primary: Boolean = true, onClick: () -> Unit): Button =
    Button(this, null, 0, if (primary) android.R.style.Widget_Material_Button_Colored else android.R.style.Widget_Material_Button_Borderless_Colored).apply {
        text = label
        setOnClickListener { onClick() }
    }

/** A spinner that calls [onSelect] only when the user picks a different entry. */
internal fun Context.choice(items: List<String>, selected: Int = 0, onSelect: (Int) -> Unit): Spinner = Spinner(this).apply {
    adapter = ArrayAdapter(context, android.R.layout.simple_spinner_item, items).also { it.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item) }
    setSelection(selected.coerceIn(0, (items.size - 1).coerceAtLeast(0)), false)
    var current = selectedItemPosition
    onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
        override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
            if (position != current) {
                current = position
                onSelect(position)
            }
        }

        override fun onNothingSelected(parent: AdapterView<*>?) = Unit
    }
}

internal fun Context.rounded(fill: Int, stroke: Int? = null, radiusDp: Int = 12): GradientDrawable = GradientDrawable().apply {
    cornerRadius = dp(radiusDp).toFloat()
    setColor(fill)
    stroke?.let { setStroke(dp(1), it) }
}

internal fun Context.card(): LinearLayout = column(14).apply {
    background = rounded(getColor(R.color.card), getColor(R.color.card_stroke))
}

/** A coloured label with text: state is never shown by colour alone. */
internal fun Context.badge(label: String, colorRes: Int): TextView = text(label, TextKind.CAPTION, bold = true, color = getColor(R.color.on_state)).apply {
    background = rounded(getColor(colorRes), radiusDp = 6)
    setPadding(dp(8), dp(3), dp(8), dp(3))
    maxLines = 1
    ellipsize = TextUtils.TruncateAt.END
}

/** A "label: value" line used on detail screens. */
internal fun Context.field(label: String, value: String?): LinearLayout = row().apply {
    gravity = Gravity.TOP
    add(text(label, TextKind.BODY, color = getColor(R.color.text_secondary)), width = 0, weight = 0.4f)
    add(text(value?.takeIf { it.isNotBlank() } ?: "—", TextKind.BODY), width = 0, weight = 0.6f)
    setPadding(0, dp(3), 0, dp(3))
}

internal fun Context.selectableBackground(): Int {
    val tv = TypedValue()
    theme.resolveAttribute(android.R.attr.selectableItemBackground, tv, true)
    return tv.resourceId
}

internal fun stateColor(state: LiveState): Int = when (state) {
    LiveState.LIVE_STABLE -> R.color.state_stable
    LiveState.LIVE_MOTION -> R.color.state_motion
    LiveState.NOT_LIVE -> R.color.state_not_live
    LiveState.DISCONNECTED -> R.color.state_error
    LiveState.NOT_SHARED -> R.color.state_info
}
