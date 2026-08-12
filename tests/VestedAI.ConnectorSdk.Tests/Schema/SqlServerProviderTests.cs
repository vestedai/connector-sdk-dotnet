using VestedAI.ConnectorSdk.Schema;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Schema;

internal sealed class FakeCatalog : ICatalogReader
{
    public List<CatalogTable> Tables { get; } = new();
    public List<CatalogColumn> Columns { get; } = new();
    public List<CatalogExtensionLink> Links { get; } = new();

    public Task<IReadOnlyList<CatalogTable>> TablesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CatalogTable>>(Tables);

    public Task<IReadOnlyList<CatalogColumn>> ColumnsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CatalogColumn>>(Columns);

    public Task<IReadOnlyList<CatalogExtensionLink>> ExtensionLinksAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CatalogExtensionLink>>(Links);
}

public class SqlServerProviderTests
{
    private const string ItemBase = "ASG$Item$437dbf0e-84ff-417a-965d-ed2bb9650972";
    private const string ItemExt  = "ASG$Item$5ecfc871-5d82-43f1-9c54-59685e82318d";
    private const string HDataItem = "ASG - HData$Item$437dbf0e-84ff-417a-965d-ed2bb9650972";

    private static FakeCatalog TwoVariantItem()
    {
        var cat = new FakeCatalog();
        cat.Tables.Add(new CatalogTable("dbo", ItemBase));
        cat.Tables.Add(new CatalogTable("dbo", ItemExt));
        cat.Tables.Add(new CatalogTable("dbo", HDataItem));
        cat.Tables.Add(new CatalogTable("dbo", "$ndo$cachesync"));
        cat.Tables.Add(new CatalogTable("dbo", "Access Control"));

        cat.Columns.Add(new CatalogColumn(ItemBase, "No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(ItemBase, "Description", "nvarchar", true, 2, false));
        cat.Columns.Add(new CatalogColumn(ItemExt, "No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(ItemExt, "Retail Dept_", "nvarchar", true, 2, false));
        cat.Columns.Add(new CatalogColumn(HDataItem, "No.", "nvarchar", false, 1, true));

        cat.Links.Add(new CatalogExtensionLink(ItemExt, ItemBase));
        return cat;
    }

    [Fact]
    public async Task GroupsPhysicalTablesIntoOneEntityWithItsVariants()
    {
        var schema = await new SqlServerProvider(TwoVariantItem()).DescribeAsync("ASG", default);

        var item = Assert.Single(schema.Entities, e => e.LogicalName == "Item");
        Assert.Equal(2, item.Variants.Count);
        Assert.Equal(ItemBase, item.Variants.Single(v => v.Role == "base").PhysicalName);
        Assert.Equal(ItemExt, item.Variants.Single(v => v.Role == "extension").PhysicalName);
    }

    [Fact]
    public async Task ScopesToOneCompanyAndExcludesTheOthers()
    {
        var schema = await new SqlServerProvider(TwoVariantItem()).DescribeAsync("ASG", default);

        Assert.DoesNotContain(schema.Entities.SelectMany(e => e.Variants),
            v => v.PhysicalName == HDataItem);
    }

    [Fact]
    public async Task DropsSystemAndNdoTables()
    {
        var schema = await new SqlServerProvider(TwoVariantItem()).DescribeAsync("ASG", default);

        Assert.Single(schema.Entities);
    }

    [Fact]
    public async Task MergesColumnsAcrossVariantsAndRecordsTheirOrigin()
    {
        var schema = await new SqlServerProvider(TwoVariantItem()).DescribeAsync("ASG", default);

        var item = schema.Entities.Single();
        Assert.Equal(4, item.Columns.Count);
        Assert.Equal(ItemExt, item.Columns.Single(c => c.Name == "Retail Dept_").VariantPhysicalName);
    }

    [Fact]
    public async Task ColumnCaptionIsNullNotADuplicateOfTheColumnName()
    {
        // Correction to the brief: CatalogColumn carries no caption field, so
        // Caption: c.ColumnName would fabricate a value that looks like
        // curated business vocabulary but is really just the column name
        // again. Real BC captions live in metadata INFORMATION_SCHEMA does
        // not expose — this slice must not pretend otherwise.
        var schema = await new SqlServerProvider(TwoVariantItem()).DescribeAsync("ASG", default);

        var item = schema.Entities.Single();
        Assert.All(item.Columns, c => Assert.Null(c.Caption));
    }

    [Fact]
    public async Task JoinKeyIsThePrimaryKeyOfTheBaseVariant()
    {
        var schema = await new SqlServerProvider(TwoVariantItem()).DescribeAsync("ASG", default);

        Assert.Equal(new[] { "No." }, schema.Entities.Single().JoinKey);
    }

    [Fact]
    public async Task EmitsOneVariantJoinRelationForTheEntity()
    {
        var schema = await new SqlServerProvider(TwoVariantItem()).DescribeAsync("ASG", default);

        var rel = Assert.Single(schema.Relations, r => r.Kind == "variant_join");
        Assert.Equal("Item", rel.FromEntity);
        Assert.Equal("Item", rel.ToEntity);
        Assert.Equal(new[] { "No." }, rel.FromColumns);
    }

    [Fact]
    public async Task EntityWithMultipleExtensionsStillEmitsExactlyOneVariantJoinRelation()
    {
        // Correction to the brief: a naive "one relation per extension" loop
        // emits byte-identical rows for every extension beyond the first,
        // carrying no distinguishing information. An entity with N variants
        // must still produce exactly one variant_join relation, expressing
        // "this entity requires an internal join on these columns" once.
        const string itemExt2 = "ASG$Item$6f1e9b3a-2f34-4a11-9b0d-2a6a2e6a2c11";

        var cat = TwoVariantItem();
        cat.Tables.Add(new CatalogTable("dbo", itemExt2));
        cat.Columns.Add(new CatalogColumn(itemExt2, "No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(itemExt2, "Weight", "decimal", true, 2, false));
        cat.Links.Add(new CatalogExtensionLink(itemExt2, ItemBase));

        var schema = await new SqlServerProvider(cat).DescribeAsync("ASG", default);

        var item = schema.Entities.Single(e => e.LogicalName == "Item");
        Assert.Equal(3, item.Variants.Count);

        var rel = Assert.Single(schema.Relations, r => r.Kind == "variant_join");
        Assert.Equal("Item", rel.FromEntity);
        Assert.Equal("Item", rel.ToEntity);
        Assert.Equal(new[] { "No." }, rel.FromColumns);
        Assert.Equal(new[] { "No." }, rel.ToColumns);
    }

    [Fact]
    public async Task SingleVariantEntityHasNoJoinAndNoVariantRelation()
    {
        // The degenerate case a SQL-Server-shaped design gets wrong. Plain
        // MySQL tables look like this, so it must be right before slice D.
        var cat = new FakeCatalog();
        var loc = "ASG$Location$437dbf0e-84ff-417a-965d-ed2bb9650972";
        cat.Tables.Add(new CatalogTable("dbo", loc));
        cat.Columns.Add(new CatalogColumn(loc, "Code", "nvarchar", false, 1, true));

        var schema = await new SqlServerProvider(cat).DescribeAsync("ASG", default);

        var entity = schema.Entities.Single();
        Assert.Single(entity.Variants);
        Assert.Equal("base", entity.Variants[0].Role);
        Assert.Empty(schema.Relations);
    }

    [Fact]
    public async Task FallsBackToMostColumnsAsBaseWhenMetadataIsUnavailable()
    {
        var cat = TwoVariantItem();
        cat.Links.Clear();                                    // $ndo$ table unreadable
        cat.Columns.Add(new CatalogColumn(ItemBase, "Type", "int", true, 3, false));

        var schema = await new SqlServerProvider(cat).DescribeAsync("ASG", default);

        Assert.Equal(ItemBase, schema.Entities.Single().Variants.Single(v => v.Role == "base").PhysicalName);
    }

    [Fact]
    public async Task LinkDeclaredBaseWinsEvenWhenItSortsAfterTheExtensionAndHasFewerColumns()
    {
        // Every other fixture in this file gives its base table a GUID that
        // happens to sort alphabetically before its extension's GUID
        // ("437dbf0e..." < "5ecfc871..."), so a PickBase reduced to
        // physicalNames.First() silently agrees with the correct answer in
        // every one of them. This fixture inverts both signals a bad
        // PickBase could latch onto: the declared base sorts SECOND and has
        // FEWER columns than the extension, so only reading the $ndo$ link
        // gets it right.
        const string vendorBase = "ASG$Vendor$9ecfc871-5d82-43f1-9c54-59685e82318d";
        const string vendorExt  = "ASG$Vendor$137dbf0e-84ff-417a-965d-ed2bb9650972";

        var cat = new FakeCatalog();
        cat.Tables.Add(new CatalogTable("dbo", vendorBase));
        cat.Tables.Add(new CatalogTable("dbo", vendorExt));
        cat.Columns.Add(new CatalogColumn(vendorBase, "No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(vendorExt, "No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(vendorExt, "Foo", "nvarchar", true, 2, false));
        cat.Columns.Add(new CatalogColumn(vendorExt, "Bar", "nvarchar", true, 3, false));
        cat.Links.Add(new CatalogExtensionLink(vendorExt, vendorBase));

        var schema = await new SqlServerProvider(cat).DescribeAsync("ASG", default);

        var entity = schema.Entities.Single(e => e.LogicalName == "Vendor");
        Assert.Equal(vendorBase, entity.Variants.Single(v => v.Role == "base").PhysicalName);
        Assert.Equal(vendorExt, entity.Variants.Single(v => v.Role == "extension").PhysicalName);
    }

    [Fact]
    public async Task ColumnCountFallbackWinsEvenWhenTheBaseSortsAfterTheExtensionAlphabetically()
    {
        // Same defeat of the alphabetical coincidence as the test above, but
        // for the no-links fallback path: the table with more columns must
        // win regardless of how its physical name sorts.
        const string custFewCols  = "ASG$Customer$137dbf0e-84ff-417a-965d-ed2bb9650972";
        const string custManyCols = "ASG$Customer$9ecfc871-5d82-43f1-9c54-59685e82318d";

        var cat = new FakeCatalog();
        cat.Tables.Add(new CatalogTable("dbo", custFewCols));
        cat.Tables.Add(new CatalogTable("dbo", custManyCols));
        cat.Columns.Add(new CatalogColumn(custFewCols, "No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(custManyCols, "No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(custManyCols, "Name", "nvarchar", true, 2, false));
        cat.Columns.Add(new CatalogColumn(custManyCols, "Address", "nvarchar", true, 3, false));
        // No links: $ndo$ table unreadable, forcing the column-count fallback.

        var schema = await new SqlServerProvider(cat).DescribeAsync("ASG", default);

        var entity = schema.Entities.Single(e => e.LogicalName == "Customer");
        Assert.Equal(custManyCols, entity.Variants.Single(v => v.Role == "base").PhysicalName);
        Assert.Equal(custFewCols, entity.Variants.Single(v => v.Role == "extension").PhysicalName);
    }

    [Fact]
    public async Task ScopesAsyncListsEveryCompanyInTheCatalog()
    {
        var scopes = await new SqlServerProvider(TwoVariantItem()).ScopesAsync(default);

        Assert.Equal(new[] { "ASG", "ASG - HData" }, scopes);
    }

    [Fact]
    public async Task CatalogFingerprintChangesWhenATableIsAdded()
    {
        var before = await new SqlServerProvider(TwoVariantItem()).CatalogFingerprintAsync(default);

        var cat = TwoVariantItem();
        cat.Tables.Add(new CatalogTable("dbo", "ASG$Location$437dbf0e-84ff-417a-965d-ed2bb9650972"));
        var after = await new SqlServerProvider(cat).CatalogFingerprintAsync(default);

        Assert.NotEqual(before, after);
    }
}
