namespace CommandCenter.Core.Settings
{
    public enum AnimationSetting { FollowWindows, On, Off }

    // Appearance (ticket 08): Home's picture, animation effects and interface sounds, kept with the other settings. The
    // window asks AnimationsOn before anything moves and listens to Changed.
    public interface IAppearance
    {
        HomeImage HomeImage { get; set; }
        AnimationSetting Animations { get; set; }
        bool InterfaceSounds { get; set; }

        // The setting, or Windows' "Show animations" while it follows Windows
        bool AnimationsOn { get; }

        event Action? Changed;
    }

    // The instance form over AppSettings, created once in AppServices with Windows' animation setting
    public sealed class Appearance(Func<bool> windowsAnimations) : IAppearance
    {
        public event Action? Changed;

        public HomeImage HomeImage
        {
            get => AppSettings.Current.HomeImage;
            set => Set(() => AppSettings.Current.HomeImage = value);
        }

        public AnimationSetting Animations
        {
            get => AppSettings.Current.Animations;
            set => Set(() => AppSettings.Current.Animations = value);
        }

        public bool InterfaceSounds
        {
            get => AppSettings.Current.InterfaceSounds;
            set => Set(() => AppSettings.Current.InterfaceSounds = value);
        }

        public bool AnimationsOn => Animations switch
        {
            AnimationSetting.On => true,
            AnimationSetting.Off => false,
            _ => windowsAnimations(),
        };

        private void Set(Action change)
        {
            change();
            AppSettings.Current.Save();
            Changed?.Invoke();
        }
    }
}
