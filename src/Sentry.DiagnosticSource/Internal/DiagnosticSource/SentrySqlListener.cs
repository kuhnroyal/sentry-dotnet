using Sentry.Extensibility;
using Sentry.Internal.Extensions;

namespace Sentry.Internal.DiagnosticSource;

internal class SentrySqlListener : IObserver<KeyValuePair<string, object?>>
{
    private enum SentrySqlSpanType
    {
        Connection,
        Execution
    };

    internal const string ConnectionExtraKey = "db.connection_id";
    internal const string OperationExtraKey = "db.operation_id";

    internal const string SqlDataWriteConnectionOpenBeforeCommand = "System.Data.SqlClient.WriteConnectionOpenBefore";
    internal const string SqlMicrosoftWriteConnectionOpenBeforeCommand = "Microsoft.Data.SqlClient.WriteConnectionOpenBefore";

    internal const string SqlMicrosoftWriteConnectionOpenAfterCommand = "Microsoft.Data.SqlClient.WriteConnectionOpenAfter";
    internal const string SqlDataWriteConnectionOpenAfterCommand = "System.Data.SqlClient.WriteConnectionOpenAfter";

    internal const string SqlMicrosoftWriteConnectionCloseAfterCommand = "Microsoft.Data.SqlClient.WriteConnectionCloseAfter";
    internal const string SqlDataWriteConnectionCloseAfterCommand = "System.Data.SqlClient.WriteConnectionCloseAfter";

    internal const string SqlDataBeforeExecuteCommand = "System.Data.SqlClient.WriteCommandBefore";
    internal const string SqlMicrosoftBeforeExecuteCommand = "Microsoft.Data.SqlClient.WriteCommandBefore";

    internal const string SqlDataAfterExecuteCommand = "System.Data.SqlClient.WriteCommandAfter";
    internal const string SqlMicrosoftAfterExecuteCommand = "Microsoft.Data.SqlClient.WriteCommandAfter";

    internal const string SqlDataWriteCommandError = "System.Data.SqlClient.WriteCommandError";
    internal const string SqlMicrosoftWriteCommandError = "Microsoft.Data.SqlClient.WriteCommandError";

    private readonly IHub _hub;
    private readonly SentryOptions _options;

    public SentrySqlListener(IHub hub, SentryOptions options)
    {
        _hub = hub;
        _options = options;
    }

    private static void SetConnectionId(ISpan span, Guid? connectionId)
    {
        Debug.Assert(connectionId != Guid.Empty);

        span.SetExtra(ConnectionExtraKey, connectionId);
    }

    private static void SetOperationId(ISpan span, Guid? operationId)
    {
        Debug.Assert(operationId != Guid.Empty);

        span.SetExtra(OperationExtraKey, operationId);
    }

    private static Guid? TryGetOperationId(ISpan span) =>
        span.Extra.TryGetValue(OperationExtraKey, out var key) && key is Guid guid
            ? guid
            : null;

    private static Guid? TryGetConnectionId(ISpan span) =>
        span.Extra.TryGetValue(ConnectionExtraKey, out var key) && key is Guid guid
            ? guid
            : null;

    private void AddSpan(SentrySqlSpanType type, string operation, object? value)
    {
        var transaction = _hub.GetTransactionIfSampled();
        if (transaction == null)
        {
            return;
        }

        var operationId = value?.GetGuidProperty("OperationId");

        switch (type)
        {
            case SentrySqlSpanType.Connection when transaction.GetDbParentSpan().StartChild(operation) is
                { } connectionSpan:
                SetOperationId(connectionSpan, operationId);
                break;

            case SentrySqlSpanType.Execution when value?.GetGuidProperty("ConnectionId") is { } connectionId:
                var parent = TryGetConnectionSpan(transaction, connectionId) ?? transaction.GetDbParentSpan();
                var span = TryStartChild(parent, operation, null);
                if (span != null)
                {
                    SetOperationId(span, operationId);
                    SetConnectionId(span, connectionId);
                }

                break;
        }
    }

