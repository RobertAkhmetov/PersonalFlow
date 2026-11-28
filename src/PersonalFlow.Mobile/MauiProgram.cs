using Microsoft.Extensions.Logging;
using PersonalFlow.Mobile.Reminders;

namespace PersonalFlow.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton(new PersonalFlow.Client.LocalStore(Path.Combine(FileSystem.AppDataDirectory, "personalflow.db")));
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        builder.Services.AddSingleton<MobileIdentitySession>();
        builder.Services.AddSingleton<MobileSyncCoordinator>();
        builder.Services.AddSingleton<MobileBackgroundSync>();
        builder.Services.AddSingleton<PrivateSeedImporter>();
#if ANDROID
        builder.Services.AddSingleton<IReminderPlatform, AndroidReminderPlatform>();
#elif IOS
        builder.Services.AddSingleton<IReminderPlatform, IosReminderPlatform>();
#endif
        builder.Services.AddSingleton<ReminderCoordinator>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
