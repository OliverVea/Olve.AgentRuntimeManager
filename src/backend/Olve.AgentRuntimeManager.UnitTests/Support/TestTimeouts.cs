// Nothing may hang the test run (the pipeline once waited 21 minutes on one): a test that takes
// longer fails, by name. Waits inside tests are bounded tighter than this (their Guard).
[assembly: Timeout(60_000)]
