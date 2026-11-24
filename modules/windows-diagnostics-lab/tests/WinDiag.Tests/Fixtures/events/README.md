# Event XML fixtures

These files are **hand-written test data** in the Windows event XML format (the format of `EventRecord.ToXml()` and
`wevtutil qe <log> /f:xml`). They were not captured from a real machine; computer names, users and SIDs are made up.
Real event logs are read by the Windows-only integration tests in CI.
