using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Packo.Models.SQLite;
using Packo.Services;
using Packo.Services.Interfaces;
using SQLite;
using Supabase.Gotrue;

var count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    count++;
}
async Task MustFail(Func<Task> action)
{
    try { await action(); }
    catch { return; }
    throw new Exception("Expected failure");
}

var dbPath = Path.Combine(Path.GetTempPath(), "packo-account-delete-" + Guid.NewGuid() + ".db3");
var db = new SQLiteAsyncConnection(dbPath);
using var server = new AccountServer();
try
{
    await db.CreateTableAsync<SQLiteUser>();
    var local = new LocalUserService(db);
    var remote = new Remote(server.Url);
    await remote.Client.InitializeAsync();
    // The real Auth transport logs into the loopback fixture; no live Supabase calls.
    var login = await remote.Client.Auth.SignIn("test@example.com", "test-password");
    var session = new SessionService(remote, local);
    await session.SetAuthenticatedUserAsync(login!.User!);
    var localId = session.LocalUserId!;
    Check(session.IsAuthenticated, "The real SDK establishes an authenticated account");
    var other = await local.GetOrCreateAccountAsync(Guid.NewGuid().ToString());
    var guest = await local.GetOrCreateLocalUserAsync();

    var scopedTables = new[] { "SQLitePendingPackingItemChange", "SQLitePendingTripChange",
        "SQLiteTripReminder", "SQLiteReminderPreference", "SQLiteTripPlaces", "SQLitePackingItem", "SQLitePackingList", "SQLiteTrip" };
    foreach (var table in scopedTables)
    {
        await db.ExecuteAsync($"CREATE TABLE {table}(LocalUserId TEXT, Data TEXT)");
        foreach (var id in new[] { localId, guest.LocalUserId, other.LocalUserId })
            await db.ExecuteAsync($"INSERT INTO {table} VALUES(?, 'cached data')", id);
    }

    Connectivity.Current.NetworkAccess = NetworkAccess.None;
    await MustFail(() => session.DeleteAccountAsync());
    Check(server.DeleteRequests == 0 && session.UserId == server.UserId && SecureStorage.Values.Count == 3,
        "Offline deletion sends no request and retains the session and credentials");
    Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
    server.FailDeletion = true;
    await MustFail(() => session.DeleteAccountAsync());
    Check(session.IsAuthenticated && SecureStorage.Values.Count == 3
        && await db.ExecuteScalarAsync<int>("SELECT count(*) FROM SQLiteTrip WHERE LocalUserId=?", localId) == 1,
        "A failed RPC preserves login and local trip data for retry");

    server.FailDeletion = false;
    server.PauseDeletion = true;
    var deletion = session.DeleteAccountAsync();
    await server.DeletionReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(!deletion.IsCompleted && session.IsAuthenticated && SecureStorage.Values.Count == 3,
        "Credentials stay available until the delete request is acknowledged");
    Check(server.Authorization == "Bearer " + server.AccessToken, "The RPC carries the actual session JWT");
    Check(string.IsNullOrWhiteSpace(server.DeleteBody) || server.DeleteBody is "{}" or "null",
        "The RPC accepts no arbitrary user ID");
    server.AllowDeletion.TrySetResult();
    await deletion;
    Check(session.UserId is null && session.User is null && !session.IsAuthenticated
        && remote.Client.Auth.CurrentSession is null && session.LocalUserId == guest.LocalUserId,
        "A successful deletion clears the real SDK session and selects the guest account");
    Check(SecureStorage.Values.Count == 0 && server.SignOutRequests == 0,
        "All persisted credentials disappear without a failing remote sign-out request");
    await session.TryRecoverSessionAsync();
    Check(session.UserId is null, "Session recovery cannot restore the deleted account");

    await local.DeleteAccountDataAsync(server.UserId);
    foreach (var table in scopedTables)
        Check(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE LocalUserId=?", localId) == 0
            && await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table}") == 2,
            $"{table} clears only the deleted account, retaining guest and other-account rows");
    Check(await db.Table<SQLiteUser>().Where(x => x.SupabaseUserId == server.UserId).CountAsync() == 0,
        "The deleted account's local profile disappears");
    await local.DeleteAccountDataAsync(server.UserId);
    Check(await db.Table<SQLiteUser>().CountAsync() == 2, "Repeated local cleanup is safe");
    Console.WriteLine($"{count} account deletion app checks passed.");
}
finally
{
    await db.CloseAsync();
    File.Delete(dbPath);
}

sealed class Remote(string url) : ISupabaseService
{
    public Supabase.Client Client { get; } = new(url, "fixture-anon-key", new Supabase.SupabaseOptions
        { AutoConnectRealtime = false, AutoRefreshToken = false });
    public Task InitializeAsync() => Client.InitializeAsync();
    public Task EnsureRealtimeConnectedAsync() => Task.CompletedTask;
}

sealed class AccountServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public string Url { get; }
    public string UserId { get; } = Guid.NewGuid().ToString();
    public string AccessToken { get; }
    public bool FailDeletion { get; set; }
    public bool PauseDeletion { get; set; }
    public int DeleteRequests { get; private set; }
    public int SignOutRequests { get; private set; }
    public string? Authorization { get; private set; }
    public string? DeleteBody { get; private set; }
    public TaskCompletionSource DeletionReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowDeletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AccountServer()
    {
        _listener.Start();
        var number = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://127.0.0.1:{number}";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { sub = UserId, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        AccessToken = "eyJhbGciOiJIUzI1NiJ9." + payload + ".fixture";
        _ = ListenAsync();
    }
    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = socket.GetStream();
                using var headerBytes = new MemoryStream();
                var one = new byte[1];
                while (headerBytes.Length < 64 * 1024)
                {
                    await stream.ReadExactlyAsync(one, _stop.Token);
                    headerBytes.WriteByte(one[0]);
                    if (headerBytes.Length >= 4 && Encoding.ASCII.GetString(headerBytes.GetBuffer(), (int)headerBytes.Length - 4, 4) == "\r\n\r\n") break;
                }
                var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n");
                var headers = lines.Skip(1).Where(x => x.Contains(':')).ToDictionary(
                    x => x[..x.IndexOf(':')], x => x[(x.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
                var bodyBytes = new byte[int.Parse(headers.GetValueOrDefault("Content-Length", "0"))];
                await stream.ReadExactlyAsync(bodyBytes, _stop.Token);
                var path = lines[0].Split(' ')[1].Split('?')[0];
                var status = 200;
                string response;
                if (path == "/auth/v1/token")
                    response = JsonSerializer.Serialize(new { access_token = AccessToken, refresh_token = "fixture-refresh",
                        expires_in = 3600, token_type = "bearer", user = new { id = UserId, email = "test@example.com",
                            confirmed_at = DateTimeOffset.UtcNow } });
                else if (path == "/rest/v1/rpc/delete_my_account")
                {
                    DeleteRequests++;
                    Authorization = headers.GetValueOrDefault("Authorization");
                    DeleteBody = Encoding.UTF8.GetString(bodyBytes);
                    if (PauseDeletion) { DeletionReceived.TrySetResult(); await AllowDeletion.Task; }
                    status = FailDeletion ? 500 : 200;
                    response = FailDeletion ? "{\"message\":\"transaction failed\",\"code\":\"XX000\"}" : "true";
                }
                else
                {
                    if (path.Contains("logout")) SignOutRequests++;
                    status = 404;
                    response = "{}";
                }
                var bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(bytes, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
}
