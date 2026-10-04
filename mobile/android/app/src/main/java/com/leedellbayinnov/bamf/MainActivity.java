package com.leedellbayinnov.bamf;

import android.os.Bundle;
import android.webkit.CookieManager;
import android.webkit.WebView;
import androidx.activity.OnBackPressedCallback;
import com.getcapacitor.BridgeActivity;

public class MainActivity extends BridgeActivity {
    @Override
    public void onCreate(Bundle savedInstanceState) {
        registerPlugin(BamfDiscoveryPlugin.class);
        super.onCreate(savedInstanceState);

        // Back walks the WebView's history first: from a server's dashboard it
        // ends at the finder, where another server can be chosen. Only with
        // nothing left to go back to does it leave the app.
        getOnBackPressedDispatcher().addCallback(this, new OnBackPressedCallback(true) {
            @Override
            public void handleOnBackPressed() {
                WebView web = getBridge() == null ? null : getBridge().getWebView();
                if (web != null && web.canGoBack()) {
                    web.goBack();
                } else {
                    setEnabled(false);
                    getOnBackPressedDispatcher().onBackPressed();
                }
            }
        });
    }

    // A server's sign-in is a cookie. The WebView writes cookies to disk lazily,
    // so one set just before the app is swiped away could be lost and the next
    // launch would ask for the password again. Writing them out whenever the
    // app leaves the screen avoids that.
    @Override
    public void onPause() {
        super.onPause();
        CookieManager.getInstance().flush();
    }
}
