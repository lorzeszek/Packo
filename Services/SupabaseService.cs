using Packo.Services.Interfaces;

namespace Packo.Services
{
    public class SupabaseService : ISupabaseService
    {
        public const string ProjectUrl = "https://dahwafdelpczzbgvlelc.supabase.co";
        internal static string PublicAnonKey { get; private set; } = string.Empty;
        private readonly Supabase.Client _client;

        public Supabase.Client Client => _client;

        public SupabaseService()
        {
            var url = ProjectUrl;
            var apiKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImRhaHdhZmRlbHBjenpiZ3ZsZWxjIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTQwNTgzMTYsImV4cCI6MjA2OTYzNDMxNn0.vVAFp0wKgaM5804ITEcAph8jp2G-laGOngdiY2Qkfl0";

            PublicAnonKey = apiKey;
            _client = new Supabase.Client(url, apiKey, new Supabase.SupabaseOptions
            {
                AutoConnectRealtime = false,
            });
        }

        public async Task InitializeAsync()
        {
            await _client.InitializeAsync(); // bardzo ważne
        }

        public async Task EnsureRealtimeConnectedAsync()
        {
            if (_client.Realtime.Socket != null && _client.Realtime.Socket.IsConnected)
                return;
            await _client.Realtime.ConnectAsync();
        }
    }
}
