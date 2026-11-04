using System.Globalization;
using System.Text.Json;
using Reserve.Api;
using StackExchange.Redis;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (args.Length == 2 && args[0] == "generate")
{
    var random = new Random(340);
    UserAccount[] users = Enumerable.Range(1, 100_000)
        .Select(id => new UserAccount(id, random.Next(0, 1_000_000)))
        .ToArray();
    string output = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    string temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(users, jsonOptions) + "\n");
        File.Move(temporary, output, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }
    Console.WriteLine($"Saved {users.Length:N0} users to {output}; IDs 1..100000, balances 0..999999 JPY, seed 340.");
    return 0;
}
if (args.Length is 3 or 4 && args[0] == "seed-redis")
{
    var data = await new FileUserRepository(Path.GetFullPath(args[1])).LoadAsync(CancellationToken.None);
    // Validate before touching Redis.
    _ = ReserveSnapshot.Build(data, CancellationToken.None);
    string connectionString = args[2];
    string manifestKey = args.Length == 4 ? args[3] : "por:current";
    string version = Guid.NewGuid().ToString("N");
    string key = "por:users:" + version;
    using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
    var db = connection.GetDatabase();
    foreach (var batch in data.Users.Chunk(1000))
    {
        await db.HashSetAsync(key, batch.Select(user => new HashEntry(
            user.Id.ToString(CultureInfo.InvariantCulture),
            user.Balance.ToString(CultureInfo.InvariantCulture))).ToArray());
    }
    var manifest = new RedisManifest(version, key, data.Users.Length);
    // Publish one pointer only after the entire immutable version has been written.
    await db.StringSetAsync(manifestKey, JsonSerializer.Serialize(manifest, jsonOptions));
    Console.WriteLine($"Published {data.Users.Length:N0} users as {key}; pointer: {manifestKey}.");
    return 0;
}
Console.Error.WriteLine("Usage: ReserveData generate <output.json> | seed-redis <input.json> <connection> [manifest-key]");
return 1;
