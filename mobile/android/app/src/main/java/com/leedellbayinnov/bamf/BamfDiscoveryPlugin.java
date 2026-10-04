package com.leedellbayinnov.bamf;

import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.Uri;
import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import org.json.JSONObject;

/**
 * Finds BAMF servers on the phone's Wi-Fi network and opens one in the WebView.
 *
 * Discovery is a TCP connect to BAMF's port on each address of the phone's own
 * subnet (never more than a /22), then a GET of /manifest.webmanifest, which
 * BAMF serves without sign-in and names "BAMF Network Monitor". A port that is
 * open is not enough to count: something else may be listening on it.
 *
 * Only plain HTTP is probed. The manifest holds nothing private, and keeping
 * discovery free of any "trust every certificate" code is what Google Play
 * expects. A server reached over HTTPS is entered by hand.
 */
@CapacitorPlugin(name = "BamfDiscovery")
public class BamfDiscoveryPlugin extends Plugin {
    private static final String MANIFEST_NAME = "BAMF Network Monitor";
    private static final int DEFAULT_PORT = 8840;
    private static final int MAX_PREFIX_SPAN = 22;   // never scan more than a /22 (1022 hosts)
    private static final int THREADS = 64;
    private static final int CONNECT_MS = 600;
    private static final int HTTP_MS = 1500;

    private volatile boolean cancelled = false;
    private volatile ExecutorService running = null;

    // The server the WebView is showing. Navigation within it stays in the app; anything else goes to the browser.
    private volatile String openHost = null;

    // ---- Wi-Fi ----

    private static final class Net { String address; int prefix; }

