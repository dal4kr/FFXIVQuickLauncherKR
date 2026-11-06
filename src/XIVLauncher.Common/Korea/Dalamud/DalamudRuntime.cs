using System.Runtime.InteropServices;
using System.Text.Json;

namespace XIVLauncher.Common.Korea.Dalamud;

internal sealed class DalamudRuntime(DalamudPackageStore store, string privateRoot, IEnumerable<string>? runtimeRoots)
{
    internal async Task<string> EnsureAsync(string binaryDirectory, string? downloadVersion, IProgress<string>? progress, CancellationToken ct)
    {
        var requirements = ReadRequirements(binaryDirectory);
        var roots = runtimeRoots ?? GetInstalledRoots().Append(privateRoot);
        foreach (var root in roots.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            if (IsCompatible(root, requirements)) return root;

        if (!Version.TryParse(downloadVersion, out var version) || version.Major != 10)
            throw new LauncherException("Dalamud에 필요한 .NET 10 Desktop Runtime(x64)을 찾지 못했습니다.");
        progress?.Report("Dalamud용 .NET 런타임을 준비하는 중입니다…");
        var stage = store.CreateStagingDirectory();
        try
        {
            foreach (var (name, prefix) in new[] { ("Runtime", "dotnet-runtime"), ("WindowsDesktop", "windowsdesktop-runtime") })
            {
                var archive = Path.Combine(stage, prefix + ".zip");
                var uri = new Uri($"https://builds.dotnet.microsoft.com/dotnet/{name}/{version}/{prefix}-{version}-win-x64.zip");
                await store.DownloadAsync(uri, archive, ct).ConfigureAwait(false);
                DalamudPackageStore.ExtractArchive(archive, Path.Combine(stage, "runtime"), ct, overwrite: true);
                File.Delete(archive);
            }
            var runtime = Path.Combine(stage, "runtime");
            if (!IsCompatible(runtime, requirements)) throw new LauncherException("다운로드한 .NET 런타임을 확인하지 못했습니다.");
            store.ReplaceDirectory(runtime, privateRoot);
            return privateRoot;
        }
        finally { store.RemoveStagingDirectory(stage); }
    }

    private static Dictionary<string, Version> ReadRequirements(string binaryDirectory)
    {
        var result = new Dictionary<string, Version>(StringComparer.Ordinal);
        foreach (var name in new[] { "Dalamud.runtimeconfig.json", "Dalamud.Injector.runtimeconfig.json" })
        {
            var path = Path.Combine(binaryDirectory, name);
            if (!File.Exists(path)) continue;
            using var config = JsonDocument.Parse(File.ReadAllBytes(path));
            var options = config.RootElement.GetProperty("runtimeOptions");
            var frameworks = new List<JsonElement>();
            if (options.TryGetProperty("framework", out var single)) frameworks.Add(single);
            if (options.TryGetProperty("frameworks", out var multiple)) frameworks.AddRange(multiple.EnumerateArray());
            foreach (var framework in frameworks)
            {
                var frameworkName = framework.GetProperty("name").GetString()!;
                var version = Version.Parse(framework.GetProperty("version").GetString()!);
                if (frameworkName is not ("Microsoft.NETCore.App" or "Microsoft.WindowsDesktop.App") || version.Major != 10)
                    throw new LauncherException("이 Dalamud 빌드의 런타임 요구 사항을 지원하지 않습니다.");
                if (!result.TryGetValue(frameworkName, out var current) || version > current) result[frameworkName] = version;
            }
        }
        if (result.Count == 0) throw new LauncherException("Dalamud 런타임 설정을 읽지 못했습니다.");
        return result;
    }

    private static bool IsCompatible(string root, Dictionary<string, Version> requirements) =>
        HasVersion(Path.Combine(root, "host", "fxr"), new Version(10, 0, 0), "hostfxr.dll") &&
        requirements.All(requirement => HasVersion(Path.Combine(root, "shared", requirement.Key), requirement.Value,
            requirement.Key == "Microsoft.NETCore.App" ? "coreclr.dll" : "PresentationFramework.dll"));

    private static bool HasVersion(string directory, Version minimum, string requiredFile) =>
        Directory.Exists(directory) && Directory.EnumerateDirectories(directory).Any(path =>
            Version.TryParse(Path.GetFileName(path), out var candidate) && candidate.Major == minimum.Major &&
            candidate >= minimum && File.Exists(Path.Combine(path, requiredFile)));
    private static IEnumerable<string> GetInstalledRoots()
    {
        var active = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar))?.Parent?.Parent?.FullName;
        if (active is not null) yield return active;
        foreach (var name in new[] { "DOTNET_ROOT_X64", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) yield return value;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    }
}
