# Contributing to Lolitacoding.Nacos

Thanks for taking the time to contribute. This document describes how the repository is
organised and the conventions every change must follow.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (the exact version is pinned in `global.json`)
- Docker, for the integration tests
- `git`

## Build and test

```bash
dotnet build -c Release
dotnet test tests/Lolitacoding.Nacos.Tests -c Release
```

The build is held to a strict bar: **zero warnings, zero errors**. `TreatWarningsAsErrors` is
enabled repository-wide, and the trim/AOT analyzers (`IL2xxx`, `IL3xxx`) are treated the same way
in the core library.

### Integration tests

Integration tests spin up real Nacos containers through Testcontainers.

```bash
docker compose -f tests/docker-compose.yml up -d          # start Nacos 2.x and 3.x
dotnet test tests/Lolitacoding.Nacos.IntegrationTests -c Release
docker compose -f tests/docker-compose.yml down
```

They are tagged `[Trait("Category", "Integration")]` and are excluded from the fast test run.

## Coding conventions

These are enforced by `.editorconfig` and the analyzers, not by review comments.

- `TargetFramework=net10.0`, `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`.
- **File-scoped namespaces**, `sealed` by default, `record`/`record struct` for immutable DTOs.
- Every asynchronous method takes a trailing `CancellationToken ct = default`.
- **Never** call `DateTime.Now`, `DateTime.UtcNow`, or `Task.Delay` in library code. Inject
  `TimeProvider` and use `timeProvider.GetUtcNow()` / `timeProvider.Delay(...)`.
- Logging goes through `[LoggerMessage]` source generation. String-interpolated log messages are
  rejected.
- JSON uses `System.Text.Json` source generation. `Newtonsoft.Json` is not allowed.
- No static singletons or global mutable state. Configurable behaviour lives in an options class
  validated by an `IValidateOptions<T>` implementation.
- Errors surface as the custom hierarchy rooted at `NacosException`
  (`NacosAuthException`, `NacosTimeoutException`, `NacosServerException`,
  `NacosConnectionException`), always preserving the server error code.
- Every public member carries XML documentation.

## Public API

The public surface is tracked by `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Adding a public
member fails the build with `RS0016` until it is recorded.

```bash
pwsh ./tools/sync-public-api.ps1
```

This script reconciles `PublicAPI.Unshipped.txt` with the compiled surface, adding new symbols and
pruning removed ones. Never edit the baseline files by hand to silence a warning.

## Tests

- Unit tests use **xUnit v3** with **Shouldly** assertions and **NSubstitute** for test doubles.
  The whole repository uses this one combination — do not introduce a second assertion library.
- The network layer is abstracted behind interfaces, so unit tests never touch a real socket.
- New behaviour should reach at least 80% line coverage.
- Integration tests are marked `[Trait("Category", "Integration")]`.

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(config): add MD5 verification to GetConfigAsync
fix(naming): re-register instances after reconnect
test(transport): cover exponential backoff with a fake TimeProvider
docs: document the failover cache directory
chore(ci): pin the Nacos container image tag
```

Pull requests should:

1. Reference the milestone task from `PLAN.md` (for example `T1.3`).
2. List each acceptance criterion from the plan with a self-check result.
3. Include the output of `dotnet build -c Release` and `dotnet test`.
4. Update `PLAN.md` when reality diverges from the plan, rather than diverging silently.

## Protocol changes

When touching the wire protocol, cite the upstream source you verified the behaviour against
(server tag, file, line) in the pull request, and record it in `docs/protocol-notes.md`. Do not
implement protocol details from memory — see section 6 of `PLAN.md`.
