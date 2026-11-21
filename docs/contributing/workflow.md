# Branch and release workflow

This is SharpMind Studio's own workflow. The same rules are used in the
[SharpMind](https://github.com/Integral2u/SharpMind) repository, but the two
projects have separate histories, separate versions and separate release lines:
SharpMind's released line is `master`, Studio's is `main`.

The workflow is deliberately local-first: commits are cheap, pushes are
checkpoints, and the released line only receives finished work.

## Principles

- Work and commit locally. Push only when a push buys something.
- The released line is for released code and urgent fixes, not day-to-day work.
- A PR is optional. Use one when you need a pre-merge gate or a review record.
- Keep one logical change per commit and per branch.
- Keep Studio's version and changelog independent of SharpMind's.

## Relationship to SharpMind

Studio sits on the SharpMind libraries, so during a cycle the two move together:

- Both checkouts are on matching `release/x.y.z` branches, side by side
  (`..\SharpMind` beside this repository).
- Engine API changes land in SharpMind first; Studio adapts afterwards.
- Studio never depends on an unreleased engine version in its package build.

## Branches

| Branch | Purpose |
| --- | --- |
| `main` | Released line. Release merges and urgent fixes only. |
| `release/x.y.z` | Integration branch for the current cycle (for example `release/1.1.3`). |
| `feat/<topic>` | Feature work, branched from the current `release/x.y.z`. |
| `fix/<topic>` | Bug fix for the current cycle, branched from `release/x.y.z`. |
| `docs/<topic>` | Documentation-only work, branched from the current `release/x.y.z`. |
| `store/<version>-resubmit` | Temporary Store hotfix branch. See [Store submission](#store-submission). |

- Keep work branches short-lived.
- Rebase a work branch on `release/x.y.z` before merging it back.
- Delete the work branch after it is merged, and delete the cycle branch after
  its release merge.
- Never rewrite history that is already pushed and shared.

## Daily loop

```powershell
git switch release/x.y.z
git pull --ff-only
git switch -c feat/my-change

# edit, build, test, commit locally

git switch feat/my-change
git rebase release/x.y.z
git switch release/x.y.z
git merge --ff-only feat/my-change
```

If the branch is already pushed and shared, merge it instead of rebasing it.

## When to push

Push a work or release branch when:

- you want a backup or safe point;
- you are handing the work to someone else;
- you need a gate that cannot run locally (CodeQL, a clean machine, an MSIX on
  fresh hardware);
- you are about to start a risky refactor.

```powershell
git push -u origin <branch>
```

Do not push `main` except for a release merge or an urgent fix.

## When to open a PR

No PR is required for normal local work. Open one only when:

- you want the full CI and CodeQL to run before the change reaches `main`;
- you want another person to review the diff;
- branch protection requires it;
- the change comes from an external contributor.

PRs must target `main` for CodeQL to run.

## Gates

Run focused tests while working. Run the full suite and the encoding guard
before merging into `release/x.y.z` or `main`, and before pushing a release.

```powershell
dotnet build SharpMind.Studio.slnx -c Release
dotnet test SharpMind.Studio.Tests\SharpMind.Studio.Tests.csproj -c Release --nologo -v q
pwsh ./scripts/check-encoding.ps1
```

The installer solutions are separate; see [CONTRIBUTING.md](../../CONTRIBUTING.md)
and `SharpMind.Studio Setup/README.md` for the MSI and MSIX builds.

## Engine source mode

Studio can build against the engine packages or the engine source:

| Branch | `SharpMindSource` | Needs `..\SharpMind` |
| --- | --- | --- |
| `main` | `package` | No |
| `release/x.y.z` | `project` | Yes, on the matching branch |

- `main` builds the published packages, so a clean clone needs no engine checkout.
- The release branch builds the sibling source tree, because that is where the
  cycle's engine changes are exercised.
- `-p:SharpMindSource=project` or `-p:SharpMindSource=package` overrides either
  branch for a one-off check.

Do not let this depend on what happens to be checked out next door: which engine
a binary used is decided by the branch, not by the file system.

## Commit messages

Use `type(scope): imperative summary`, for example:

```text
fix(vultr): follow plan pagination
feat(chat): remember the last model per provider
docs(workflow): describe the Store resubmission path
```

The body should explain why the change was needed, what changed, what was
verified, and any known limits. Do not mix a version bump with feature work.

## Versioning and changelog

- The version lives only in `Directory.Build.props`: `Version`,
  `AssemblyVersion` and `FileVersion`.
- Add release notes under `## [Unreleased]` in `CHANGELOG.md`.
- At release time, rename that section to `## [x.y.z.0] - YYYY-MM-DD` and add a
  fresh `## [Unreleased]`.
- Only the release owner bumps the version.
- Studio's version is independent of SharpMind's. Do not bump it to match the
  engine.

## Release

```powershell
git switch main
git pull --ff-only
git merge --no-ff release/x.y.z

# resolve the merge-down files below, then run the full gates on main

git push origin main
git tag vx.y.z.0
git push origin vx.y.z.0
```

Tag only when installers or packages are actually published. Merge to `main`
only when the cycle is complete or a fix must ship early. After the tag, start
the next cycle from `main` with a version bump as its own commit.

## Merge-down resolution

The release branch and `main` differ on purpose. When merging, keep `main`'s
process defaults and the branch's version:

| File | Keep |
| --- | --- |
| `Directory.Build.props` | The branch's version; `main`'s `package` default |
| `SharpMind.References.props` | `main`'s `package` default |
| `SharpMind.Studio.slnx` | `main`'s self-contained solution, without engine projects |
| `SharpMind.Studio.Package.slnx` | Deleted; `main` removed it when the solution became self-contained |
| `README.md` | `main`'s build section; the branch's feature text |

Installers and packages are built from `main`, so the merged `main` must build
without a sibling engine checkout.

## Store submission

The MSIX is the Store package; the MSI and ZIP remain the GitHub downloads until
the listing is live.

- The package version must be four parts with the revision at `0`
  (`x.y.z.0`). `scripts/build-msix.ps1` enforces this, because the Store
  reserves the fourth part.
- The normal path is: finish the cycle, merge to `main`, build the MSIX from
  `main`, and submit that version. A resubmission carries merged code, not a
  patched copy of the old line.

If a submitted version fails certification and the fix cannot wait for the
cycle:

```powershell
git switch -c store/1.1.2-resubmit v1.1.2.0
# fix, plus one version-bump commit on this branch only
```

- Base the branch on the released tag, not on `main`, so the package is exactly
  what was certified plus the fix.
- The package version is the next free `x.y.z.0`. That number is then spent;
  the cycle releases above it at merge-down.
- Cherry-pick the fix commits into `release/x.y.z`. Never merge the branch: its
  version-bump commit belongs only to the resubmission.
- Delete the branch once the package is accepted or abandoned.

## SharpMind package pin

When the package build is in use, the engine version is pinned in
`Directory.Packages.props`.

- Pin the last published SharpMind version, never an unreleased one.
- Test against the local engine build before changing the pin.
- Bump the pin in a single `build:` commit, only after SharpMind's version is
  final.

## CI triggers

| Workflow | Runs on | Does |
| --- | --- | --- |
| `encoding` | Any push and any PR | Encoding guard |
| `package-build` | Any push and any PR | Release build plus tests: package mode normally, engine source on `release/x.y.z` |
| `CodeQL` | Push to `main`, PR targeting `main`, weekly | Static analysis |
| `pages` | Push to `main` touching `docs/**` | Deploys the site |

Studio is a single Windows target, so its build and test run on every push: the
cost is about a minute and a backup push gets a real gate. The engine's
multi-platform matrix stays limited to `master` and PRs.

## Do not commit

- Model files and GGUF files. Keep them outside the repository, for example in
  `C:\Users\<you>\SharpMind\Models`.
- Datasets and benchmark artifacts.
- `bin/`, `obj/`, logs, local settings and secrets.
- `SharpMind.Studio Setup/Release/` and other build output.
- Large binaries that are not part of a release.

## Handoff note

Include this in every handoff:

```text
Branch:
HEAD commit:
Changed:
Verified:
Not verified:
Next step:
```
