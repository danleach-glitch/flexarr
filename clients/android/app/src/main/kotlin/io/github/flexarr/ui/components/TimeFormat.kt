package io.github.flexarr.ui.components

import android.content.Context
import android.text.format.DateFormat
import io.github.flexarr.core.i18n.Localization
import java.text.SimpleDateFormat

/** A time of day in the language the app shows (not the phone's), keeping the phone's 12 or 24 hour clock. */
fun endsFormat(context: Context): java.text.DateFormat {
    val locale = Localization.locale
    return SimpleDateFormat(DateFormat.getBestDateTimePattern(locale, if (DateFormat.is24HourFormat(context)) "Hm" else "hm"), locale)
}
