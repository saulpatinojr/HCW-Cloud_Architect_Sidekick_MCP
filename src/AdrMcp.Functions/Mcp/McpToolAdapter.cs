using AdrMcp.Application.Tools;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;

namespace AdrMcp.Functions.Mcp;

/// <summary>
/// Bridges the Azure Functions MCP trigger to the transport-agnostic pipeline: extracts arguments, HTTP headers
/// (available through HttpTransport when the extension forwards them) and the session id.
/// </summary>
public sealed class McpToolAdapter
{
    private readonly ToolInvocationPipeline _pipeline;
    public McpToolAdapter(ToolInvocationPipeline pipeline) => _pipeline = pipeline;

    public Task<string> RunAsync<TReq>(ToolInvocationContext context, Func<ToolCall<TReq>, CancellationToken, Task<object>> handler, CancellationToken ct) where TReq : class, IToolRequest
        => _pipeline.ExecuteAsync(context.Name, ToArguments(context), ExtractHeaders(context), context.SessionId, handler, ct);

    public static IReadOnlyDictionary<string, object?> ToArguments(ToolInvocationContext context)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (context.Arguments is null) return dict;
        foreach (var kv in context.Arguments) dict[kv.Key] = kv.Value;
        return dict;
    }

    public static IReadOnlyDictionary<string, string> ExtractHeaders(ToolInvocationContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.Transport is HttpTransport http && http.Headers is not null)
        {
            foreach (var kv in http.Headers)
            {
                var value = kv.Value?.ToString();
                if (value is not null) headers[kv.Key] = value;
            }
        }
        return headers;
    }
}
