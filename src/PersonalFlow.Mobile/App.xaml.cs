using PersonalFlow.Mobile.Reminders;

namespace PersonalFlow.Mobile;

public partial class App : Application
{
	private readonly ReminderCoordinator reminders;
	private readonly MobileBackgroundSync backgroundSync;

	public App(ReminderCoordinator reminders, MobileBackgroundSync backgroundSync)
	{
		this.reminders = reminders;
		this.backgroundSync = backgroundSync;
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "PersonalFlow" };
		window.Resumed += (_, _) =>
		{
			reminders.OnResume();
			_ = backgroundSync.TrySynchronizeAsync();
		};
		_ = backgroundSync.TrySynchronizeAsync();
		return window;
	}
}
