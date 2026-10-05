import Capacitor
import UIKit

/// The app's one screen: Capacitor's web view with BAMF's discovery plugin added.
class BamfViewController: CAPBridgeViewController {
    override func capacitorDidLoad() {
        bridge?.registerPluginInstance(BamfDiscoveryPlugin())
        // Swiping in from the left edge walks the web view's history. From a
        // server's dashboard it ends at the finder, where another can be chosen.
        webView?.allowsBackForwardNavigationGestures = true
    }
}
