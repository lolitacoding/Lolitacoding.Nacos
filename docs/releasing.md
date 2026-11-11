# Releasing

A git tag is the only thing that publishes. Every job that guards a release runs on the tag, the
packages are built once and validated before the feed sees them, and the version comes from the tag
through [MinVer](https://github.com/adamralph/minver) — no file in the repository carries it.

## Version scheme

Tags are `v<semver>`, and the version of a package is the tag without the `v`:

| Ref | Package version |
| --- | --- |
| a commit before the first tag | `0.1.0-preview.0.<height>` |
| `v0.1.0-preview.1` on the commit | `0.1.0-preview.1` |
| a commit after `v0.1.0-preview.1` | `0.1.0-preview.1.<height>` |
| `v0.1.0` on the commit | `0.1.0` |
| a commit after `v0.1.0` | `0.1.1-preview.0.<height>` |

`<height>` is the number of commits since the tag, so every push produces a version that is ordered
and unique even before the next tag exists. `MinVerMinimumMajorMinor` pins the `0.1` line until a
`v1.0.0` tag says otherwise.

## What a tag runs

All gates, then the push:

| Job | What it proves |
| --- | --- |
| Build and unit tests | Release build with warnings as errors, 900+ unit tests |
| Native AOT publish | the sample still publishes under `win-x64` with every `IL2xxx`/`IL3xxx` warning an error |
| Integration tests (three jobs) | the base suite on 3.0.2, 3.2.4 and 3.3.0-RC, the AI registry on 3.2.4, the agent registry and lock service on 3.3.0-RC |
| Pack and validate | exactly seven `.nupkg` and seven `.snupkg`, each with its `lib/net11.0` assembly, README and nuspec |
| Publish | downloads that artifact and pushes it; it never re-packs |

The publish job needs all of the above. It also requires the repository variable
`NUGET_PUBLISH_ENABLED` to be `true`; while it is unset or `false` the job is skipped and a tag is a
full rehearsal of everything except the push.

## Publishing

Publishing uses an API key today:

- Repository variable `NUGET_PUBLISH_ENABLED` = `true`.
- Repository secret `NUGET_API_KEY` = a nuget.org key scoped to push new packages and
  `Lolitacoding.*`.

With both set, pushing a tag publishes. `--skip-duplicate` makes a re-run of the same tag harmless:
a version that is already on the feed is skipped instead of failing the job.

To rehearse without publishing, push the tag while `NUGET_PUBLISH_ENABLED` is unset, confirm every
job is green and that the `nuget-packages` artifact holds the expected version for all seven ids,
then set the variable and re-run the workflow from the Actions tab. A re-run keeps the tag as its
ref, so the publish condition still holds.

A published version can be unlisted but not replaced, so a fix is always a new tag.

## Trusted publishing

nuget.org supports GitHub OIDC trusted publishing, which removes the long-lived key. The publish job
already grants `id-token: write`, so the switch is a policy plus one step:

1. On nuget.org, add a trusted publishing policy for owner `lolitacoding`, repository
   `Lolitacoding.Nacos` and workflow file `ci.yml`.
2. Add a repository variable `NUGET_USER` holding the nuget.org account name.
3. Replace the push step with a login step and use the key it returns:

   ```yaml
   - uses: NuGet/login@v1
     id: login
     with:
       user: ${{ vars.NUGET_USER }}

   - run: >
       dotnet nuget push ./artifacts/*.nupkg
       --api-key ${{ steps.login.outputs.NUGET_API_KEY }}
       --source https://api.nuget.org/v3/index.json
       --skip-duplicate
   ```

4. Delete `NUGET_API_KEY` once a release has gone through this path.

## First release checklist

1. `dotnet build Lolitacoding.Nacos.slnx -c Release` is clean and the unit tests pass.
2. The commit the tag points at is the one that was verified; nothing that belongs in the release is
   left uncommitted.
3. `git tag v0.1.0-preview.1 && git push origin v0.1.0-preview.1`.
4. Every job except Publish is green and the artifact contains `0.1.0-preview.1` for all seven ids.
5. Set `NUGET_PUBLISH_ENABLED` and `NUGET_API_KEY`, then re-run the workflow.
6. Check the seven package pages on nuget.org, then create the GitHub release from the tag.
