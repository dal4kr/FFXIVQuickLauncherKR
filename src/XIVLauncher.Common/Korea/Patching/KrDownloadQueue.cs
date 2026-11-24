namespace XIVLauncher.Common.Korea.Patching;

public sealed partial class KrGameUpdater
{
    /// <summary>Uses the official checkbox's four file-download workers. Captured when an update starts; file and patch application stays ordered.</summary>
    public bool UnlockDownloadBandwidth { get; set; }

    private sealed record DownloadRequest(Uri Url, long Size, string? Md5, PatchItem? Patch, string Name);

    /// <summary>Keeps at most four verified-file downloads ahead of the ordered application loop.</summary>
    private sealed class DownloadQueue(KrGameUpdater updater, string root, UpdatePlan plan, TransferState state,
                                       CancellationToken token) : IAsyncDisposable
    {
        private readonly int concurrency = updater.UnlockDownloadBandwidth ? 4 : 1;
        private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        private readonly IEnumerator<DownloadRequest> remaining = plan.Files
            .Select(file => new DownloadRequest(file.Url, file.Size, file.Md5, null, file.RelativePath))
            .Concat(plan.Patches.Select(patch => new DownloadRequest(patch.Url, patch.Size, null, patch, patch.Version)))
            .GetEnumerator();
        private readonly Queue<Task<string>> pending = new();

        internal async Task<string> NextAsync()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            while (pending.Count < concurrency && remaining.MoveNext())
            {
                var item = remaining.Current;
                pending.Enqueue(updater.DownloadAsync(root, item.Url, item.Size, item.Md5, item.Patch, state, item.Name, cancellation.Token));
            }
            return await pending.Dequeue().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* Preserve the download/application error while observing every worker. */ }
            finally { remaining.Dispose(); cancellation.Dispose(); }
        }
    }
}
