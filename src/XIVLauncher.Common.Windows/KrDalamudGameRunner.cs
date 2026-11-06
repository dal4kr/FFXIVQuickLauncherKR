using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using XIVLauncher.Common.Korea;
using XIVLauncher.Common.Korea.Dalamud;

namespace XIVLauncher.Common.Windows;

public sealed class KrDalamudGameRunner
{
    public async Task<Process> StartAsync(GameLaunchPlan game, PreparedDalamud dalamud, DalamudOptions options,
        CancellationToken cancellationToken = default)
    {
        var start = CreateStartInfo(dalamud, options,
            ["launch", options.LoadMethod == DalamudLoadMethod.EntryPoint ? "--mode=entrypoint" : "--mode=inject", "--game=" + game.ExecutablePath]);
        start.ArgumentList.Add("--");
        foreach (var argument in game.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            start.ArgumentList.Add(argument);

        var output = await RunInjectorAsync(start, cancellationToken).ConfigureAwait(false);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var result = JsonDocument.Parse(line);
                if (result.RootElement.TryGetProperty("pid", out var id) && id.TryGetInt32(out var pid) && pid > 0)
                    return Process.GetProcessById(pid);
            }
            catch (JsonException) { }
            catch (ArgumentException) { throw new LauncherException("게임 프로세스가 실행 직후 종료되었습니다."); }
        }
        throw new LauncherException("KR Dalamud Injector에서 게임 실행 결과를 받지 못했습니다. 게임 창과 Dalamud 로그를 확인해 주세요.");
    }

    public async Task InjectAsync(Process game, PreparedDalamud dalamud, DalamudOptions options,
        CancellationToken cancellationToken = default)
    {
        GetInjectionTargetPath(game);
        var start = CreateStartInfo(dalamud, options, ["inject", game.Id.ToString(CultureInfo.InvariantCulture), "--fix-acl"]);
        await RunInjectorAsync(start, cancellationToken).ConfigureAwait(false);
    }

    public static string GetInjectionTargetPath(Process game)
    {
        try
        {
            game.Refresh();
            if (game.HasExited) throw new LauncherException("선택한 게임이 종료되었습니다. 게임을 실행한 뒤 다시 연결해 주세요.");
            if (!string.Equals(game.ProcessName, "ffxiv_dx11", StringComparison.OrdinalIgnoreCase))
                throw new LauncherException("FFXIV 게임 프로세스에만 Dalamud를 연결할 수 있습니다.");
            var path = game.MainModule?.FileName
                ?? throw new LauncherException("게임 실행 경로를 확인하지 못했습니다.");
            if (game.Modules.Cast<ProcessModule>().Any(module =>
                string.Equals(module.ModuleName, "Dalamud.Boot.dll", StringComparison.OrdinalIgnoreCase)))
                throw new LauncherException("이미 Dalamud가 연결된 게임입니다. 다시 연결하려면 게임을 재시작해 주세요.");
            return path;
        }
        catch (Win32Exception)
        {
            throw new LauncherException("게임 프로세스에 접근하지 못했습니다. 게임이 관리자 권한으로 실행 중이라면 런처도 관리자 권한으로 실행해 주세요.");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            throw new LauncherException("선택한 게임의 상태를 확인하지 못했습니다. 게임 실행 상태를 확인하고 다시 시도해 주세요.");
        }
    }

    private static ProcessStartInfo CreateStartInfo(PreparedDalamud dalamud, DalamudOptions options, IEnumerable<string> commandArguments)
    {
        var start = new ProcessStartInfo(dalamud.InjectorPath)
        {
            WorkingDirectory = dalamud.BinaryDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in commandArguments) start.ArgumentList.Add(argument);
        foreach (var argument in new[]
        {
            "--dalamud-working-directory=" + dalamud.BinaryDirectory,
            "--dalamud-configuration-path=" + Path.Combine(dalamud.UserDirectory, "dalamudConfig.json"),
            "--dalamud-plugin-directory=" + Path.Combine(dalamud.UserDirectory, "installedPlugins"),
            "--dalamud-asset-directory=" + dalamud.AssetDirectory,
            "--dalamud-temp-directory=" + Path.Combine(dalamud.UserDirectory, "temp"),
            "--logpath=" + dalamud.UserDirectory,
            "--dalamud-client-language=korean",
            "--dalamud-delay-initialize=" + Math.Clamp(options.InjectionDelayMs, 0, 60000),
        }) start.ArgumentList.Add(argument);
        if (options.NoPlugins) start.ArgumentList.Add("--no-plugin");
        start.Environment["DALAMUD_RUNTIME"] = dalamud.RuntimeDirectory;
        start.Environment["DOTNET_ROOT"] = dalamud.RuntimeDirectory;
        start.Environment["DOTNET_ROOT_X64"] = dalamud.RuntimeDirectory;
        start.Environment["__COMPAT_LAYER"] = "RunAsInvoker DPIUnaware";
        return start;
    }

    private static async Task<string> RunInjectorAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var injector = Process.Start(start) ?? throw new LauncherException("KR Dalamud Injector를 시작하지 못했습니다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var stdout = injector.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = injector.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await injector.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            if (injector.ExitCode != 0 || errors.Contains("Dalamud.Boot::Initialize returned", StringComparison.Ordinal))
                throw new LauncherException($"KR Dalamud 연결에 실패했습니다(Injector 종료 코드: {injector.ExitCode}). 게임 창과 Dalamud 로그를 확인해 주세요.");
            return output;
        }
        catch (OperationCanceledException)
        {
            try { if (!injector.HasExited) injector.Kill(); }
            catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new LauncherException("KR Dalamud 연결 시간이 초과되었습니다. 게임 창과 Dalamud 로그를 확인해 주세요.");
        }
    }
}
