package io.github.flexarr.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.i18n.t
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PinnedPageTitle
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.launch

/** What Settings says beside Language: the language's own name, or Device language. */
internal fun languageValue(model: AppModel): String =
    model.profileLanguage?.let { code -> model.languages.firstOrNull { it.code == code }?.name } ?: t("Device language")

/** The profile's language, on every device; unset, each device's own. */
@Composable
fun LanguageSettingsScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    var error by remember { mutableStateOf<String?>(null) }
    var saving by remember { mutableStateOf(false) }

    fun choose(code: String?) {
        if (saving) return
        saving = true
        error = null
        model.scope.launch {
            try {
                model.setLanguage(code)
            } catch (e: FlexarrException) {
                error = e.message
            } finally {
                saving = false
            }
        }
    }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            stickyHeader { PinnedPageTitle(t("Language"), onBack = { navigator.back() }) }
            item {
                Group(footer = error ?: t("This profile’s own, on every device.")) {
                    Choice(t("Device language"), selected = model.profileLanguage == null) { choose(null) }
                    model.languages.forEach { language ->
                        Choice(language.name, selected = model.profileLanguage == language.code) { choose(language.code) }
                    }
                }
            }
        }
    }
}
