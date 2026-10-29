using System.Text.Json.Nodes;

namespace MailClient.Wine;

// Works around a server-compatibility bug in eM Client 11's Matrix sync deserializer.
//
// MailClient.Protocols.Matrix.RoomInfo marks timeline/state/account_data/ephemeral/
// unread_notifications/summary as C# `required` properties, and its source-generated
// System.Text.Json metadata rejects the whole /sync response if any one is absent
// ("JSON deserialization for type 'RoomInfo' was missing required properties including:
// 'account_data', 'summary'"). Beeper's Matrix homeserver (matrix.beeper.com) legitimately
// omits account_data and summary on many joined rooms -- a real /sync response captured from
// the affected account had 30/149 rooms without account_data, 29/149 without summary, and
// 6/149 without ephemeral. eM Client then fails the entire initial sync, shows the red
// warning/offline state, and never populates the chat list (SyncToken stays null).
//
// The Matrix spec treats all of those sections as optional. Rather than reimplement the
// deserializer, this pre-processes only SyncResponse JSON, filling in the spec-defined empty
// shape for any missing section before the existing source-generated deserializer sees it.
// Any other response type is returned untouched, and any parsing failure falls back to the
// original string so this can never make things worse.
public static class MatrixJsonDefaults
{
    private const string SyncResponseTypeName = "MailClient.Protocols.Matrix.SyncResponse";

    public static string EnsureSyncRequiredFields<T>(string json)
    {
        if (typeof(T).FullName != SyncResponseTypeName)
        {
            return json;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root ||
                root["rooms"] is not JsonObject rooms)
            {
                return json;
            }

            NormalizeRoomMap(rooms["join"] as JsonObject, RoomKind.Join);
            NormalizeRoomMap(rooms["leave"] as JsonObject, RoomKind.Leave);
            NormalizeRoomMap(rooms["invite"] as JsonObject, RoomKind.Invite);

            return root.ToJsonString();
        }
        catch
        {
            return json;
        }
    }

    private enum RoomKind
    {
        Join,
        Leave,
        Invite,
    }

    private static void NormalizeRoomMap(JsonObject? map, RoomKind kind)
    {
        if (map == null)
        {
            return;
        }

        foreach (var entry in map)
        {
            if (entry.Value is not JsonObject room)
            {
                continue;
            }

            switch (kind)
            {
                case RoomKind.Join:
                    EnsureEvents(room, "state");
                    EnsureEvents(room, "timeline");
                    EnsureEvents(room, "account_data");
                    EnsureEvents(room, "ephemeral");
                    EnsureObject(room, "summary");
                    EnsureObject(room, "unread_notifications");
                    if (room["unread_notifications"] is JsonObject unread)
                    {
                        unread["notification_count"] ??= 0;
                        unread["highlight_count"] ??= 0;
                    }
                    break;

                case RoomKind.Leave:
                    EnsureEvents(room, "state");
                    EnsureEvents(room, "timeline");
                    EnsureEvents(room, "account_data");
                    break;

                case RoomKind.Invite:
                    EnsureEvents(room, "invite_state");
                    break;
            }
        }
    }

    // `{ "events": [...] }` sections: missing means empty, and a present section without an
    // events array is equally unusable by MatrixEvents.Events (also `required`).
    private static void EnsureEvents(JsonObject room, string name)
    {
        if (room[name] is not JsonObject section)
        {
            room[name] = new JsonObject { ["events"] = new JsonArray() };
            return;
        }

        if (section["events"] is not JsonArray)
        {
            section["events"] = new JsonArray();
        }
    }

    private static void EnsureObject(JsonObject room, string name)
    {
        if (room[name] is not JsonObject)
        {
            room[name] = new JsonObject();
        }
    }
}
