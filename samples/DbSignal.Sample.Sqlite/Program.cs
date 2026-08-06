using DbSignal;
using DbSignal.Sqlite;
using Microsoft.Data.Sqlite;

// A live demonstration of the one thing this library exists for: noticing a write your
// application did not make.
//
//   dotnet run --project samples/DbSignal.Sample.Sqlite
//
// Leave it running, open the printed .db file in DB Browser for SQLite (or any tool),
// change a row, press "Write Changes" — the console reacts without a restart.
//
// Pass --simulate to have the sample write to itself from a second connection, so the
// whole thing is visible without installing anything.

var dbPath = Path.Combine(AppContext.BaseDirectory, "sample.db");
var connectionString = $"Data Source={dbPath}";
var simulate = args.Contains("--simulate", StringComparer.OrdinalIgnoreCase);

CreateSchemaIfMissing(connectionString);

Console.WriteLine("DbSignal — SQLite sample");
Console.WriteLine(new string('─', 60));
Console.WriteLine($"Watching : {dbPath}");

await using var feed = SqliteFeed.For(connectionString)
                                 .PollEvery(TimeSpan.FromMilliseconds(250))
                                 .Build();

Console.WriteLine($"Provider : {feed.ProviderName}");
Console.WriteLine($"Detail   : {feed.Capabilities.Detail}  " +
                  $"(this engine cannot name the table — one counter for the whole file)");
Console.WriteLine($"Own writes surface: {feed.Capabilities.FiltersOwnWrites is false}");
Console.WriteLine(new string('─', 60));
Console.WriteLine(simulate
    ? "Simulating an external writer every 2s. Ctrl+C to stop."
    : "Open the file in another tool and save a change. Ctrl+C to stop.");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (simulate)
{
    _ = SimulateExternalWriterAsync(connectionString, cts.Token);
}

var seen = 0;
await foreach (var batch in feed.ReadAsync(Checkpoint.Now, cts.Token))
{
    seen++;
    Console.WriteLine($"[{batch.ObservedUtc.ToLocalTime():HH:mm:ss}] " +
                      $"change #{seen} detected — position {batch.Position}");

    // DatabaseChanged tells us *that* something moved, not *what*. So we re-read.
    // On SQL Server the same handler would receive the changed keys and re-read only those.
    foreach (var name in ReadProductNames(connectionString))
    {
        Console.WriteLine($"          · {name}");
    }
}

Console.WriteLine();
Console.WriteLine($"Stopped after detecting {seen} change(s).");

static void CreateSchemaIfMissing(string connectionString)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText =
        "CREATE TABLE IF NOT EXISTS Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);";
    command.ExecuteNonQuery();
}

static List<string> ReadProductNames(string connectionString)
{
    var names = new List<string>();
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT Name FROM Products ORDER BY Id DESC LIMIT 5;";
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        names.Add(reader.GetString(0));
    }
    return names;
}

// Stands in for the ERP job, the script, or the person with a database GUI open.
// A separate connection — nothing shared with the feed.
static async Task SimulateExternalWriterAsync(string connectionString, CancellationToken ct)
{
    var n = 0;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Products (Name) VALUES ($name);";
        command.Parameters.AddWithValue("$name", $"Widget {++n} @ {DateTime.Now:HH:mm:ss}");
        await command.ExecuteNonQueryAsync(ct);
    }
}
