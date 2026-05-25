namespace TmTimeTracker.Jira;

public interface IAccessTokenSource
{
    Task<(string AccessToken, string CloudId)> GetAccessTokenAsync(CancellationToken ct);
    Task ForceRefreshAsync(CancellationToken ct);
}