    /** The phone's IPv4 address on Wi-Fi or Ethernet, or null (cellular and VPNs are not scanned). */
    private Net lanAddress() {
        ConnectivityManager cm = (ConnectivityManager) getContext().getSystemService(android.content.Context.CONNECTIVITY_SERVICE);
        if (cm == null) return null;
        for (Network n : cm.getAllNetworks()) {
            NetworkCapabilities caps = cm.getNetworkCapabilities(n);
            if (caps == null) continue;
            if (!(caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))) continue;
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) continue;
            LinkProperties lp = cm.getLinkProperties(n);
            if (lp == null) continue;
            for (LinkAddress la : lp.getLinkAddresses()) {
                if (la.getAddress() instanceof Inet4Address && !la.getAddress().isLoopbackAddress()) {
                    Net out = new Net();
                    out.address = la.getAddress().getHostAddress();
                    out.prefix = la.getPrefixLength();
                    return out;
                }
            }
        }
        return null;
    }

    private static long toLong(String ipv4) {
        String[] p = ipv4.split("\\.");
        long v = 0;
        for (String s : p) v = (v << 8) | (Integer.parseInt(s) & 0xff);
        return v;
    }

    private static String fromLong(long v) {
        return ((v >> 24) & 0xff) + "." + ((v >> 16) & 0xff) + "." + ((v >> 8) & 0xff) + "." + (v & 0xff);
    }

    @PluginMethod
    public void getNetworkInfo(PluginCall call) {
        Net n = lanAddress();
        if (n == null) { call.reject("Not on Wi-Fi", "no-wifi"); return; }
        int prefix = Math.max(n.prefix, MAX_PREFIX_SPAN);
        long mask = prefix == 0 ? 0 : (0xffffffffL << (32 - prefix)) & 0xffffffffL;
        JSObject r = new JSObject();
        r.put("address", n.address);
        r.put("prefixLength", n.prefix);
        r.put("scanned", fromLong(toLong(n.address) & mask) + "/" + prefix);
        call.resolve(r);
    }

    // ---- discovery ----

    @PluginMethod
    public void discover(PluginCall call) {
        Net n = lanAddress();
        if (n == null) { call.reject("Not on Wi-Fi", "no-wifi"); return; }
        int port = call.getInt("port", DEFAULT_PORT);
        cancel();
        cancelled = false;

        int prefix = Math.max(n.prefix, MAX_PREFIX_SPAN);
        long self = toLong(n.address);
        long mask = prefix >= 32 ? 0xffffffffL : (0xffffffffL << (32 - prefix)) & 0xffffffffL;
        long network = self & mask;
        long count = 1L << (32 - prefix);
        final List<String> hosts = new ArrayList<>();
        for (long i = 1; i < count - 1; i++) {
            long a = network + i;
            if (a != self) hosts.add(fromLong(a));
        }

        final ExecutorService pool = Executors.newFixedThreadPool(THREADS);
        running = pool;
        final AtomicInteger done = new AtomicInteger(0);
        final AtomicInteger found = new AtomicInteger(0);
        final int total = hosts.size();

        for (final String host : hosts) {
            pool.submit(() -> {
                try {
                    if (cancelled) return;
                    if (isOpen(host, port)) {
                        JSObject server = identify("http://" + host + ":" + port);
                        if (server != null) {
                            found.incrementAndGet();
                            notifyListeners("serverFound", server);
                        }
                    }
                } finally {
                    int d = done.incrementAndGet();
                    if (d % 32 == 0 || d == total) {
                        JSObject p = new JSObject();
                        p.put("done", d);
                        p.put("total", total);
                        notifyListeners("progress", p);
                    }
                }
            });
        }
        pool.shutdown();
        new Thread(() -> {
            try { pool.awaitTermination(120, TimeUnit.SECONDS); } catch (InterruptedException ignored) { }
            JSObject r = new JSObject();
            r.put("scanned", total);
            r.put("found", found.get());
            r.put("cancelled", cancelled);
            call.resolve(r);
        }).start();
    }

    @PluginMethod
    public void cancel(PluginCall call) {
        cancel();
        call.resolve();
    }

    private void cancel() {
        cancelled = true;
        ExecutorService p = running;
        if (p != null) p.shutdownNow();
    }

    private static boolean isOpen(String host, int port) {
        try (Socket s = new Socket()) {
            s.connect(new InetSocketAddress(host, port), CONNECT_MS);
            return true;
        } catch (Exception e) {
            return false;
        }
    }

    /** Is this URL a BAMF server? Returns its description, or null. */
    private static JSObject identify(String base) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(base + "/manifest.webmanifest").openConnection();
            c.setConnectTimeout(HTTP_MS);
            c.setReadTimeout(HTTP_MS);
            c.setInstanceFollowRedirects(false);
            c.setRequestProperty("Accept", "application/manifest+json, application/json");
            if (c.getResponseCode() != 200) return null;
            ByteArrayOutputStream buf = new ByteArrayOutputStream();
            try (InputStream in = c.getInputStream()) {
                byte[] chunk = new byte[1024];
                int r;
                while ((r = in.read(chunk)) > 0 && buf.size() < 8192) buf.write(chunk, 0, r);
            }
            JSONObject m = new JSONObject(buf.toString("UTF-8"));
            if (!MANIFEST_NAME.equals(m.optString("name"))) return null;
            Uri u = Uri.parse(base);
            JSObject out = new JSObject();
            out.put("url", base);
            out.put("host", u.getHost());
            out.put("port", u.getPort());
            return out;
        } catch (Exception e) {
            return null;
        } finally {
            if (c != null) c.disconnect();
        }
    }

    /** Is the server at this address BAMF? (A saved server, or one typed in.) */
    @PluginMethod
    public void check(PluginCall call) {
        final String url = call.getString("url");
        if (url == null || !allowedTarget(Uri.parse(url))) { call.reject("Not an address BAMF can open", "bad-url"); return; }
        new Thread(() -> {
            String base = url.replaceAll("/+$", "");
            JSObject r = identify(base);
            JSObject out = new JSObject();
            out.put("ok", r != null);
            out.put("url", base);
            call.resolve(out);
        }).start();
    }

    // ---- opening a server ----

    /**
     * Plain HTTP only to addresses on a private network (or a name that can only
     * be one: single-label or .local). HTTPS to anything, since a server reached
     * from outside the home sits behind a real certificate.
     */
    static boolean allowedTarget(Uri u) {
        String scheme = u.getScheme();
        String host = u.getHost();
        if (scheme == null || host == null || host.isEmpty()) return false;
        if (scheme.equals("https")) return true;
        if (!scheme.equals("http")) return false;
        if (host.matches("\\d{1,3}(\\.\\d{1,3}){3}")) {
            String[] p = host.split("\\.");
            int a = Integer.parseInt(p[0]), b = Integer.parseInt(p[1]);
            return a == 10 || a == 127
                || (a == 172 && b >= 16 && b <= 31)
                || (a == 192 && b == 168)
                || (a == 169 && b == 254)
                || (a == 100 && b >= 64 && b <= 127);   // Tailscale and other carrier-grade-NAT space
        }
        return host.endsWith(".local") || !host.contains(".");
    }

    @PluginMethod
    public void openServer(PluginCall call) {
        final String url = call.getString("url");
        final Uri u = url == null ? null : Uri.parse(url);
        if (u == null || !allowedTarget(u)) { call.reject("Not an address BAMF can open", "bad-url"); return; }
        openHost = u.getHost();
        getActivity().runOnUiThread(() -> getBridge().getWebView().loadUrl(url));
        call.resolve();
    }

    /** Let the opened server move around inside itself; everything else takes the default (the browser). */
    @Override
    public Boolean shouldOverrideLoad(Uri url) {
        String h = openHost;
        if (h != null && h.equals(url.getHost()) && ("http".equals(url.getScheme()) || "https".equals(url.getScheme()))) return false;
        return null;
    }
}
