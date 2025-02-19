namespace Sentry.Tests.Protocol;

public class BreadcrumbTests : ImmutableTests<Breadcrumb>
{
    private readonly IDiagnosticLogger _testOutputLogger;

    public BreadcrumbTests(ITestOutputHelper output)
    {
        _testOutputLogger = new TestOutputDiagnosticLogger(output);
    }

    [Fact]
    public void SerializeObject_ParameterlessConstructor_IncludesTimestamp()
    {
        var sut = new Breadcrumb("test", "unit");

        var actualJson = sut.ToJsonString(_testOutputLogger);
        var actual = Json.Parse(actualJson, Breadcrumb.FromJson);

        Assert.NotEqual(default, actual.Timestamp);
    }

    [Fact]
    public void SerializeObject_AllPropertiesSetToNonDefault_SerializesValidObject()
    {
        var sut = new Breadcrumb(
            DateTimeOffset.MaxValue,
            "message1",
            "type1",
            new Dictionary<string, string> { { "key", "val" } },
            "category1",
            BreadcrumbLevel.Warning);

        var actual = sut.ToJsonString(_testOutputLogger);

        Assert.Equal(
            "{\"timestamp\":\"9999-12-31T23:59:59.999Z\"," +
            "\"message\":\"message1\"," +
            "\"type\":\"type1\"," +
            "\"data\":{\"key\":\"val\"}," +
            "\"category\":\"category1\"," +
            "\"level\":\"warning\"}",
            actual);
    }

    [Theory]
    [MemberData(nameof(TestCases))]
    public void SerializeObject_TestCase_SerializesAsExpected((Breadcrumb breadcrumb, string serialized) @case)
    {
        var actual = @case.breadcrumb.ToJsonString(_testOutputLogger);

        Assert.Equal(@case.serialized, actual);
    }

    public static IEnumerable<object[]> TestCases()
    {
        // Timestamp is included in every breadcrumb
        var expectedTimestamp = DateTimeOffset.MaxValue;
        var expectedTimestampString = "9999-12-31T23:59:59.999Z";
        var timestampString = $"\"timestamp\":\"{expectedTimestampString}\"";

        yield return new object[] { (new Breadcrumb(expectedTimestamp), $"{{{timestampString}}}") };
        yield return new object[] { (new Breadcrumb(expectedTimestamp, "message"), $"{{{timestampString},\"message\":\"message\"}}") };
        yield return new object[] { (new Breadcrumb(expectedTimestamp, type: "type"), $"{{{timestampString},\"type\":\"type\"}}") };
        yield return new object[] { (new Breadcrumb(expectedTimestamp, data: new Dictionary<string, string> { { "key", "val" } }), $"{{{timestampString},\"data\":{{\"key\":\"val\"}}}}") };
        yield return new object[] { (new Breadcrumb(expectedTimestamp, category: "category"), $"{{{timestampString},\"category\":\"category\"}}") };
        yield return new object[] { (new Breadcrumb(expectedTimestamp, level: BreadcrumbLevel.Critical), $"{{{timestampString},\"level\":\"critical\"}}") };
    }
}
