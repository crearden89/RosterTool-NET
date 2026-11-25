using Microsoft.EntityFrameworkCore;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Model;

public class ModelSmokeTests
{
    [Fact]
    public void The_model_creates_on_sqlite()
    {
        using var db = new TestDatabase();
        using var ctx = db.System();
        Assert.Equal(0, ctx.Stores.Count());
    }
}
