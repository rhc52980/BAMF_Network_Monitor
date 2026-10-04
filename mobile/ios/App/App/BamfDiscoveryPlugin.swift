import Capacitor
import Foundation
import UIKit
import WebKit

/// Finds BAMF servers on the phone's Wi-Fi network and opens one in the web view.
/// The same contract as the Android plugin (BamfDiscoveryPlugin.java), so the
/// finder page in www/ is shared.
///
/// Discovery asks each address of the phone's own subnet (never more than a /22)
/// for /manifest.webmanifest, which BAMF serves without sign-in and names
/// "BAMF Network Monitor". An open port is not enough to count: something else
/// may be listening on it. Only plain HTTP is probed; the manifest holds
/// nothing private. A server reached over HTTPS is entered by hand.
@objc(BamfDiscoveryPlugin)
public class BamfDiscoveryPlugin: CAPPlugin, CAPBridgedPlugin {
    public let identifier = "BamfDiscoveryPlugin"
    public let jsName = "BamfDiscovery"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "getNetworkInfo", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "discover", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "cancel", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "check", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "openServer", returnType: CAPPluginReturnPromise),
    ]

    private static let manifestName = "BAMF Network Monitor"
    private static let defaultPort = 8840
    private static let maxPrefixSpan = 22   // never scan more than a /22 (1022 hosts)
    private static let concurrency = 64

    private var scan: Task<Void, Never>?
    /// The server the web view is showing. Navigation within it stays in the app;
    /// anything else is handed to the system (the browser).
    private var openHost: String?

    private let session: URLSession = {
        let c = URLSessionConfiguration.ephemeral
        c.timeoutIntervalForRequest = 1.5
        c.timeoutIntervalForResource = 3
        c.waitsForConnectivity = false
        c.httpShouldSetCookies = false
        c.requestCachePolicy = .reloadIgnoringLocalCacheData
        c.urlCache = nil
        return URLSession(configuration: c)
    }()

    // A scan is a burst of up to ~2,000 connection attempts. It has no business
    // continuing once the app is out of sight, so stop it as the app goes to the background.
    override public func load() {
        NotificationCenter.default.addObserver(forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main) { [weak self] _ in
            self?.scan?.cancel()
        }
    }

    // MARK: Wi-Fi

    /// The phone's IPv4 address on Wi-Fi or Ethernet (en*), or nil. Cellular and VPNs are not scanned.
    private func lanAddress() -> (address: UInt32, prefix: Int)? {
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return nil }
        defer { freeifaddrs(head) }
        var best: (UInt32, Int)?
        var p: UnsafeMutablePointer<ifaddrs>? = first
        while let cur = p {
            let ifa = cur.pointee
            p = ifa.ifa_next
            guard let sa = ifa.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET),
                  String(cString: ifa.ifa_name).hasPrefix("en"),
                  (ifa.ifa_flags & UInt32(IFF_UP)) != 0, (ifa.ifa_flags & UInt32(IFF_LOOPBACK)) == 0,
                  let nm = ifa.ifa_netmask else { continue }
            let addr = sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            if addr >> 16 == 0xA9FE { continue }   // 169.254 link-local: no real network
            let mask = nm.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            let found = (addr, mask.nonzeroBitCount)
            if String(cString: ifa.ifa_name) == "en0" { return found }
            if best == nil { best = found }
        }
        return best
    }

    private static func text(_ v: UInt32) -> String {
        "\(v >> 24 & 255).\(v >> 16 & 255).\(v >> 8 & 255).\(v & 255)"
    }

    private static func span(_ prefix: Int) -> (prefix: Int, mask: UInt32) {
        let p = max(prefix, maxPrefixSpan)
        return (p, p >= 32 ? 0xFFFF_FFFF : (0xFFFF_FFFF << UInt32(32 - p)) & 0xFFFF_FFFF)
    }

    @objc func getNetworkInfo(_ call: CAPPluginCall) {
        guard let n = lanAddress() else { call.reject("Not on Wi-Fi", "no-wifi"); return }
        let s = Self.span(n.prefix)
        call.resolve([
            "address": Self.text(n.address),
            "prefixLength": n.prefix,
            "scanned": "\(Self.text(n.address & s.mask))/\(s.prefix)",
        ])
    }

    // MARK: Discovery

    @objc func discover(_ call: CAPPluginCall) {
        guard let n = lanAddress() else { call.reject("Not on Wi-Fi", "no-wifi"); return }
        let port = call.getInt("port") ?? Self.defaultPort
        scan?.cancel()
        let s = Self.span(n.prefix)
        let network = n.address & s.mask
        let count = UInt32(1) << UInt32(32 - s.prefix)
        let hosts = (1..<(count - 1)).map { network + $0 }.filter { $0 != n.address }.map(Self.text)
        let total = hosts.count

        scan = Task { [weak self] in
            guard let self else { return }
            var found = 0, done = 0, next = 0
            await withTaskGroup(of: [String: Any]?.self) { group in
                func launch() {
                    guard next < hosts.count else { return }
                    let host = hosts[next]; next += 1
                    group.addTask { [weak self] in await self?.identify("http://\(host):\(port)") }
                }
                for _ in 0..<Self.concurrency { launch() }
                while let result = await group.next() {
                    done += 1
                    if let server = result {
                        found += 1
                        self.notifyListeners("serverFound", data: server)
                    }
                    if done % 32 == 0 || done == total { self.notifyListeners("progress", data: ["done": done, "total": total]) }
                    if Task.isCancelled { group.cancelAll(); continue }
                    launch()
                }
            }
            call.resolve(["scanned": total, "found": found, "cancelled": Task.isCancelled])
        }
    }

    @objc func cancel(_ call: CAPPluginCall) {
        scan?.cancel()
        call.resolve()
    }

    /// Is this URL a BAMF server? Returns its description, or nil.
    private func identify(_ base: String) async -> [String: Any]? {
        guard let url = URL(string: base + "/manifest.webmanifest") else { return nil }
        var req = URLRequest(url: url)
        req.setValue("application/manifest+json, application/json", forHTTPHeaderField: "Accept")
        guard let (data, resp) = try? await session.data(for: req),
              (resp as? HTTPURLResponse)?.statusCode == 200, data.count < 8192,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              json["name"] as? String == Self.manifestName,
              let u = URLComponents(string: base) else { return nil }
        return ["url": base, "host": u.host ?? "", "port": u.port ?? 0]
    }

    /// Is the server at this address BAMF? (A saved server, or one typed in.)
    @objc func check(_ call: CAPPluginCall) {
        guard let raw = call.getString("url"), let u = URL(string: raw), Self.allowedTarget(u) else {
            call.reject("Not an address BAMF can open", "bad-url"); return
        }
        let base = raw.replacingOccurrences(of: "/+$", with: "", options: .regularExpression)
        Task {
            let r = await identify(base)
            call.resolve(["ok": r != nil, "url": base])
        }
    }

    // MARK: Opening a server

    /// Plain HTTP only to addresses on a private network (or a name that can only
    /// be one: single-label or .local). HTTPS to anything, since a server reached
    /// from outside the home sits behind a real certificate.
    static func allowedTarget(_ u: URL) -> Bool {
        guard let scheme = u.scheme?.lowercased(), let host = u.host, !host.isEmpty else { return false }
        if scheme == "https" { return true }
        guard scheme == "http" else { return false }
        let parts = host.split(separator: ".").compactMap { Int($0) }
        if parts.count == 4, host.split(separator: ".").count == 4 {
            let a = parts[0], b = parts[1]
            return a == 10 || a == 127
                || (a == 172 && (16...31).contains(b))
                || (a == 192 && b == 168)
                || (a == 169 && b == 254)
                || (a == 100 && (64...127).contains(b))   // Tailscale and other carrier-grade-NAT space
        }
        return host.hasSuffix(".local") || !host.contains(".")
    }

    @objc func openServer(_ call: CAPPluginCall) {
        guard let raw = call.getString("url"), let u = URL(string: raw), Self.allowedTarget(u) else {
            call.reject("Not an address BAMF can open", "bad-url"); return
        }
        openHost = u.host
        DispatchQueue.main.async { [weak self] in
            self?.bridge?.webView?.load(URLRequest(url: u))
            call.resolve()
        }
    }

    /// Let the opened server move around inside itself; everything else takes the default (the browser).
    @objc public override func shouldOverrideLoad(_ navigationAction: WKNavigationAction) -> NSNumber? {
        guard let url = navigationAction.request.url, let h = openHost, url.host == h,
              url.scheme == "http" || url.scheme == "https" else { return nil }
        return false
    }
}
