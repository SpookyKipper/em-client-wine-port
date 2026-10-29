# eM Client CrossOver Stabilization

IL-level binary patches (applied via [Mono.Cecil](https://github.com/jbevain/cecil), no source
access needed) that fix real bugs in [eM Client](https://www.emclient.com/) 10.4.5674 and 11.0.1267
when it runs under [CrossOver](https://www.codeweavers.com/crossover)/Wine on Linux — not a native
Linux port, no source changes to eM Client itself, just targeted binary patches for the specific
things Wine gets wrong.

## Quick start

Already have eM Client installed in a CrossOver bottle? Patch it:

```bash
./releases/10.4.5674/deploy.sh      # eM Client 10.4.5674
./releases/11.0.1267/deploy.sh      # eM Client 11.0.1267 (stable)
```

That's it. It finds your bottle(s), checks the installed version matches, backs up the current
files, rebuilds and re-verifies every patch fresh against your exact install, deploys, and rolls
back cleanly if anything goes wrong. Safe to run more than once — it detects an already-patched
install and does nothing.

For eM Client 11, run `./releases/11.0.1267/install-msix.sh` first if it isn't installed yet — see
[eM Client 11](#em-client-11) below.

```bash
./releases/10.4.5674/deploy.sh --list          # just show what bottles/versions it finds
./releases/10.4.5674/deploy.sh --bottle NAME   # target a specific bottle non-interactively
./releases/10.4.5674/deploy.sh --help          # full usage
```

Needs the [.NET SDK](https://dotnet.microsoft.com/download) and `python3` — the script checks
for both and tells you how to install whichever is missing.

## `releases/10.4.5674/deploy.sh` flags

| Flag | Description |
|---|---|
| *(none)* | Interactive: lists bottles found, prompts for choice, confirms before deploying |
| `--bottle NAME` | Skip bottle selection — target a specific bottle non-interactively |
| `--list` | List found bottles and their installed versions, then exit (patches nothing) |
| `-y`, `--yes` | Skip the version-mismatch confirmation; also defaults the optional patch (patch 5) to **not** included — use `--patches` to include it non-interactively instead |
| `--force` | Skip the "already patched, nothing to do" short-circuit and re-apply anyway |
| `--max-revision N` | Cap the *mandatory* patches at patch N (0–4) instead of the latest. Doesn't touch optional patch 5 — use `--patches` for that. Mutually exclusive with `--patches` |
| `--patches 1,3,5` | Apply exactly these patch numbers (mandatory or optional, any combination), bypassing the "mandatory patches apply together" rule — the only non-interactive way to add the optional patch. Only ever adds patches, never removes one already applied. Mutually exclusive with `--max-revision` |
| `--install-fonts` | Install the vendored `fonts/` without the license-consent prompt |
| `--no-fonts` | Skip font installation without the license-consent prompt |
| `--install-associations` | Add file-type associations (see `file-associations/` below) without the prompt |
| `--no-associations` | Skip file-type associations without the prompt |
| `--force-associations` | Also overwrite extensions that already have some association set |
| `--wine-manager crossover\|bottles` | Which Wine-prefix manager the target bottle lives under — [CrossOver](https://www.codeweavers.com/crossover) or [Bottles](https://usebottles.com/). Auto-detected when only one is installed; prompted for otherwise (required under `-y` in that case) |
| `-h`, `--help` | Full usage |

## eM Client 10

`releases/10.4.5674/deploy.sh` groups its fixes into 6 numbered **patches** — patch 5 is
optional, patches 1–4 and 6 are applied by default. A patch is a curated bundle of one or more
related fixes (e.g. patch 1 bundles every "tofu box" icon/font glyph fix together); the table
below shows which bugs each patch covers. Use `--patches N` to apply one targeted patch on its own
(e.g. `--patches 5` to add just the Exchange sync-freeze fix to an already-patched bottle), or
`--patches 1,3` for several at once — see `./deploy.sh --help`.

| Patch # | Bug | Root cause | Report |
|---|---|---|---|
| 1 | Splash screen banner rendered wrong, Settings icon resizing looked wrong | Wine's `gdiplus` doesn't implement `InterpolationMode.HighQualityBicubic` | [gdiplus-interpolation-findings.md](reports/gdiplus-interpolation-findings.md) |
| 1 | License dialog's "Get a license" button showed two tofu boxes | Corrupted control characters baked into eM Client's own resource data (not a Wine bug) | [license-icon-findings.md](reports/license-icon-findings.md) |
| 1 | Splash screen tip line showed two tofu boxes | A real emoji character Wine has no glyph for (a genuine Wine gap, investigated at length) | [splash-tip-icon-findings.md](reports/splash-tip-icon-findings.md) |
| 2 | Settings grid occasionally failed to paint | A Wine clip-region bug silently discards some paint calls | [settings-panel-clip-region-findings.md](reports/settings-panel-clip-region-findings.md) |
| 2 | Settings dialog's left category panel was completely blank | `formSettings`'s `Load` event never fires under Wine | [settings-panel-clip-region-findings.md](reports/settings-panel-clip-region-findings.md) |
| 2 | Settings crashed when clicking a category | Wine throws the wrong exception type from a Shell COM API eM Client already handles for the *expected* type | [default-mail-client-notimplemented-findings.md](reports/default-mail-client-notimplemented-findings.md) |
| 3 | License Activation silently failed (spinner, then nothing) | RSA-OAEP decrypt fails inside Wine's `bcrypt.dll`/GnuTLS backend | [license-activation-oaep-findings.md](reports/license-activation-oaep-findings.md) |
| 4 | New-mail notification toast didn't display correctly until it faded out — invisible text/icons, no hover-pause, unclickable icons | `this` form's own window doesn't reliably paint or receive input under Wine; content only ever appeared during the fade animation ticks | [notification-empty-until-fade-findings.md](reports/notification-empty-until-fade-findings.md) |
| **5 (optional)** | Severe, recurring multi-minute freezes during Exchange sync, especially while composing/replying | Wine can't honor async DNS lookup cancellation, so two fully-synchronous, UI-thread-reachable sync entry points could each block for a 20s completion-port fallback | [exchange-sync-freeze-findings.md](reports/exchange-sync-freeze-findings.md) |
| 6 | Font-family dropdown (and other nested combo dropdowns) rendered underneath its own parent popup | Wine never establishes a real native window-owner relationship the app's popup code asks for; fixed by asserting it explicitly instead | [floating-popup-zorder-findings.md](reports/floating-popup-zorder-findings.md) |
| 6 | Opening the standard color picker from inside a floating popup closed the popup instead of leaving it open behind the dialog | A global WinForms filter auto-closes any popup it doesn't recognize as owning the newly-active window — same root cause as the dropdown bug above | [floating-popup-zorder-findings.md](reports/floating-popup-zorder-findings.md) |
| — (`--install-associations`) | Docx attachments (and other extensions) wouldn't open — "no Windows program configured" | Fresh CrossOver bottles ship no file-type association for most attachment extensions (not an eM Client bug) | [office-file-associations-findings.md](reports/office-file-associations-findings.md) |

All confirmed fixed and working, tested across Windows 7/8/10/11 CrossOver bottles. Patch 5 is
opt-in because the same underlying fix was found to behave differently across machines in
testing; file-type associations are a separate feature entirely, controlled by
`--install-associations`, not part of the patch numbering.

## eM Client 11

eM Client 11 is stable (as of October 2026) and is a separate product build from 10.4.5674 — a new
assembly set targeting .NET 10, tracked completely independently from the 10.4.5674 pipeline
above: its own bottle, its own `releases/11.0.1267/` folder, its own findings reports. See
`CLAUDE.md`'s "eM Client 11 — separate release line" section for the full breakdown of what's
shared (the patcher tool) versus kept separate (everything else).

`releases/11.0.1267/` is the stable release line, verified against both `11.0.1267.0` and
`11.0.1389.0` (the current stable build) on x86 and x64. Every patch applies unmodified and every
verification and handler check passes. Three fixes followed the initial port: Stage 7's
`MailClient.deps.json` target key is read from the file instead of hardcoded to `win-x86` (x64
installs otherwise got the helper entry under the wrong runtime target), **patch 7** replaces
eM Client 11's native license crypto with BouncyCastle so activation works under Wine, and
**patch 8** fills in optional Matrix room sections that some homeservers (e.g. Beeper) omit, which
eM Client's model otherwise rejects as missing required properties. See
[emclient11-1267-stable-port-findings.md](reports/emclient11-1267-stable-port-findings.md),
[emclient11-license-ecdsa-findings.md](reports/emclient11-license-ecdsa-findings.md), and
[emclient11-matrix-beeper-sync-findings.md](reports/emclient11-matrix-beeper-sync-findings.md). The older
beta line (`releases/11.0.196-beta/`, covering 11.0.196 and 11.0.282) is kept as a frozen
historical reference; use `releases/11.0.1267/` for current installs.

### `releases/11.0.1267/install-msix.sh` flags

eM Client 11 ships as an MSIX package, which CrossOver/Wine can't install directly — run this
*first* to get a clean install in place (see below), then `deploy.sh` to patch it.

| Flag | Description |
|---|---|
| *(none)* | Interactive: offers to create a fresh bottle, then lists existing bottles and prompts for choice |
| `--bottle NAME` | Skip bottle selection — install into a specific existing bottle |
| `--new-bottle NAME` | Skip the create/reuse prompt: always create a new bottle named NAME (must not already exist) |
| `-y`, `--yes` | Don't prompt on the Windows-11-template check, or on whether to run `deploy.sh` afterward — answers yes to both |
| `--list` | List found bottles, then exit (installs nothing) |
| `--arch x86\|x64` | Which package to extract from the MSIX bundle (default: `x86`, matching every previously-tested install) |
| `--install-fonts` | Install the vendored `fonts/` without the license-consent prompt |
| `--no-fonts` | Skip font installation without the license-consent prompt |
| `--wine-manager crossover\|bottles` | Which Wine-prefix manager to install into — [CrossOver](https://www.codeweavers.com/crossover) or [Bottles](https://usebottles.com/). Auto-detected when only one is installed; prompted for otherwise (required under `-y` in that case). Under Bottles, `--new-bottle` creates one via `bottles-cli new` instead of `cxbottle --create` |
| `-h`, `--help` | Full usage |

### `releases/11.0.1267/deploy.sh` flags

Identical flag set to the 10.4.5674 script above, file-associations flags included — `--bottle`,
`--list`, `-y`/`--yes`, `--force`, `--max-revision N`, `--patches LIST`, `--install-fonts`,
`--no-fonts`, `--install-associations`, `--no-associations`, `--force-associations`,
`--wine-manager crossover|bottles`, `-h`/`--help` all work identically — though which bugs land
in which patch number is its own, independently-curated grouping (see the table below), not the
same numbering as the 10.4.5674 script's own patches. Patches 1–4, 6, 7, and 8 are mandatory;
patch 5 is optional.

| Patch # | Bug | Root cause | Report |
|---|---|---|---|
| 1 | Splash screen tip line showed two tofu boxes | Same bug as the 10.4.5674 fix above — confirmed identical, not just similar | [emclient11-splash-tip-icon-findings.md](reports/emclient11-splash-tip-icon-findings.md) |
| 2 | Message preview pane intermittently went completely blank | Wine stops delivering `WM_PAINT` to the CEF-hosted preview pane for an arbitrary length of time; exact trigger mechanism not pinned down, so this periodically forces a repaint instead | [emclient11-preview-pane-blank-findings.md](reports/emclient11-preview-pane-blank-findings.md) |
| 3 | App wouldn't start at all — crashed during its own background init | .NET 10's new PBKDF2 API always routes through Wine's `bcrypt.dll`, which throws on the exact call the app makes to derive its local-cache encryption key | [emclient11-pbkdf2-startup-crash-findings.md](reports/emclient11-pbkdf2-startup-crash-findings.md) |
| 4 | New-mail notification toast empty until fade | Same bug and same fix as the 10.4.5674 fix above; two of the seven sub-fixes needed real adaptation for structural changes in this build | [emclient11-notification-empty-until-fade-findings.md](reports/emclient11-notification-empty-until-fade-findings.md) |
| **5 (optional)** | Severe, recurring multi-minute freezes during Exchange sync | Same bug and same fix as the 10.4.5674 fix above — confirmed identical, applied completely unmodified, no adaptation needed | [exchange-sync-freeze-findings.md](reports/exchange-sync-freeze-findings.md) |
| **5 (optional)** | Freezes syncing Microsoft 365 / Graph API accounts specifically (a different bug from the classic IMAP/EWS freeze above) | Same underlying Wine DNS defect, hit through `HttpClient`'s own connection path instead, plus a second hang in socket send/receive | [emclient11-msgraph-sync-freeze-findings.md](reports/emclient11-msgraph-sync-freeze-findings.md) |
| 6 | Font-family dropdown (and other nested combo dropdowns) rendered underneath its own parent popup | Wine never establishes a real native window-owner relationship the app's popup code asks for; fixed by asserting it explicitly instead | [floating-popup-zorder-findings.md](reports/floating-popup-zorder-findings.md) |
| 6 | Opening the standard color picker from inside a floating popup closed the popup instead of leaving it open behind the dialog | A global WinForms filter auto-closes any popup it doesn't recognize as owning the newly-active window — same root cause as the dropdown bug above | [floating-popup-zorder-findings.md](reports/floating-popup-zorder-findings.md) |
| 7 | License activation silently failed — spinner, then nothing; the license never activated | Wine's CNG/bcrypt cannot import the license server's ECDSA P-256 key (`0x80090029`), so eM Client's v2 license signature check fails; older RSA-OAEP formats hit the same Wine defect fixed for eM Client 10. Both are replaced with pure-managed BouncyCastle crypto | [emclient11-license-ecdsa-findings.md](reports/emclient11-license-ecdsa-findings.md) |
| 8 | Matrix account showed a red warning and listed no chats, even though login/avatar/session worked | Beeper's homeserver omits optional `account_data`, `summary`, and `ephemeral` sections on many rooms; eM Client's `RoomInfo` marks them `required`, so `System.Text.Json` rejected the whole `/sync` response. Missing sections are filled with their spec-defined empty shape before deserialization | [emclient11-matrix-beeper-sync-findings.md](reports/emclient11-matrix-beeper-sync-findings.md) |

Patch 5 bundles both sync-freeze fixes together — they're decided and applied as one unit
(`--patches 5` includes both; there's no way to request just one).

Unlike the 10.4.5674 pipeline, eM Client 11 ships as an MSIX package, which CrossOver/Wine can't
install directly — there's no classic installer to run inside the bottle. Get a clean install in
place first with `releases/11.0.1267/install-msix.sh` (downloads the .NET 10 desktop runtime,
resolves and downloads the current MSIX bundle, extracts it into the bottle by hand, and installs
a set of ICU DLLs Wine doesn't provide but the app's spell-checker needs — without them, the app
crashes on essentially every keystroke in a compose window; see
[emclient11-icu-spellcheck-crash-findings.md](reports/emclient11-icu-spellcheck-crash-findings.md)
— downloaded fresh each run, not vendored here, same as the .NET runtime above), then run
`releases/11.0.1267/deploy.sh` (which `install-msix.sh` will offer to do for you) to apply the
patches above. The ICU fix is a missing OS component, not an eM Client bug, so it lives in
`install-msix.sh` rather than as one of the patches in the table.

After a CrossOver install, launch eM Client from your desktop application menu (search for
**eM Client**) or run `~/bin/mailclient`. The installer registers the shortcut with `cxmenu`; if
CrossOver's own window was already open when the bottle was created, restart it once so the new
bottle appears there too.

`deploy.sh` builds its stages in a temporary workdir and needs roughly 5 GB free there. It picks
a directory automatically — `${TMPDIR:-/tmp}` first, then `$HOME/.cache/em-client-wine-port` — and
deliberately avoids a RAM-backed `/tmp` (filling a tmpfs can freeze the desktop, not just fail the
patch). Override with `EMCLIENT_DEPLOY_WORKDIR=/path ./releases/11.0.1267/deploy.sh ...` if
needed.

## How it's built

```
original/em-<version>/   Untouched eM Client install output, one subfolder per eM Client version
                          tested against (confirmed byte-identical regardless of target Windows
                          version, so one snapshot per eM Client build is enough). Not tracked in
                          git — drop a fresh install here.

il-patches/               The Cecil-based patcher tools (source tracked, binaries built fresh
                           each run) plus every patch's own doc comments explaining what it does
                           and why.

file-associations/        .reg files fixing attachment types (office documents, images,
                           archives, audio/video) a fresh CrossOver bottle has no file-type
                           association for. deploy.sh imports only the extensions actually
                           missing one on your bottle.

releases/<version>/       deploy.sh — the thing you actually run (see Quick start). Versioned so
                           a new eM Client release gets its own folder rather than editing this
                           one in place.

reports/                  One findings doc per bug: root cause, the fix, and — importantly —
                           any wrong turns taken first and why, so they don't get re-explored.

supporting/                Before/after screenshots for the visual bugs.

fonts/                    Genuine Microsoft fonts (Segoe UI, Tahoma, Calibri) the app expects
                           but Wine doesn't ship — see "A note on fonts/" below.

CLAUDE.md                 The full technical deep-dive: exact patch pipeline commands, the
                           investigation method that found each bug, and hard-won lessons about
                           doing this kind of IL patching correctly. Written for AI-assisted
                           continuation (this project was built working with Claude Code) but
                           equally useful as human documentation of exactly how everything works.
```

Every patch is a small, targeted edit — flip a constant, add a missing exception handler, redirect
one call to a different implementation, fix a couple of bytes in an embedded resource — never a
full reimplementation. `CLAUDE.md` documents the exact IL-level mechanics and the mistakes made
(and fixed) along the way, if you want to go deeper than the summary table above.

## Extending this for a new eM Client version

1. Drop the new version's install output into `original/<new-version>/`.
2. Copy `releases/10.4.5674/` to `releases/<new-version>/`.
3. Retest each patch by hand against the new build — don't assume it still applies; see
   `CLAUDE.md`'s "Investigation method" for how each bug was originally found and confirmed.
4. Fix up whatever broke in the new folder's `deploy.sh`. Each version's script stays a frozen,
   working reference for that exact build.

## A note on `fonts/`

The font files vendored in this repo (Segoe UI, Segoe UI Emoji, Tahoma, Calibri) are genuine
Microsoft fonts, not open-licensed — they're here because whoever added them holds a valid
Windows font license, not because they're freely redistributable. They're used to work around
Wine's default font substitutes (see `reports/splash-tip-icon-findings.md` for what that
investigation did and didn't fix) and are installed into a bottle only on explicit opt-in
(`deploy.sh --install-fonts`, which asks first). If you're forking or reusing this repo, make
your own call on whether you're entitled to redistribute them — don't assume this repo having
them means you are.

## License

The patching tools and scripts in this repo (`il-patches/`, `releases/`) are original work with
no license file attached yet — ask if you want to reuse them and it'll get sorted out. eM Client
itself is proprietary and not included here (`original/` is gitignored); this repo only ever
ships small binary *diffs* against an install you provide yourself. See "A note on `fonts/`"
above for the vendored font files specifically.
