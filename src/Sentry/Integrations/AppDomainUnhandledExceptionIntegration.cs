using Sentry.Extensibility;
using Sentry.Internal;
using Sentry.Protocol;

namespace Sentry.Integrations;

internal class AppDomainUnhandledExceptionIntegration : ISdkIntegration
{
    private readonly IAppDomain _appDomain;
    private IHub? _hub;
    private SentryOptions? _options;

    internal AppDomainUnhandledExceptionIntegration(IAppDomain? appDomain = null)
        => _appDomain = appDomain ?? AppDomainAdapter.Instance;

    public void Register(IHub hub, SentryOptions options)
    {
        _hub = hub;
        _options = options;
        _appDomain.UnhandledException += Handle;
    }

    // Internal for testability
#if !NET6_0_OR_GREATER
    [HandleProcessCorruptedStateExceptions]
#endif
    [SecurityCritical]
    internal void Handle(object sender, UnhandledExceptionEventArgs e)
    {
        _options?.LogDebug("AppDomain Unhandled Exception");

        if (e.ExceptionObject is Exception ex)
        {
            ex.Data[Mechanism.HandledKey] = false;
            ex.Data[Mechanism.MechanismKey] = "AppDomain.UnhandledException";

            // Call the internal implementation, so that we still capture even if the hub has been disabled.
            _hub?.CaptureExceptionInternal(ex);
        }

        if (e.IsTerminating)
        {
            _hub?.Flush(_options!.ShutdownTimeout);
        }
    }
}
