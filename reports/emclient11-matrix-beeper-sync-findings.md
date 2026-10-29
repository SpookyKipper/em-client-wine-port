# eM Client 11 Matrix account: red warning, no chats (Beeper `/sync` omits optional room sections)

**Status:** root-caused and fixed as **patch 8 / Stage 10**. Verified against the affected
account's real `/sync` response: the patched `MatrixClient` deserializes it and reports
**149 joined rooms**; the unpatched one throws before any room is processed.

## Symptom

A Matrix account (Beeper, `https://matrix.beeper.com/`) completes login: the avatar is fetched,
`whoami` succeeds, and the session appears as eM Client in Element's session list. But eM Client
shows a red warning/offline state and lists no chats. The account configuration's `SyncToken` is
`null`, i.e. the initial Matrix sync never completed.

## Root cause

Reproduced by feeding the account's real `/sync?timeout=0` response to
`MailClient.Protocols.Matrix.MatrixClient.GetSyncResponse` under Wine. It throws immediately:

```
System.Text.Json.JsonException: JSON deserialization for type
'MailClient.Protocols.Matrix.RoomInfo' was missing required properties including:
'account_data', 'summary'.
  at System.Text.Json.ThrowHelper.ThrowJsonException_JsonRequiredPropertyMissing
  ...
  at MailClient.Protocols.Matrix.MatrixClient.Deserialize[T](...)
  at MailClient.Protocols.Matrix.MatrixClient.GetSyncResponse(...)
```

`RoomInfo` marks every room section as C# `required`:

```csharp
public required TimelineEvents Timeline { get; set; }
public required MatrixEvents State { get; set; }
public required MatrixEvents AccountData { get; set; }
public required MatrixEvents Ephemeral { get; set; }
public required UnreadNotifications UnreadNotifications { get; set; }
public required JsonElement Summary { get; set; }
```

`MatrixEvents.Events` is itself `required`. The source-generated `System.Text.Json` metadata
therefore rejects the entire response if any one room omits any one section. The Matrix spec treats
all of these as optional, and Beeper's homeserver omits them on a large fraction of rooms. Counted
on the real response (149 joined rooms):

| Missing section | Rooms |
|---|---|
| `account_data` | 30 |
| `summary` | 29 |
| `ephemeral` | 6 |

This is not a Wine bug: native Windows eM Client would hit the same `JsonException` against this
server. It is fixed here because it is a real blocked account in this project's target setup.

## Fix

`il-patches/MailClient.Wine/MatrixJsonDefaults.cs` pre-processes only `SyncResponse` JSON before
the existing deserializer sees it. For each room under `rooms.join`, `rooms.leave`, and
`rooms.invite`, any missing optional section is filled with its spec-defined empty shape
(`{"events":[]}` for state/timeline/account_data/ephemeral/invite_state, `{}` for summary,
zeroed `unread_notifications`). Other response types pass through untouched, and any parse failure
falls back to the original string.

`il-patches/matrix-sync-json-patcher-Program.cs` inserts one call at the start of
`MatrixClient.Deserialize<T>(string, JsonTypeInfo<T>)`:

```csharp
json = MatrixJsonDefaults.EnsureSyncRequiredFields<T>(json);
```

Stage 10 rebuilds `MailClient.Wine.dll` from its full tracked source (version marker,
DnsConnectHelper, TokenRefreshRetryHelper, MatrixJsonDefaults), because that already-deployed
helper is where the new method lives, and (re)asserts its `MailClient.deps.json` entry.

## Verification

- Real `/sync` response, patched `MatrixClient` loaded from the deployed app, under Wine:
  `OK nextLen=111 ... Join=149`. The unpatched assembly throws the `JsonException` above on the
  same response.
- Full pipeline run on pristine `11.0.1389` x86 with `--patches 1,...,8`: Stage 10 applied, all
  verification checks passed, stage mask **1007**.
- Live deployment to the affected bottle: stage mask 1007, deployed
  `MailClient.Protocols.Matrix.dll` decompiles to the inserted
  `MatrixJsonDefaults.EnsureSyncRequiredFields<T>` call, and the deployed helper contains the
  `MatrixJsonDefaults` type.
- Idempotency: the license and Matrix patchers now detect an already-patched input and skip
  (needed after a marker/file drift observed during testing), rather than failing.

## Files / release

- New patch 8 (`STAGE_BIT[10]=512`), mandatory; `PIPELINE_LATEST_PATCH=8`.
- New `il-patches/MailClient.Wine/MatrixJsonDefaults.cs` and
  `il-patches/matrix-sync-json-patcher-Program.cs`.
- `deploy.sh` rebuilds `MailClient.Wine` for this stage, adds
  `MailClient.Protocols.Matrix.dll` to the deployed/backup file list, and verifies the call, the
  helper type, handlers, and the deps entry.

## Remaining check

The deserialization blocker is fixed and proven with the real response. A live restart of eM
Client still needs to confirm the UI finishes initial sync and populates the chat list; later
Matrix stages (E2EE key processing, room timeline handling) were not exercised beyond the 149-room
sync parse.
