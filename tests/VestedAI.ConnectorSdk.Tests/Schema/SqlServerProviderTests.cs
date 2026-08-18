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
    public async Task MultipleEntitiesAreOrderedByLogicalNameWithoutCrossContamination()
    {
        // Every other fixture in this file produces exactly one entity, so
        // entity ordering, ScopeKey, Kind and variant Ordinal were never
        // pinned, and one entity's columns leaking into another's was never
        // guarded against.
        const string custBase = "ASG$Customer$437dbf0e-84ff-417a-965d-ed2bb9650972";
        const string custExt  = "ASG$Customer$5ecfc871-5d82-43f1-9c54-59685e82318d";
        const string locBase  = "ASG$Location$437dbf0e-84ff-417a-965d-ed2bb9650972";

        var cat = new FakeCatalog();
        cat.Tables.Add(new CatalogTable("dbo", custBase));
        cat.Tables.Add(new CatalogTable("dbo", custExt));
        cat.Tables.Add(new CatalogTable("dbo", locBase));
        cat.Columns.Add(new CatalogColumn(custBase, "No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(custExt, "No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(custExt, "Credit Limit", "decimal", true, 2, false));
        cat.Columns.Add(new CatalogColumn(locBase, "Code", "nvarchar", false, 1, true));
        cat.Links.Add(new CatalogExtensionLink(custExt, custBase));

        var schema = await new SqlServerProvider(cat).DescribeAsync("ASG", default);

        Assert.Equal(new[] { "Customer", "Location" }, schema.Entities.Select(e => e.LogicalName));

        var customer = schema.Entities[0];
        Assert.Equal("ASG", customer.ScopeKey);
        Assert.Equal("table", customer.Kind);
        Assert.Equal(2, customer.Variants.Count);
        Assert.Equal(0, customer.Variants.Single(v => v.Role == "base").Ordinal);
        Assert.Equal(1, customer.Variants.Single(v => v.Role == "extension").Ordinal);
        Assert.DoesNotContain(customer.Columns, c => c.Name == "Code");

        var location = schema.Entities[1];
        Assert.Equal("ASG", location.ScopeKey);
        Assert.Equal("table", location.Kind);
        Assert.Single(location.Variants);
        Assert.DoesNotContain(location.Columns, c => c.Name == "Credit Limit");
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
    public async Task ColumnFieldsBeyondNameAndOriginAreCorrectForBaseAndExtension()
    {
        // Every other test reads only Name, VariantPhysicalName and Caption —
        // hard-coding IsPk: false, or inverting Nullable, would still pass
        // them all. IsPk is how a downstream consumer finds the join column
        // per variant, so it has to be right on both the base's own PK
        // column and the extension's mirrored PK column, not just one.
        var schema = await new SqlServerProvider(TwoVariantItem()).DescribeAsync("ASG", default);

        var item = schema.Entities.Single();

        var baseKeyCol = item.Columns.Single(c => c.Name == "No." && c.VariantPhysicalName == ItemBase);
        Assert.True(baseKeyCol.IsPk);
        Assert.False(baseKeyCol.Nullable);
        Assert.Equal("nvarchar", baseKeyCol.Type);
        Assert.Equal(1, baseKeyCol.Position);

        var extKeyCol = item.Columns.Single(c => c.Name == "No." && c.VariantPhysicalName == ItemExt);
        Assert.True(extKeyCol.IsPk);

        var extOnlyCol = item.Columns.Single(c => c.Name == "Retail Dept_");
        Assert.False(extOnlyCol.IsPk);
        Assert.True(extOnlyCol.Nullable);
        Assert.Equal("nvarchar", extOnlyCol.Type);
        Assert.Equal(2, extOnlyCol.Position);
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
    public async Task JoinKeyOrdersACompositePrimaryKeyByOrdinalPositionNotInsertionOrder()
    {
        // Every other fixture's primary key is one column, so the
        // .OrderBy(c => c.OrdinalPosition) on the join key never ran against
        // more than one row. BC's own Sales Line and Item Ledger Entry carry
        // 3-part keys, and the join key is this slice's entire output.
        // Columns are inserted here scrambled (position 2, then 3, then 1) —
        // inserting them already sorted would prove nothing about the
        // ordering logic actually running.
        const string baseTable = "ASG$Sales Line$437dbf0e-84ff-417a-965d-ed2bb9650972";
        const string extTable  = "ASG$Sales Line$5ecfc871-5d82-43f1-9c54-59685e82318d";

        var cat = new FakeCatalog();
        cat.Tables.Add(new CatalogTable("dbo", baseTable));
        cat.Tables.Add(new CatalogTable("dbo", extTable));
        cat.Columns.Add(new CatalogColumn(baseTable, "Line No.", "int", false, 2, true));
        cat.Columns.Add(new CatalogColumn(baseTable, "Document Type", "int", false, 3, true));
        cat.Columns.Add(new CatalogColumn(baseTable, "Document No.", "nvarchar", false, 1, true));
        cat.Columns.Add(new CatalogColumn(extTable, "Document No.", "nvarchar", false, 1, true));
        cat.Links.Add(new CatalogExtensionLink(extTable, baseTable));

        var schema = await new SqlServerProvider(cat).DescribeAsync("ASG", default);

        var entity = schema.Entities.Single(e => e.LogicalName == "Sales Line");
        Assert.Equal(new[] { "Document No.", "Line No.", "Document Type" }, entity.JoinKey);
    }

    [Fact]
    public async Task MultiVariantEntityWithNoPrimaryKeyEmitsEmptyJoinKeyAndNoRelation()
    {
        // There is nothing to join on: a base variant with zero
        // IsPrimaryKey columns must not emit a variant_join relation, even
        // though the entity itself has more than one variant.
        const string noKeyBase = "ASG$Comment Line$437dbf0e-84ff-417a-965d-ed2bb9650972";
        const string noKeyExt  = "ASG$Comment Line$5ecfc871-5d82-43f1-9c54-59685e82318d";

        var cat = new FakeCatalog();
        cat.Tables.Add(new CatalogTable("dbo", noKeyBase));
        cat.Tables.Add(new CatalogTable("dbo", noKeyExt));
        cat.Columns.Add(new CatalogColumn(noKeyBase, "Text", "nvarchar", true, 1, false));
        cat.Columns.Add(new CatalogColumn(noKeyExt, "Text", "nvarchar", true, 1, false));
        cat.Columns.Add(new CatalogColumn(noKeyExt, "Extra", "nvarchar", true, 2, false));
        cat.Links.Add(new CatalogExtensionLink(noKeyExt, noKeyBase));

        var schema = await new SqlServerProvider(cat).DescribeAsync("ASG", default);

        var entity = schema.Entities.Single(e => e.LogicalName == "Comment Line");
        Assert.Equal(2, entity.Variants.Count);
        Assert.Empty(entity.JoinKey);
        Assert.Empty(schema.Relations);
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
        Assert.Empty(entity.JoinKey);
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

        // Companies come first and sorted; the system scope is appended
        // because this fixture holds "Access Control", a company-less table.
        Assert.Equal(new[] { "ASG", "ASG - HData", SqlServerProvider.SystemScopeKey }, scopes);
    }

    [Fact]
    public async Task ScopesAsyncSortsCompaniesRegardlessOfCatalogInsertionOrder()
    {
        // TwoVariantItem() happens to insert "ASG" before "ASG - HData",
        // which already matches sorted order, so ScopesAsyncListsEvery...
        // above cannot tell a real sort from a coincidence. This fixture
        // inserts them the other way around.
        var cat = new FakeCatalog();
        cat.Tables.Add(new CatalogTable("dbo", HDataItem));
        cat.Tables.Add(new CatalogTable("dbo", ItemBase));

        var scopes = await new SqlServerProvider(cat).ScopesAsync(default);

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

    [Fact]
    public async Task CatalogFingerprintChangesWhenAColumnIsAddedToAnExistingTable()
    {
        // A BC extension deploy typically adds a FIELD to an existing table,
        // not a new table. A name-only fingerprint would miss this entirely
        // and the core would never re-extract a stale snapshot.
        var before = await new SqlServerProvider(TwoVariantItem()).CatalogFingerprintAsync(default);

        var cat = TwoVariantItem();
        cat.Columns.Add(new CatalogColumn(ItemBase, "Type", "int", true, 3, false));
        var after = await new SqlServerProvider(cat).CatalogFingerprintAsync(default);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task CatalogFingerprintIsStableRegardlessOfCatalogRowOrder()
    {
        // The whole reason the fingerprint is cheap to poll is that an
        // unordered INFORMATION_SCHEMA scan of the same, unchanged catalog
        // still hashes to the same value. Two catalogs with identical
        // content, inserted in opposite table AND column order, must
        // produce identical hashes.
        var forward = new FakeCatalog();
        forward.Tables.Add(new CatalogTable("dbo", ItemBase));
        forward.Tables.Add(new CatalogTable("dbo", ItemExt));
        forward.Columns.Add(new CatalogColumn(ItemBase, "No.", "nvarchar", false, 1, true));
        forward.Columns.Add(new CatalogColumn(ItemExt, "No.", "nvarchar", false, 1, true));
        forward.Columns.Add(new CatalogColumn(ItemExt, "Retail Dept_", "nvarchar", true, 2, false));

        var reversed = new FakeCatalog();
        reversed.Tables.Add(new CatalogTable("dbo", ItemExt));
        reversed.Tables.Add(new CatalogTable("dbo", ItemBase));
        reversed.Columns.Add(new CatalogColumn(ItemExt, "Retail Dept_", "nvarchar", true, 2, false));
        reversed.Columns.Add(new CatalogColumn(ItemExt, "No.", "nvarchar", false, 1, true));
        reversed.Columns.Add(new CatalogColumn(ItemBase, "No.", "nvarchar", false, 1, true));

        var forwardHash = await new SqlServerProvider(forward).CatalogFingerprintAsync(default);
        var reversedHash = await new SqlServerProvider(reversed).CatalogFingerprintAsync(default);

        Assert.Equal(forwardHash, reversedHash);
    }

    // ---------------------------------------------------------------------
    // System scope. Business Central's own tables carry no company prefix, so
    // before SystemScopeKey they matched no scope and were dropped from every
    // describe — measured on the live catalog, 0 of 16,250 extracted variants
    // lacked a prefix. With the core's SQL gate at `enforce` that made any
    // query touching them refusable, with no scope an operator could name.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DescribesCompanylessSystemTablesUnderTheSystemScope()
    {
        var schema = await new SqlServerProvider(TwoVariantItem())
            .DescribeAsync(SqlServerProvider.SystemScopeKey, default);

        var acl = Assert.Single(schema.Entities, e => e.LogicalName == "Access Control");
        Assert.Equal(SqlServerProvider.SystemScopeKey, acl.ScopeKey);

        // The variant carries the table's LITERAL name: these are referenced
        // unprefixed in SQL, so anything else would be unqueryable.
        Assert.Equal("Access Control", Assert.Single(acl.Variants).PhysicalName);
        Assert.Empty(acl.JoinKey);
    }

    [Fact]
    public async Task SystemScopeExcludesBcStorageInternals()
    {
        var schema = await new SqlServerProvider(TwoVariantItem())
            .DescribeAsync(SqlServerProvider.SystemScopeKey, default);

        // $ndo$cachesync describes how the catalog is STORED, not anything a
        // question can be asked about. It has no company prefix either, so
        // without an explicit exclusion it would ride in on the same rule.
        Assert.DoesNotContain(schema.Entities, e => e.LogicalName.StartsWith("$"));
    }

    [Fact]
    public async Task SystemScopeAndCompanyScopesDoNotOverlap()
    {
        var provider = new SqlServerProvider(TwoVariantItem());

        var system = await provider.DescribeAsync(SqlServerProvider.SystemScopeKey, default);
        var company = await provider.DescribeAsync("ASG", default);

        // A company table must never appear in the system scope...
        Assert.DoesNotContain(system.Entities, e => e.LogicalName == "Item");
        // ...and a system table must never appear in a company's.
        Assert.DoesNotContain(company.Entities, e => e.LogicalName == "Access Control");
    }

    [Fact]
    public async Task ScopesOffersTheSystemScopeOnlyWhenSuchTablesExist()
    {
        var withSystem = await new SqlServerProvider(TwoVariantItem()).ScopesAsync(default);
        Assert.Contains(SqlServerProvider.SystemScopeKey, withSystem);

        // A catalog of nothing but company tables must NOT advertise the
        // scope: it would extract to an empty catalog, which the core's
        // ingestor refuses rather than superseding a good snapshot with none.
        var companiesOnly = new FakeCatalog();
        companiesOnly.Tables.Add(new CatalogTable("dbo", ItemBase));
        companiesOnly.Columns.Add(new CatalogColumn(ItemBase, "No.", "nvarchar", false, 1, true));

        var scopes = await new SqlServerProvider(companiesOnly).ScopesAsync(default);
        Assert.DoesNotContain(SqlServerProvider.SystemScopeKey, scopes);
    }

    [Fact]
    public async Task ThrowsWhenARealCompanyCollidesWithTheSystemScopeKey()
    {
        // I first claimed this collision was IMPOSSIBLE — that BcPhysicalName's
        // non-greedy company group could never span a `$`. It can: non-greedy
        // stops at the FIRST position where the remainder matches, and if that
        // requires crossing a `$` it will. A company named "$system" therefore
        // parses out of "$system$Item$<app-id>". This test is what caught the
        // wrong claim, so it pins the guard rather than the assumption.
        var cat = new FakeCatalog();
        var colliding = SqlServerProvider.SystemScopeKey + "$Item$437dbf0e-84ff-417a-965d-ed2bb9650972";
        cat.Tables.Add(new CatalogTable("dbo", colliding));
        cat.Columns.Add(new CatalogColumn(colliding, "No.", "nvarchar", false, 1, true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqlServerProvider(cat).ScopesAsync(default));

        Assert.Contains(SqlServerProvider.SystemScopeKey, ex.Message);
    }
}
