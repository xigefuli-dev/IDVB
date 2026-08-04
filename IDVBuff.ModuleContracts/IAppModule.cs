namespace IDVBuff.ModuleContracts;

/// <summary>
/// Framework-neutral boundary between the shell and a feature project.
/// A WinUI module should return a FrameworkElement from <see cref="CreateView"/>.
/// </summary>
public interface IAppModule
{
    string Id { get; }
    string DisplayName { get; }
    string IconKey { get; }
    object CreateView();
}
