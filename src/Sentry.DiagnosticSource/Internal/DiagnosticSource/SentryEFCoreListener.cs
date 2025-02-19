using Sentry.Extensibility;

namespace Sentry.Internal.DiagnosticSource;

/// <summary>
/// Class that consumes Entity Framework Core events.
/// </summary>
internal class SentryEFCoreListener : IObserver<KeyValuePair<string, object?>>
{
    private enum SentryEFSpanType
    {
        Connection,
        QueryExecution,
        QueryCompiler
    }

    internal const string EFConnectionOpening = "Microsoft.EntityFrameworkCore.Database.Connection.ConnectionOpening";
    internal const string EFConnectionClosed = "Microsoft.EntityFrameworkCore.Database.Connection.ConnectionClosed";
    internal const string EFCommandExecuting = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting";
    internal const string EFCommandExecuted = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted";
    internal const string EFCommandFailed = "Microsoft.EntityFrameworkCore.Database.Command.CommandError";

    /// <summary>
    /// Used for EF Core 2.X and 3.X.
    /// <seealso href="https://docs.microsoft.com/dotnet/api/microsoft.entityframeworkcore.diagnostics.coreeventid.querymodelcompiling?view=efcore-3.1"/>
    /// </summary>
    internal const string EFQueryStartCompiling = "Microsoft.EntityFrameworkCore.Query.QueryCompilationStarting";
    /// <summary>
    /// Used for EF Core 2.X and 3.X.
    /// <seealso href="https://docs.microsoft.com/dotnet/api/microsoft.entityframeworkcore.diagnostics.coreeventid.querymodelcompiling?view=efcore-3.1"/>
    /// </summary>
    internal const string EFQueryCompiling = "Microsoft.EntityFrameworkCore.Query.QueryModelCompiling";
    internal const string EFQueryCompiled = "Microsoft.EntityFrameworkCore.Query.QueryExecutionPlanned";

    private readonly IHub _hub;
    private readonly SentryOptions _options;

    private readonly AsyncLocal<WeakReference<ISpan>> _spansCompilerLocal = new();
    private readonly AsyncLocal<WeakReference<ISpan>> _spansQueryLocal = new();
    private readonly AsyncLocal<WeakReference<ISpan>> _spansConnectionLocal = new();

    private bool _logConnectionEnabled = true;
    private bool _logQueryEnabled = true;

    public SentryEFCoreListener(IHub hub, SentryOptions options)
    {
        _hub = hub;
        _options = options;
    }

    internal void DisableConnectionSpan() => _logConnectionEnabled = false;

    internal void DisableQuerySpan() => _logQueryEnabled = false;

    private void AddSpan(SentryEFSpanType type, string operation, string? description)
    {
        var transaction = _hub.GetTransactionIfSampled();
        if (transaction == null)
        {
            return;
        }

        var parent = type == SentryEFSpanType.QueryExecution
            ? transaction.GetLastActiveSpan() ?? transaction.GetDbParentSpan()
            : transaction.GetDbParentSpan();

        var child = parent.StartChild(operation, description);

        var asyncLocalSpan = GetSpanBucket(type);
        asyncLocalSpan.Value = new WeakReference<ISpan>(child);
    }

    private ISpan? TakeSpan(SentryEFSpanType type)
    {
        var transaction = _hub.GetTransactionIfSampled();
        if (transaction == null)
        {
            return null;
        }

        if (GetSpanBucket(type).Value is { } reference && reference.TryGetTarget(out var startedSpan))
        {
            return startedSpan;
        }

        _options.LogWarning("Trying to close a span that was already garbage collected. {0}", type);
        return null;
    }

    private AsyncLocal<WeakReference<ISpan>> GetSpanBucket(SentryEFSpanType type)
        => type switch
        {
            SentryEFSpanType.QueryCompiler => _spansCompilerLocal,
            SentryEFSpanType.QueryExecution => _spansQueryLocal,
            SentryEFSpanType.Connection => _spansConnectionLocal,
            _ => throw new($"Unknown SentryEFSpanType: {type}")
        };

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        try
        {
            switch (value.Key)
            {
                // Query compiler span
                case EFQueryStartCompiling or EFQueryCompiling:
                    AddSpan(SentryEFSpanType.QueryCompiler, "db.query.compile", FilterNewLineValue(value.Value));
                    break;
                case EFQueryCompiled:
                    TakeSpan(SentryEFSpanType.QueryCompiler)?.Finish(SpanStatus.Ok);
                    break;

                // Connection span (A transaction may or may not show a connection with it.)
                case EFConnectionOpening when _logConnectionEnabled:
                    AddSpan(SentryEFSpanType.Connection, "db.connection", null);
                    break;
                case EFConnectionClosed when _logConnectionEnabled:
                    TakeSpan(SentryEFSpanType.Connection)?.Finish(SpanStatus.Ok);
                    break;

                // Query Execution span
                case EFCommandExecuting when _logQueryEnabled:
                    AddSpan(SentryEFSpanType.QueryExecution, "db.query", FilterNewLineValue(value.Value));
                    break;
                case EFCommandFailed when _logQueryEnabled:
                    TakeSpan(SentryEFSpanType.QueryExecution)?.Finish(SpanStatus.InternalError);
                    break;
                case EFCommandExecuted when _logQueryEnabled:
                    TakeSpan(SentryEFSpanType.QueryExecution)?.Finish(SpanStatus.Ok);
                    break;
            }
        }
        catch (Exception ex)
        {
            _options.LogError("Failed to intercept EF Core event.", ex);
        }
    }

    /// <summary>
    /// Get the Query with error message and remove the unneeded values.
    /// </summary>
    /// <example>
    /// Compiling query model:
    /// EF initialize...\r\nEF Query...
    /// becomes:
    /// EF Query...
    /// </example>
    /// <param name="value">the query to be parsed value</param>
    /// <returns>the filtered query</returns>
    internal static string? FilterNewLineValue(object? value)
    {
        var str = value?.ToString();
        return str?[(str.IndexOf('\n') + 1)..];
    }
}
