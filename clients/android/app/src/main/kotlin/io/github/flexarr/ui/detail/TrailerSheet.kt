package io.github.flexarr.ui.detail

import android.annotation.SuppressLint
import android.webkit.WebChromeClient
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import io.github.flexarr.core.youTubeId
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.Tokens

/**
 * A title's trailer in a sheet: YouTube's privacy-enhanced embed in a web view, as the web app's TrailerDialog. Only a
 * real YouTube id reaches the embed's address; closing the sheet stops it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TrailerSheet(videoId: String, title: String, onClose: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onClose, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.seat) {
        Column(Modifier.navigationBarsPadding().padding(horizontal = Edge).padding(bottom = 24.dp)) {
            Text(title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.padding(bottom = 12.dp))
            val id = youTubeId(videoId)
            if (id == null) {
                Text("This trailer can’t be played.", fontSize = 15.sp, color = Flexarr.usher)
            } else {
                TrailerPlayer(id, Modifier.fillMaxWidth().aspectRatio(16f / 9f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Color.Black))
            }
        }
    }
}

@SuppressLint("SetJavaScriptEnabled")
@Composable
private fun TrailerPlayer(id: String, modifier: Modifier) {
    AndroidView(
        factory = { context ->
            WebView(context).apply {
                setBackgroundColor(android.graphics.Color.BLACK)
                settings.javaScriptEnabled = true
                settings.mediaPlaybackRequiresUserGesture = false
                webViewClient = WebViewClient()
                webChromeClient = WebChromeClient()
                // YouTube refuses an embed that doesn't say which site it's on (error 153): the page's base is
                // flexarr.io, so the frame sends that origin and nothing else.
                loadDataWithBaseURL("https://flexarr.io", trailerPage(id), "text/html", "utf-8", null)
            }
        },
        onRelease = { it.destroy() },
        modifier = modifier,
    )
}

private fun trailerPage(id: String) = """
    <!DOCTYPE html>
    <html><head><meta name="viewport" content="width=device-width, initial-scale=1">
    <style>html,body{margin:0;height:100%;background:#000}iframe{border:0;width:100%;height:100%}</style></head>
    <body><iframe src="https://www.youtube-nocookie.com/embed/$id?autoplay=1&rel=0&playsinline=1"
      allow="autoplay; encrypted-media; picture-in-picture" referrerpolicy="strict-origin-when-cross-origin"></iframe></body></html>
""".trimIndent()
