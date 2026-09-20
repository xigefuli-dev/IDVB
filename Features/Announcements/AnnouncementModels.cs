using System.Text.Json.Serialization;

namespace IDVBuff.Features.Announcements;

public static class AnnouncementCategories
{
    public const string Update = "update";
    public const string Tips = "tips";
    public const string Notice = "notice";

    public static string GetDisplayName(string? category) => category switch
    {
        Update => "更新公告",
        Tips => "冷知识",
        Notice => "通知",
        _ => "公告"
    };
}

public sealed class AnnouncementItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = AnnouncementCategories.Notice;

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("coverImageUrl")]
    public string? CoverImageUrl { get; set; }

    [JsonPropertyName("authorName")]
    public string? AuthorName { get; set; }

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; }

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("minClientVersion")]
    public string? MinClientVersion { get; set; }

    [JsonPropertyName("publishAt")]
    public string PublishAt { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsRead { get; set; }

    [JsonIgnore]
    public bool IsDismissed { get; set; }
}

public sealed class AnnouncementResponse
{
    [JsonPropertyName("announcements")]
    public List<AnnouncementItem> Announcements { get; set; } = [];

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}

public sealed class AnnouncementLocalState
{
    [JsonPropertyName("readIds")]
    public HashSet<string> ReadIds { get; set; } = [];

    [JsonPropertyName("dismissedIds")]
    public HashSet<string> DismissedIds { get; set; } = [];

    [JsonPropertyName("lastCheckedAt")]
    public string? LastCheckedAt { get; set; }
}