    private ISpan? GetSpan(SentrySqlSpanType type, KeyValuePair<string, object?> kvp)
    {
        var transaction = _hub.GetTransactionIfSampled();
        if (transaction == null)
        {
            return null;
        }

        switch (type)
        {
            case SentrySqlSpanType.Execution:
                var operationId = kvp.Value?.GetGuidProperty("OperationId");
                if (TryGetQuerySpan(transaction, operationId) is not { } querySpan)
                {
                    _options.LogWarning(
                        "Trying to get an execution span with operation id {0}, but it was not found.",
                        operationId);
                    return null;
                }

                if (querySpan.ParentSpanId == transaction.SpanId &&
                    TryGetConnectionId(querySpan) is { } spanConnectionId &&
                    querySpan is SpanTracer executionTracer &&
                    TryGetConnectionSpan(transaction, spanConnectionId) is { } spanConnectionRef)
                {
                    // Connection Span exist but wasn't set as the parent of the current Span.
                    executionTracer.ParentSpanId = spanConnectionRef.SpanId;
                }

                return querySpan;

            case SentrySqlSpanType.Connection:
                var connectionId = kvp.Value?.GetGuidProperty("ConnectionId");
                if (TryGetConnectionSpan(transaction, connectionId) is { } connectionSpan)
                {
                    return connectionSpan;
                }

                _options.LogWarning(
                    "Trying to get a connection span with connection id {0}, but it was not found.",
                    connectionId);
                return null;

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    private static ISpan? TryStartChild(ISpan? parent, string operation, string? description) =>
        parent?.StartChild(operation, description);

    private static ISpan? TryGetConnectionSpan(ITransaction transaction, Guid? connectionId) =>
        connectionId == null
            ? null
            : transaction.Spans
                .FirstOrDefault(span =>
                    span is {IsFinished: false, Operation: "db.connection"} &&
                    TryGetConnectionId(span) == connectionId);

    private static ISpan? TryGetQuerySpan(ITransaction transaction, Guid? operationId) =>
        operationId == null
            ? null
            : transaction.Spans.FirstOrDefault(span => TryGetOperationId(span) == operationId);

    private void UpdateConnectionSpan(object? value)
    {
        if (value == null)
        {
            return;
        }

        var transaction = _hub.GetTransactionIfSampled();
        if (transaction == null)
        {
            return;
        }

        var operationId = value.GetGuidProperty("OperationId");
        var connectionId = value.GetGuidProperty("ConnectionId");
        if (operationId == null || connectionId == null)
        {
            return;
        }

        var spans = transaction.Spans.Where(span => span.Operation is "db.connection").ToList();
        if (spans.Find(span => !span.IsFinished && TryGetOperationId(span) == operationId) is { } connectionSpan)
        {
            SetConnectionId(connectionSpan, connectionId);
        }
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void OnNext(KeyValuePair<string, object?> kvp)
    {
        try
        {
            switch (kvp.Key)
            {
                // Query
                case SqlMicrosoftBeforeExecuteCommand or SqlDataBeforeExecuteCommand:
                    AddSpan(SentrySqlSpanType.Execution, "db.query", kvp.Value);
                    return;
                case SqlMicrosoftAfterExecuteCommand or SqlDataAfterExecuteCommand
                    when GetSpan(SentrySqlSpanType.Execution, kvp) is { } commandSpan:
                    commandSpan.Description = kvp.Value?.GetProperty("Command")?.GetStringProperty("CommandText");
                    commandSpan.Finish(SpanStatus.Ok);
                    return;
                case SqlMicrosoftWriteCommandError or SqlDataWriteCommandError
                    when GetSpan(SentrySqlSpanType.Execution, kvp) is { } errorSpan:
                    errorSpan.Description = kvp.Value?.GetProperty("Command")?.GetStringProperty("CommandText");
                    errorSpan.Finish(SpanStatus.InternalError);
                    return;

                // Connection
                case SqlMicrosoftWriteConnectionOpenBeforeCommand or SqlDataWriteConnectionOpenBeforeCommand:
                    AddSpan(SentrySqlSpanType.Connection, "db.connection", kvp.Value);
                    return;
                case SqlMicrosoftWriteConnectionOpenAfterCommand or SqlDataWriteConnectionOpenAfterCommand:
                    UpdateConnectionSpan(kvp.Value);
                    return;
                case SqlMicrosoftWriteConnectionCloseAfterCommand or SqlDataWriteConnectionCloseAfterCommand
                    when GetSpan(SentrySqlSpanType.Connection, kvp) is { } closeSpan:
                    TrySetConnectionStatistics(closeSpan, kvp.Value);
                    closeSpan.Finish(SpanStatus.Ok);
                    return;
            }
        }
        catch (Exception ex)
        {
            _options.LogError("Failed to intercept SQL event.", ex);
        }
    }

    private static void TrySetConnectionStatistics(ISpan span, object? value)
    {
        if (value?.GetProperty("Statistics") is not Dictionary<object, object> statistics)
        {
            return;
        }

        if (statistics["SelectRows"] is long selectRows)
        {
            span.SetExtra("rows_sent", selectRows);
        }

        if (statistics["BytesReceived"] is long bytesReceived)
        {
            span.SetExtra("bytes_received", bytesReceived);
        }

        if (statistics["BytesSent"] is long bytesSent)
        {
            span.SetExtra("bytes_sent ", bytesSent);
        }
    }
}
