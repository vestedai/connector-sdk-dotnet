using VestedAI.ConnectorSdk.Schema;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Schema;

public class BcPhysicalNameTests
{
    [Theory]
    // Plain company.
    [InlineData("ASG$Transfer Header$437dbf0e-84ff-417a-965d-ed2bb9650972",
        "ASG", "Transfer Header", "437dbf0e-84ff-417a-965d-ed2bb9650972")]
    // Company with a space AND a hyphen. A \w+ company pattern drops this and
    // 87% of the real catalog with it.
    [InlineData("ASG - HData$LSC Transaction Header$d338f3aa-e3b4-4239-a2a6-a67e7ae1900d",
        "ASG - HData", "LSC Transaction Header", "d338f3aa-e3b4-4239-a2a6-a67e7ae1900d")]
    // Company with digits.
    [InlineData("ASG CLEAN - 20221230$Item$437dbf0e-84ff-417a-965d-ed2bb9650972",
        "ASG CLEAN - 20221230", "Item", "437dbf0e-84ff-417a-965d-ed2bb9650972")]
    [InlineData("CRONUS - LS Central$Location$5ecfc871-5d82-43f1-9c54-59685e82318d",
        "CRONUS - LS Central", "Location", "5ecfc871-5d82-43f1-9c54-59685e82318d")]
    // Logical name containing an underscore-for-period, BC's own convention.
    [InlineData("ASG$LSC Trans_ Sales Entry$5ecfc871-5d82-43f1-9c54-59685e82318d",
        "ASG", "LSC Trans_ Sales Entry", "5ecfc871-5d82-43f1-9c54-59685e82318d")]
    public void ParsesRealCatalogNames(string input, string company, string logical, string ext)
    {
        Assert.True(BcPhysicalName.TryParse(input, out var parsed));
        Assert.Equal(company, parsed.Company);
        Assert.Equal(logical, parsed.LogicalName);
        Assert.Equal(ext, parsed.ExtensionAppId);
    }

    [Theory]
    [InlineData("$ndo$cachesync")]              // BC internal
    [InlineData("$ndo$navapptableextension")]   // BC internal
    [InlineData("Access Control")]              // system table, no company
    [InlineData("Application Object Metadata")]
    // Real production name: has the $ separators but no GUID suffix.
    [InlineData("ASG - HData$LSC Transaction Header$EDM")]
    [InlineData("")]
    public void RejectsNonCompanyTables(string input)
    {
        Assert.False(BcPhysicalName.TryParse(input, out _));
    }

    [Fact]
    public void CompanyIsLazySoTheGuidAnchorsTheParse()
    {
        // A logical name containing '$' must not steal the company segment.
        Assert.True(BcPhysicalName.TryParse(
            "ASG$Odd$Name$437dbf0e-84ff-417a-965d-ed2bb9650972", out var parsed));
        Assert.Equal("ASG", parsed.Company);
        Assert.Equal("Odd$Name", parsed.LogicalName);
    }
}
