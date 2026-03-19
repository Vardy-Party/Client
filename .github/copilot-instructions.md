# Copilot Instructions

## Project Guidelines
- User wants CI/CD to merge generated settings into existing appsettings.json instead of overwriting it.
- User prefers CI skip controls to be top-level YAML variables in workflow files rather than workflow_dispatch inputs.
- When introducing shared local-service code, use `VardyParty.LocalService.Client` (not `LocalService.Shared`).

## Streaming Guidelines
- Do not auto-report a stream as bad when user requests the next stream.
- Provide a separate bad-stream reporting action and accurate switch-pending messaging while resolving M3U8.
- Disguised URLs such as .woff2 may still be valid HLS manifests; do not reject playback candidates by file extension.