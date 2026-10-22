using CommunityToolkit.Maui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Shapes;
using MPowerKit.Lottie;
using Packo.Interfaces;
using Packo.Repositories;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Local;
using Packo.Repositories.Supabase;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.ViewModels;
using Packo.Views;
using SQLite;
using Syncfusion.Maui.Core.Hosting;
using System.Reflection;

namespace Packo
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkit(options =>
                {
                    options.SetPopupDefaults(new DefaultPopupSettings
                    {
                        BackgroundColor = Colors.Transparent
                    });
                    options.SetPopupOptionsDefaults(new DefaultPopupOptionsSettings
                    {
                        Shape = new RoundRectangle
                        {
                            CornerRadius = new CornerRadius(0),
                            Stroke = Colors.Transparent,
                            StrokeThickness = 0
                        },
                        Shadow = null
                    });
                })
                .ConfigureSyncfusionCore()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
                })
                .UseMPowerKitLottie()
                .ConfigureMauiHandlers(handlers =>
                {
#if ANDROID
                    handlers.AddHandler(typeof(AppShell), typeof(Platforms.Android.CustomShellRenderer));
#endif
                });

            Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping("NoUnderline", (handler, view) =>
            {
#if ANDROID
                handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#endif
            });

            Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, view) =>
            {
#if ANDROID
                handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#endif
            });

            Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("NoUnderline", (handler, view) =>
            {
#if ANDROID
                handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#endif
            });


            Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense("Ngo9BigBOggjGyl/Vkd+XU9FcVRDX3xKf0x/TGpQb19xflBPallYVBYiSV9jS3tSd0RrWHpccndWR2BaUE91Xg==");

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Packo.appsettings.json");
            var config = new ConfigurationBuilder()
                .AddJsonStream(stream!)
                .Build();
            builder.Configuration.AddConfiguration(config);

            builder.Services.AddSingleton<ISupabaseService, SupabaseService>();
            builder.Services.AddSingleton<ISessionService, SessionService>();

            // === Repositories ===
            builder.Services.AddSingleton<LocalTripRepository>();
            builder.Services.AddSingleton<SupabaseTripRepository>();

            builder.Services.AddSingleton<LocalPackingItemRepository>();
            builder.Services.AddSingleton<SupabasePackingItemRepository>();

            builder.Services.AddSingleton(sp =>
            {
                var dbPath = System.IO.Path.Combine(FileSystem.AppDataDirectory, "app.db3");
                return new SQLiteAsyncConnection(dbPath);
            });

            builder.Services.AddSingleton<ILocalUserService, LocalUserService>();


            builder.Services.AddSingleton<ITripRepository>(sp =>
            {
                var local = sp.GetRequiredService<LocalTripRepository>();
                var remote = sp.GetRequiredService<SupabaseTripRepository>();
                var session = sp.GetRequiredService<ISessionService>();
                var pendingDb = sp.GetRequiredService<SQLiteAsyncConnection>();

                return new SyncTripRepository(local, remote, session, pendingDb);
            });
            builder.Services.AddSingleton<ITripListPreloadService, TripListPreloadService>();
            builder.Services.AddSingleton<IDocumentPreloadService, DocumentPreloadService>();

            builder.Services.AddSingleton<IPackingItemRepository>(sp =>
            {
                var local = sp.GetRequiredService<LocalPackingItemRepository>();
                var remote = sp.GetRequiredService<SupabasePackingItemRepository>();
                var session = sp.GetRequiredService<ISessionService>();
                var pendingDb = sp.GetRequiredService<SQLiteAsyncConnection>();
                var tripRepo = sp.GetRequiredService<ITripRepository>();
                return new SyncPackingItemRepository(local, remote, session, pendingDb, tripRepo);
            });

            // === UI ===
            builder.Services.AddTransient<StartPage>();
            builder.Services.AddTransient<TripListPage>();
            builder.Services.AddTransient<TripSetupPage>();
            builder.Services.AddTransient<PackingListPage>();
            builder.Services.AddTransient<WeatherPage>();
            builder.Services.AddTransient<DocsPage>();
            builder.Services.AddTransient<SettingsPage>();
            builder.Services.AddSingleton<ShellHeaderView>();

            builder.Services.AddTransient<StartViewModel>();
            builder.Services.AddTransient<TripSetupViewModel>();
            builder.Services.AddTransient<TripListViewModel>();
            builder.Services.AddTransient<PackingListViewModel>();
            builder.Services.AddTransient<WeatherViewModel>();
            builder.Services.AddTransient<DocsViewModel>();
            builder.Services.AddTransient<SettingsViewModel>();
            builder.Services.AddSingleton<ShellHeaderViewModel>();

            //builder.Services.AddScoped<LocalTripRepository>();
            //builder.Services.AddScoped<SupabaseTripRepository>();

            //builder.Services.AddScoped<LocalPackingItemRepository>();
            //builder.Services.AddScoped<SupabasePackingItemRepository>();

            //builder.Services.AddSingleton(sp =>
            //{
            //    var dbPath = Path.Combine(FileSystem.AppDataDirectory, "app.db3");
            //    return new SQLiteAsyncConnection(dbPath);
            //});

            //builder.Services.AddScoped<IPackingItemRepository>(sp =>
            //{
            //    var local = sp.GetRequiredService<LocalPackingItemRepository>();
            //    var remote = sp.GetRequiredService<SupabasePackingItemRepository>();
            //    var pendingDb = sp.GetRequiredService<SQLiteAsyncConnection>();
            //    return new SyncPackingItemRepository(local, remote, pendingDb);
            //});


            //builder.Services.AddScoped<ITripRepository>(sp =>
            //{
            //    var local = sp.GetRequiredService<LocalTripRepository>();
            //    var remote = sp.GetRequiredService<SupabaseTripRepository>();
            //    var pendingDb = sp.GetRequiredService<SQLiteAsyncConnection>();
            //    return new SyncTripRepository(local, remote, pendingDb);
            //});

            //builder.Services.AddTransient<StartPage>();
            //builder.Services.AddTransient<TripListPage>();
            //builder.Services.AddTransient<PackingListPage>();

            //builder.Services.AddTransient<StartViewModel>();
            //builder.Services.AddTransient<TripListViewModel>();
            //builder.Services.AddTransient<PackingListViewModel>();

            builder.Services.AddSingleton<HttpClient>();
            builder.Services.AddSingleton<IAIService, AIService>();
            builder.Services.AddSingleton<IPackingSuggestionService, PackingSuggestionService>();
            builder.Services.AddSingleton<ITripClassificationService, TripClassificationService>();
            builder.Services.AddHttpClient<WeatherService>();
            builder.Services.AddSingleton<IWeatherPreloadService, WeatherPreloadService>();

            //builder.Services.AddHttpClient<IAIService, AIService>(client =>
            //{
            //    client.Timeout = TimeSpan.FromSeconds(10);
            //});
            //builder.Services.AddSingleton<IPackingSuggestionService, PackingSuggestionService>();

#if ANDROID
            builder.Services.AddSingleton<IGoogleAuthService, Packo.Platforms.Android.GoogleAuthService>();
#endif

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
