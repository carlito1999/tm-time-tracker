using Dapper;

namespace TmTimeTracker.Data;

public sealed record SlackChannelMapping(
    string ProjectKey, string ChannelId, string ChannelName, string MessageTemplate);

public sealed class SlackChannelRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public SlackChannelRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Upsert(SlackChannelMapping mapping)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO slack_channel (project_key, channel_id, channel_name, message_template)
              VALUES (@pk, @cid, @cname, @tpl)
              ON CONFLICT(project_key) DO UPDATE SET
                channel_id=excluded.channel_id,
                channel_name=excluded.channel_name,
                message_template=excluded.message_template",
            new
            {
                pk = mapping.ProjectKey,
                cid = mapping.ChannelId,
                cname = mapping.ChannelName,
                tpl = mapping.MessageTemplate
            });
    }

    public SlackChannelMapping? Find(string projectKey)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<SlackChannelRow>(
            "SELECT project_key, channel_id, channel_name, message_template FROM slack_channel WHERE project_key = @pk",
            new { pk = projectKey });
        return row is null ? null : Map(row);
    }

    public IReadOnlyList<SlackChannelMapping> GetAll()
    {
        using var conn = _factory.Open();
        return conn.Query<SlackChannelRow>(
            "SELECT project_key, channel_id, channel_name, message_template FROM slack_channel ORDER BY project_key")
            .Select(Map).ToList();
    }

    public void Remove(string projectKey)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM slack_channel WHERE project_key = @pk", new { pk = projectKey });
    }

    private static SlackChannelMapping Map(SlackChannelRow r) =>
        new(r.project_key, r.channel_id, r.channel_name, r.message_template);

    private sealed class SlackChannelRow
    {
        public string project_key { get; set; } = "";
        public string channel_id { get; set; } = "";
        public string channel_name { get; set; } = "";
        public string message_template { get; set; } = "";
    }
}
