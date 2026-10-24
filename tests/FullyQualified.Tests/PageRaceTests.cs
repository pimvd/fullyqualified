namespace FullyQualified.Tests;

public class PageRaceTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    /// <summary>
    /// Components below a page start their own database queries as soon as the page first yields. If the page's own queries do not
    /// take turns with theirs (DbGate), the shared DbContext throws now and then, so one request proves little: ask many times.
    /// </summary>
    [DbFact]
    public async Task Pages_with_panels_never_use_the_database_context_twice_at_once()
    {
        var org = await NewOrg();
        var owner = await EnsureOwner();
        foreach (var path in new[] { $"/tenants/{org.Id}", "/tenants", "/users" })
            for (var i = 0; i < 60; i++)
            {
                var body = await (await Factory.As(owner).GetAsync(path)).Content.ReadAsStringAsync();
                Assert.DoesNotContain("InvalidOperationException", body);
            }
    }
}
