# Porting the eM Client 11 pipeline to 11.0.1267.0 (stable)

**Status:** ported, every patch verified against the real 11.0.1267 assemblies on x86 and x64,
full `deploy.sh` run green on both. Not yet confirmed by a live GUI launch/click-through — this
environment has no eM Client 11 bottle (and no CrossOver bottle with a display attached), so the
final "does it run and behave" check is left for the user on a real bottle.

**What changed, in one line:** a new `releases/11.0.1267/` folder forked from the beta line, plus
one real x64 bug fixed in Stage 7's `MailClient.deps.json` patching. No `il-patcher` patch logic
needed any adaptation.

## Getting the reference snapshots

eM Client 11 ships as an MSIX bundle, and the stable feed now resolves to this build:

```bash
curl -sSL https://licensing.emclient.com/api/update/emclient.appinstaller
# <AppInstaller ...><MainBundle ... Version="11.0.1267.0"
#   Uri="https://cdn-dist.emclient.com/dist/v11.0.1267/setup.msixbundle" />
```

The bundle is ~837 MB and contains `MailClient_win-{x86,x64,arm64}.msix`. Extracting the x86 and
x64 entries into `original/em-11.0.1267/` and `original/em-11.0.1267-x64/` gave:

| Snapshot | Files | MailClient.dll |
|---|---|---|
| `original/em-11.0.1267/` (x86) | 2549 | 32,756,224 bytes, `FileVersion=11.0.1267.0`, `InformationalVersion=11.0.1267+ca9fa42242` |
| `original/em-11.0.1267-x64/` (x64) | 2552 | same FileVersion/InformationalVersion |

Both are flat layouts (`MailClient.exe` at the archive root), same as every earlier snapshot, so
`install-msix.sh`'s extraction logic needed no change. `MailClient.runtimeconfig.json` now names
`Microsoft.NETCore.App`/`Microsoft.WindowsDesktop.App` `10.0.13`; the stable `install-msix.sh`
therefore pins the 10.0.13 desktop-runtime installers for its font-writer helper instead of the
beta script's 10.0.11. The app itself is self-contained (it ships `coreclr.dll`, `hostfxr.dll`,
etc.), so this pin only matters for the helper tool, not for eM Client.

## Patch compatibility

Every `il-patcher` flag used by the beta deploy script was run against the x86 snapshot, in the
same order the script chains them. All 24 applied cleanly with no code change:

