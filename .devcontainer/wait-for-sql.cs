// New: start-app.sh waits with this until SQL Server accepts a login, before it starts the
// app. A .NET file-based app, because the dev container has the .NET SDK and no SQL tools:
//   dotnet run --file .devcontainer/wait-for-sql.cs -- [seconds, default 300]
// It logs in with the app's connection string (ConnectionStrings__Roster), to master: the
// app's own database does not exist until the first --seed makes it. Exit code 0 once a
// login works, 1 if none did in time, 2 without a connection string.
#:package Microsoft.Data.SqlClient@6.1.7
// The versions the solution resolves (SqlClient alone would bring a Pkcs with known faults).
#:package System.Security.Cryptography.Pkcs@10.0.13

using System.Diagnostics;
using Microsoft.Data.SqlClient;

var seconds = args.Length > 0 && int.TryParse(args[0], out var s) && s > 0 ? s : 300;
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Roster");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings__Roster is not set.");
    return 2;
}

var master = new SqlConnectionStringBuilder(connectionString)
{
    InitialCatalog = "master",
    ConnectTimeout = 5,
    // A pooled connection that failed keeps failing for a while: start afresh each time.
    Pooling = false,
};
var server = master.DataSource;
var clock = Stopwatch.StartNew();

for (var attempt = 1; ; attempt++)
{
    try
    {
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync();
        Console.WriteLine($"SQL Server at {server} accepted a login after {clock.Elapsed.TotalSeconds:0} s.");
        return 0;
    }
    catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
    {
        if (clock.Elapsed.TotalSeconds >= seconds)
        {
            Console.Error.WriteLine($"SQL Server at {server} accepted no login in {seconds} s. Last error: {ex.Message}");
            return 1;
        }
        // The first failure, then every tenth.
        if (attempt % 10 == 1)
            Console.WriteLine($"Waiting for SQL Server at {server} to accept a login: {ex.Message}");
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
}
