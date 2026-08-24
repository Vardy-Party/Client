# Copilot Instructions

## Project Guidelines
- User wants CI/CD to merge generated settings into existing appsettings.json instead of overwriting it.
- User prefers CI skip controls to be top-level YAML variables in workflow files rather than workflow_dispatch inputs.
- When introducing shared local-service code, use `VardyParty.LocalService.Client` (not `LocalService.Shared`).
- User requires no .csproj modifications while troubleshooting local Android CLI build issues; prefer command-only fixes first.

## Test hard rules

- Every `[Fact]` / `[Theory]` must contain `// Arrange`, `// Act`, and `// Assert` sections (in that order).
- Create specimens with AutoFixture (`AutoMoqFixture.Create()`, `Build`/`Create` in the test). Do not hand-construct domain graphs.
- Use `_fixture.GetMock<T>()` for collaborators. Never `new Mock<T>()`.
- Test data must be fictional: no real teams, leagues, competitions, or streaming sites.
- Do not auto-report a stream as bad when user requests the next stream.
- Provide a separate bad-stream reporting action and accurate switch-pending messaging while resolving M3U8.
- Disguised URLs such as .woff2 may still be valid HLS manifests; do not reject playback candidates by file extension.