| Deploy stage (patch #) | Flags | Result on 11.0.1267 |
|---|---|---|
| 1 (patch 3) | `--patch-pbkdf2-instance-api` | 3 call sites (2 in `MailClient.dll`, 1 in `MailClient.Abstractions.dll`) |
| 2 (patch 1) | `--patch-splash-tip-icon` | resource edit at `0x1A32`, same `labelTip.Text` |
| 3 (patch 4) | the 14-flag notification chain | all 14 |
| 4 (patch 2) | `--patch-preview-pane-periodic-repaint` | added |
| 6 (patch 5) | `--patch-account-manager-sync-async`, `--patch-folder-sync-async` | both |
| 7 (patch 5) | `--patch-http-dns-connect-callback`, `--patch-token-refresh-retry` | both |
| 8 (patch 6) | `--patch-nested-popup-owned-by-ancestor`, `--patch-colordialog-suppress-autoclose` | both |

This is consistent with what the beta line already saw between 11.0.196 and 11.0.282: the touched
types/methods (`AESEncryptor`, `FileBasedCache<T>`, `FormSplashScreen` resources,
`FormGenericNotification`, `FormMailNotification`, `ControlMessageDetail`, `AccountManager`,
`Folder`, `InteractionController`, `Credentials`, `PopupWindowHelper`, `CommonUIUtils`,
`ControlToolStripButton*`) still have the same shape in the stable build. The patches auto-detect
their targets by exact signature, so a real structural change would have failed loudly here — and
didn't.

## Verification runs

Two throwaway bottles were used, both a trimmed copy of the new snapshot (all `MailClient*`/
`System*`/WinApi assemblies and the deps/runtimeconfig, with native CEF payload and third-party
assemblies removed purely to fit the test disk). Both were run through a real `deploy.sh` (its
`/tmp` workdir pointed at a disk-backed directory for the test — this machine's `/tmp` is a
7.3 GB tmpfs and the stage chain legitimately needs more space than that):

- **x86**, `--patches 1,2,3,4,5,6` — all stages applied, `all verification checks passed`, deployed,
  stage mask **239** (`1|2|4|8|32|64|128`).
- **x86 re-run** — read the marker back as mask 239, reported "already has every requested stage",
  skipped the pipeline (idempotent path).
- **x64**, `--patches 1,2,3,4,5,6` — same result, mask 239, all verification checks passed.

The verification block itself is unchanged from the beta script: `ilspycmd` decompile greps for
each fix's marker members/strings, `--dump-handlers` on every method whose exception-handler table
was touched, the Stage 2 byte-size/`c2a0c2a0` resource check, and a deps.json entry check.

## Disk-space bug found while testing the port (fixed in the stable script)

The first real run against a full-size bottle died partway through the notification chain with:

```
Unhandled exception. System.IO.IOException: Disk quota exceeded
   at System.IO.FileSystem.CopyFile(...)
   at Program...RunPatchNotificationPeriodicReblit
```

Two separate problems compounded:

1. **Every intermediate stage directory was retained until the end.** The 14-step notification
   chain writes `output-stage3a` … `output-stage3m` and `output-final`, and the old script kept
   all of them alongside Stage 1/2 and the pristine copy. At ~543 MB per DLL-only stage output
   (full x86 install), that is roughly **8-9 GB** of live files, not the "two directories at a
   time" the script's comments implied.
2. **The workdir was hardcoded under `/tmp`.** On this machine `/tmp` is a 7.3 GB **tmpfs**
   (RAM-backed). Filling it does not just produce a clean ENOSPC: it consumes system memory and
   can make the whole desktop unresponsive/thrash until reboot, which is exactly what the user
   reported.

The stable script now:

- calls `prune_stage_output` after each stage has consumed the previous output, so only the
  pristine copy, Stage 1, Stage 2 (both still needed by verification), the current stage output,
  and the next one exist at once — measured peak for a trimmed 149 MB test bottle was **930 MB**,
  extrapolating to roughly **3-4 GB** for a full 661 MB install;
- picks the workdir from `${TMPDIR:-/tmp}` then `$HOME/.cache/em-client-wine-port`, requiring
  ~5 GB free and **2× that (10 GB) on a tmpfs**, so it falls back to the disk-backed cache
  directory on normal `/tmp`-is-tmpfs systems;
- accepts `EMCLIENT_DEPLOY_WORKDIR=/path` as an explicit override;
- prints the exact `rm -rf` command for a workdir left behind on failure.

A full `--patches 1,2,3,4,5,6` run against a pristine test bottle after these changes reached
mask 239 with all verification checks passing, and the workdir was created under
`$HOME/.cache/em-client-wine-port` (never `/tmp`). If an old run left a workdir behind, reclaim it
with `rm -rf /tmp/emclient11-deploy-*`.

## The one real port bug: x64 deps.json target

Stage 7 makes `MailClient.Wine.dll` a genuinely loaded dependency and therefore edits
`MailClient.deps.json`. The beta script passed a hardcoded target key:

```bash
".NETCoreApp,Version=v10.0/win-x86"
```

That is correct for the x86 package, but the x64 package's deps.json uses
`".NETCoreApp,Version=v10.0/win-x64"`. The beta verification only grepped for the string anywhere
in the file, so it passed while the entry sat under a target the x64 runtime never consults — the
app would load `MailClient.dll`, hit a `DnsConnectHelper`/`TokenRefreshRetryHelper` reference, and
fail to resolve `MailClient.Wine` at runtime. (Same failure class as the wrong-version-key bug the
beta line already documented, along a different axis — which is why the fix both reads the target
from `runtimeTarget.name` and verifies the entry is under that exact target.)

`releases/11.0.1267/deploy.sh` now does:

```bash
DEPS_TARGET_KEY=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['runtimeTarget']['name'])" \
    "$FINAL_DIR/MailClient.deps.json")
python3 .../license-oaep-patcher-patch-deps-json.py \
    "$FINAL_DIR/MailClient.deps.json" "MailClient.Wine" "$RELEASE_VERSION.0" "$DEPS_TARGET_KEY"
```

and the verification step checks the key's presence under that target specifically. Confirmed on
both test bottles: `win-x86` and `win-x64` deps.json each got the entry under their own target.

## Packaging changes

- `releases/11.0.1267/` — copy of `releases/11.0.196-beta/`, with:
  - `OUR_RELEASE_NUMBER_FOR_VERSION=( ["11.0.1267"]=1 )`; the beta builds are deliberately rejected
    by this folder's version gate (the beta folder still supports them).
  - `install-msix.sh` `APPINSTALLER_URL` without `?beta=true` (stable feed), .NET runtime pinned to
    10.0.13, snapshot file counts updated to 2549/2552.
  - header/comments updated from beta to the stable line.
  - the x64 deps.json fix above.
- `releases/11.0.196-beta/` is untouched, as a frozen reference for 11.0.196/11.0.282.
- No change to `il-patches/il-patcher-Program.cs`, the patch grouping (`PATCH_STAGES`), the stage
  bitmask, the version marker, fonts, file associations, or Bottles support.

## What was not tested here

- A live launch of the patched build under CrossOver. The test bottles above were deliberately
  trimmed for disk space and never run; no eM Client 11 profile exists on this machine. The
  practical checks still worth doing on a real bottle: app starts, syncs, notification toast
  paints, preview pane recovers, and the font/color popup fixes behave as before — the same
  click-through list used for the beta releases.
- `install-msix.sh` start-to-finish into a real bottle. Its two changed inputs were checked
  directly: the stable feed returns 11.0.1267.0, and both 10.0.13 runtime installer URLs return
  HTTP 200. The rest of the script is unchanged from the beta line.
