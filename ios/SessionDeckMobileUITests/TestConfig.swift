enum TestConfig {
    // Overwritten on the build machine before a device run; empty skips the test.
    static let host = ""
    // "address:port CODE" of a test gateway to pair with for this run; a test that needs one fails without it.
    static let pair = ""
}
