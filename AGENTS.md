## Agent skills

### Issue tracker

Issues live in GitHub Issues. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the five canonical labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, and `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

This is a single-context repo. See `docs/agents/domain.md`.

<!-- project-governance:start -->
## Project governance

This is the myFanControl repository. Keep `src/` for product code, `scripts/` for tooling, `tests/` for checks, `docs/` for stable documentation, `research/` for hardware investigation, and `release/` for local version deliveries. Keep `release/.gitkeep` tracked; generated deliveries and research artifacts are ignored.

Root exceptions: `README.md` is the repository entry point; `PRODUCT_README.md` is copied into product packages by `scripts/Publish-Product.ps1`; `HANDOFF.md` is the current handoff; `DEVELOPMENT_PLAN.md` is a linked historical plan; `NuGet.Config` and `.gitignore` are tool configuration. `licenses/` must stay at its current path for the project files. `.tools/` and `artifacts/` contain local dependencies, diagnostics, and generated outputs. Preserve historical diagnostics and unknown files.

Product versions are explicit Git tags and arguments to `scripts/Package-Release.ps1`; the latest published version is v0.1.3. Keep version claims aligned with the actual two-package delivery. Repository housekeeping alone does not create a new product version. For product releases, choose the next SemVer number based on behavior and update the current-version documentation.

Applicable offline checks: `& .tools/dotnet/dotnet.exe run --project tests/ControlPolicyVerification/ControlPolicyVerification.csproj -c Release` and `& .tools/dotnet/dotnet.exe run --project tests/UiVerification/UiVerification.csproj -c Release -- artifacts/diagnostics/monitor-after-restart-20261002-171300.json <new evidence directory>`. Set `DOTNET_CLI_HOME` to `.tools/cli-home` and `NUGET_PACKAGES` to `.tools/nuget-packages`. Do not run hardware control or sleep diagnostics without the user's explicit instruction.

Distribution commands: `scripts/Publish-Product.ps1` for self-contained and framework-dependent Windows x64 directories, then `scripts/Package-Release.ps1 -Version <VERSION> -SelfContainedDirectory <directory> -FrameworkDependentDirectory <directory>`. Deliver both ZIP files and `SHA256SUMS.txt`. Product builds need local signed `LpcIO.bin` and compiled PawnIO modules; source alone is insufficient.

Remote: `origin` is `https://github.com/t66sun/myFanControl.git`; target branch is `master`. Do not create GitHub Issues or contact maintainers unless the user explicitly asks. Local commits and deliveries do not grant permission to push, tag, or publish.

At task start, inspect branch, HEAD, staged/unstaged changes, and untracked files. Preserve prior user work. Verify applicable changes and commit only task-owned paths. For an authorized product release, package the committed content in `release/v<version>/` with `release.json` (`pending` before packaging, `complete` only after validation), `CHANGELOG.md`, both product ZIPs, and checksums. Record each deliverable's SHA-256 in `release.json`; keep only the latest successful local product version, as directed in `HANDOFF.md`. Do not create a Git tag for local delivery. If verification or packaging fails, keep the work and pending record, and report the failure without claiming delivery.
<!-- project-governance:end -->
