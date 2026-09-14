namespace NetTrace.Core.Abstractions;

/// <summary>
/// PID to friendly name. Icon loading deliberately lives in the UI layer — it needs
/// PresentationCore, and Core has no business depending on WPF.
/// </summary>
public interface IProcessResolver
{
    (string Name, string? Path) Resolve(int pid);
}
