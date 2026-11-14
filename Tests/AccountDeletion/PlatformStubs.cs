// Only device APIs are substituted. Session, SQLite and Supabase SDK are production code.
public enum NetworkAccess { None, Internet }
public sealed class ConnectivityChangedEventArgs : EventArgs
{
    public NetworkAccess NetworkAccess { get; init; }
}
public sealed class Connectivity
{
    public static Connectivity Current { get; } = new();
    public NetworkAccess NetworkAccess { get; set; } = NetworkAccess.Internet;
    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;
}
public static class MainThread
{
    public static Task InvokeOnMainThreadAsync(Action action) { action(); return Task.CompletedTask; }
}
public static class SecureStorage
{
    public static Dictionary<string, string> Values { get; } = [];
    public static Task SetAsync(string key, string value) { Values[key] = value; return Task.CompletedTask; }
    public static Task<string?> GetAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));
    public static bool Remove(string key) => Values.Remove(key);
}
