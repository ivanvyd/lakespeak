using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LakeSpeak.Configuration;

/// <summary>
/// A pointer to the last answer, so `export last` and `feedback last` mean something.
/// </summary>
/// <remarks>
/// Deliberately a pointer and nothing more. It records the identifiers needed to re-address a
/// message in Databricks — never the question, the answer, or a single row of the result.
/// Databricks already stores the conversation; duplicating its content into a file on disk would
/// create a second copy of governed data with none of the governance.
/// </remarks>
public sealed class RecentConversation
{
    public string? Profile { get; set; }

    /// <summary>
    /// Origin of the workspace that owns the conversation. This is routing context, not a
    /// credential; paths, queries, user information and tokens are never stored.
    /// </summary>
    public string? WorkspaceHost { get; set; }

    public string? AgentId { get; set; }

    public string? AgentTitle { get; set; }

    public string? ConversationId { get; set; }

    public string? MessageId { get; set; }

    /// <summary>Attachment holding the query result, when the answer had one.</summary>
    public string? AttachmentId { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public static string DefaultPath =>
        Path.Combine(Path.GetDirectoryName(LakeSpeakConfig.DefaultPath)!, "recent.yaml");

    public static RecentConversation? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<RecentConversation>(File.ReadAllText(path));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // A corrupt pointer file is not worth failing a command over; it is a convenience,
            // and the user can always pass explicit ids.
            return null;
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var yaml = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()
            .Serialize(this);

        File.WriteAllText(path, yaml);

        // Best-effort tightening on Unix. The file holds no secret, but it does reveal which
        // Agents and conversations this user has touched.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
            }
        }
    }
}
