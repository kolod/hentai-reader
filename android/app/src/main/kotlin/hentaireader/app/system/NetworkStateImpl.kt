package hentaireader.app.system

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import hentaireader.host.platform.NetworkState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * The default network as downloads see it (007 FR-023, research R9): connected, and metered or
 * not (mobile data). Wi-Fi-only downloads wait while it is metered and resume on Wi-Fi.
 */
class NetworkStateImpl(context: Context) : NetworkState {
    private val manager = context.getSystemService(ConnectivityManager::class.java)
    private val _status = MutableStateFlow(current())
    override val status: StateFlow<NetworkState.Status> = _status

    init {
        manager.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                _status.value = NetworkState.Status(true, !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED))
            }

            override fun onLost(network: Network) {
                _status.value = NetworkState.Status(false, true)
            }
        })
    }

    private fun current(): NetworkState.Status {
        val caps = manager.activeNetwork?.let { manager.getNetworkCapabilities(it) } ?: return NetworkState.Status(false, true)
        return NetworkState.Status(true, !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED))
    }
}
