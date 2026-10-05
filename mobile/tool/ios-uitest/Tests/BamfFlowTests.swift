import XCTest

/// Drives the installed BAMF app (by bundle id) through the paths that need real
/// input: sign in, a sign-in that survives a restart, and the left-edge swipe
/// back to the finder. Run it with tool/ios-uitest/run.sh.
final class BamfFlowTests: XCTestCase {
    let bundle = "com.leedellbayinnov.bamf"
    var shots: String { ProcessInfo.processInfo.environment["BAMF_SHOTS"] ?? "/tmp" }
    var password: String { ProcessInfo.processInfo.environment["BAMF_PW"] ?? "" }

    func shot(_ name: String) {
        let png = XCUIScreen.main.screenshot().pngRepresentation
        try? png.write(to: URL(fileURLWithPath: "\(shots)/\(name).png"))
    }

    override func setUp() { continueAfterFailure = false }

    func testSignInRestartAndSwipeBack() throws {
        XCTAssertFalse(password.isEmpty, "BAMF_PW not passed in")
        let app = XCUIApplication(bundleIdentifier: bundle)
        app.launch()

        // 1. The finder reconnects to the saved server (or finds it) and shows its sign-in page.
        var web = app.webViews.firstMatch
        let field = web.secureTextFields.firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 90), "no sign-in page appeared")
        shot("1-signin-page")
        field.tap()
        field.typeText(password)
        shot("2-typed")
        web.buttons["Sign in"].tap()
        XCTAssertTrue(web.staticTexts["Total hosts"].waitForExistence(timeout: 45), "dashboard did not load after sign-in")
        shot("3-dashboard")

        // 2. Leave the app, relaunch it: the session should still be there.
        sleep(3)
        app.terminate()
        app.launch()
        web = app.webViews.firstMatch
        let back = web.staticTexts["Total hosts"].waitForExistence(timeout: 90)
        shot("4-after-relaunch")
        XCTAssertTrue(back, "asked to sign in again after a relaunch (the session was not kept)")

        // 3. Swipe in from the left edge: back to the finder, which must not reconnect by itself.
        let start = app.coordinate(withNormalizedOffset: CGVector(dx: 0.005, dy: 0.5))
        let end = app.coordinate(withNormalizedOffset: CGVector(dx: 0.8, dy: 0.5))
        start.press(forDuration: 0.1, thenDragTo: end)
        sleep(3)
        shot("5-after-swipe")
        web = app.webViews.firstMatch
        XCTAssertTrue(web.staticTexts["Choose a server."].waitForExistence(timeout: 20), "the edge swipe did not return to the finder")
        sleep(4)   // long enough for a wrongly automatic reconnect to happen
        XCTAssertTrue(web.staticTexts["Choose a server."].exists, "the finder reconnected by itself")
        shot("6-finder-stays")
    }
}
