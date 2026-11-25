// New: the web app's entry point. The services and the pipeline are in ShellSetup.cs;
// page areas plug in through Features/FeatureModules.cs, never here.

using Roster.Web;

var builder = WebApplication.CreateBuilder(args);
builder.AddRosterWeb();

var app = builder.Build();
app.UseRosterWeb();
app.Run();

/// <summary>The app's entry type, for the integration tests (WebApplicationFactory).</summary>
public partial class Program;
