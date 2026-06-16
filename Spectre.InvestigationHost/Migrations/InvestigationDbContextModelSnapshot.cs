using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Spectre.InvestigationHost.Data;

#nullable disable

namespace Spectre.InvestigationHost.Migrations;

[DbContext(typeof(InvestigationDbContext))]
public sealed class InvestigationDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.2");
    }
}
