package io.github.flexarr

import coil3.Uri
import coil3.intercept.Interceptor
import coil3.key.Keyer
import coil3.request.ImageResult
import coil3.request.Options
import io.github.flexarr.core.ServerAddresses
import io.github.flexarr.data.ServerCredentials
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * Artwork is cached by its address as it reads at home ([io.github.flexarr.core.ServerAddresses.homeForm]), not the
 * one it was fetched from: a poster seen at home isn't fetched again over mobile data on the away address, and back
 * home again after. [homeForm] is the signed-in server's, or leaves the address as it is.
 */
internal class ArtworkMemoryKeyer(private val homeForm: (String) -> String) : Keyer<Uri> {
    // Null for anything not the away address's, so Coil's own key is used as before.
    override fun key(data: Uri, options: Options): String? {
        val url = data.toString()
        return homeForm(url).takeIf { it != url }
    }
}

/** The disk cache's side of [ArtworkMemoryKeyer]: Coil keys that by the address fetched unless it's told otherwise. */
internal class ArtworkDiskKeys(private val homeForm: (String) -> String) : Interceptor {
    override suspend fun intercept(chain: Interceptor.Chain): ImageResult {
        val request = chain.request
        val url = when (val data = request.data) {
            is String -> data
            is Uri -> data.toString()
            else -> null
        }
        if (url == null || request.diskCacheKey != null) return chain.proceed()
        val home = homeForm(url)
        if (home == url) return chain.proceed()
        return chain.withRequest(request.newBuilder().diskCacheKey(home).build()).proceed()
    }
}

/**
 * The signed-in server's [ServerAddresses.homeForm], from its sign-in as it is now ([credentials]): the addresses are
 * worked out again only when the sign-in changes, not for every picture.
 */
internal class ArtworkHomeForm(private val credentials: () -> ServerCredentials?) : (String) -> String {
    @Volatile
    private var kept: Pair<ServerCredentials, ServerAddresses?>? = null

    override fun invoke(url: String): String {
        val current = credentials() ?: return url
        val known = kept?.takeIf { it.first == current } ?: run {
            val home = current.address.toHttpUrlOrNull()
            // A guest has no away address (see [ServerCredentials.guest]).
            val remote = current.remoteAddress?.toHttpUrlOrNull()?.takeIf { current.guest == null }
            (current to if (home != null && remote != null) ServerAddresses(home, remote) else null).also { kept = it }
        }
        return known.second?.homeForm(url) ?: url
    }
}
