// ReSharper disable once CheckNamespace
namespace Sentry;

public static class DsnSamples
{
    /// <summary>
    /// This is the main DSN we will test with.
    /// </summary>
    public const string ValidDsn = "https://d4d82fc1c2c4032a83f3a29aa3a3aff@fake-sentry.io:65535/2147483647";

    /// <summary>
    /// DSN with secret. Used only to test backwards compatibility.
    /// </summary>
    [Obsolete("Sentry has dropped the use of secrets.")]
    public const string ValidDsnWithSecret = "https://d4d82fc1c2c4032a83f3a29aa3a3aff:ed0a8589a0bb4d4793ac4c70375f3d65@fake-sentry.io:65535/2147483647";

    /// <summary>
    /// Missing ProjectId
    /// </summary>
    public const string InvalidDsn = "https://d4d82fc1c2c4032a83f3a29aa3a3aff@fake-sentry.io:65535/";
}
