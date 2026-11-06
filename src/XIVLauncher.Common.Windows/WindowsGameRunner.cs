using System.Diagnostics;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Korea;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Windows;

public sealed class WindowsGameRunner : IGameRunner
{
    public Process Start(string path, string workingDirectory, string arguments, IDictionary<string,string> environment, DpiAwareness dpiAwareness, string additionalInjectorArgs)
        => NativeAclFix.LaunchGame(workingDirectory, path, arguments, environment, dpiAwareness, _ => { });

    // Let Windows scale the saved game window size, matching the upstream default.
    public Process Start(GameLaunchPlan plan)
        => Start(plan.ExecutablePath, plan.WorkingDirectory, plan.Arguments, new Dictionary<string,string>(), DpiAwareness.Unaware, "");
}
