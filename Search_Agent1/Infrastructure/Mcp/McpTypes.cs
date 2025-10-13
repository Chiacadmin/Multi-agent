using System.Text.Json.Serialization;

namespace Infrastructure.Mcp
{
    public class McpToolDescriptor
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("input_schema")] public object? InputSchema { get; set; }
    }

    // JSON-RPC base
    public class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonPropertyName("method")] public string Method { get; set; } = "";
        [JsonPropertyName("params")] public object? Params { get; set; }
    }

    public class JsonRpcResponse<T>
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("result")] public T? Result { get; set; }
        [JsonPropertyName("error")] public JsonRpcError? Error { get; set; }
    }

    public class JsonRpcError
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
    }

    // MCP specific shapes
    public class ToolsListResult
    {
        [JsonPropertyName("tools")] public List<McpToolDescriptor> Tools { get; set; } = new();
    }

    public class ToolCallParams
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("arguments")] public object Arguments { get; set; } = new { };
    }

    public class ToolCallEnvelope
    {
        [JsonPropertyName("content")] public List<McpContent> Content { get; set; } = new();
    }

    public class McpContent
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "json";
        [JsonPropertyName("json")] public object? Json { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
