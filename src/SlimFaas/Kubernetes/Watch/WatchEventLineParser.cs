using System.Text;
using System.Text.Json;

namespace SlimFaas.Kubernetes.Watch;

public enum WatchEventKind
{
    Change,
    Bookmark,
    Error,
    Unknown
}

public readonly record struct WatchEventInfo(
    WatchEventKind Kind,
    string? ResourceVersion,
    int? ErrorCode);

/// <summary>
/// Minimal, AOT-safe parser for a single line of a Kubernetes watch stream
/// (line-delimited JSON). Only extracts what the watch-as-signal design needs:
/// the event type, the object's resourceVersion (for stream continuity) and, for
/// ERROR events, the status code (to detect 410 Gone). Never throws: malformed
/// input yields <see cref="WatchEventKind.Unknown"/>.
/// </summary>
public static class WatchEventLineParser
{
    public static WatchEventInfo Parse(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return new WatchEventInfo(WatchEventKind.Unknown, null, null);
        }

        return Parse(Encoding.UTF8.GetBytes(jsonLine));
    }

    public static WatchEventInfo Parse(ReadOnlySpan<byte> jsonLine)
    {
        try
        {
            var reader = new Utf8JsonReader(jsonLine);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return new WatchEventInfo(WatchEventKind.Unknown, null, null);
            }

            string? type = null;
            string? resourceVersion = null;
            int? errorCode = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        type = reader.GetString();
                    }
                }
                else if (reader.ValueTextEquals("object"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        ReadObjectProperties(ref reader, ref resourceVersion, ref errorCode);
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            WatchEventKind kind = type switch
            {
                "ADDED" or "MODIFIED" or "DELETED" => WatchEventKind.Change,
                "BOOKMARK" => WatchEventKind.Bookmark,
                "ERROR" => WatchEventKind.Error,
                _ => WatchEventKind.Unknown
            };

            return new WatchEventInfo(kind, resourceVersion, errorCode);
        }
        catch (JsonException)
        {
            return new WatchEventInfo(WatchEventKind.Unknown, null, null);
        }
    }

    private static void ReadObjectProperties(
        ref Utf8JsonReader reader,
        ref string? resourceVersion,
        ref int? errorCode)
    {
        // reader is positioned on the StartObject of "object".
        int objectDepth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == objectDepth)
            {
                return;
            }

            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != objectDepth + 1)
            {
                continue;
            }

            if (reader.ValueTextEquals("metadata"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    int metadataDepth = reader.CurrentDepth;
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == metadataDepth)
                        {
                            break;
                        }

                        if (reader.TokenType == JsonTokenType.PropertyName &&
                            reader.CurrentDepth == metadataDepth + 1 &&
                            reader.ValueTextEquals("resourceVersion"u8))
                        {
                            reader.Read();
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                resourceVersion = reader.GetString();
                            }
                        }
                        else if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            reader.Read();
                            reader.Skip();
                        }
                    }
                }
                else
                {
                    reader.Skip();
                }
            }
            else if (reader.ValueTextEquals("code"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int code))
                {
                    errorCode = code;
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }
}
