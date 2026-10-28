namespace VaultMcp.Core;

/// <summary>
/// The parsed notes of the allow-listed folders, rebuilt from the store when older than <see cref="Ttl"/>.
/// Only allow-listed files are ever read, so the cost is bounded by the owner's list, not the vault.
/// </summary>
public sealed class VaultIndex
{
    public sealed record Snapshot(VaultConfig Config, IReadOnlyList<VaultNote> Notes, DateTimeOffset BuiltAt);

    private readonly IVaultStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _snapshot;

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(15);

    public TimeSpan Ttl { get; }

    public VaultIndex(IVaultStore store, TimeProvider? time = null, TimeSpan? ttl = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
        Ttl = ttl ?? DefaultTtl;
    }

    public async Task<Snapshot> GetAsync(CancellationToken ct)
    {
        var current = _snapshot;
        if (current is not null && IsFresh(current)) return current;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _snapshot;
            if (current is not null && IsFresh(current)) return current;
            current = await BuildAsync(ct).ConfigureAwait(false);
            _snapshot = current;
            return current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _snapshot = null;

    private bool IsFresh(Snapshot s) => _time.GetUtcNow() - s.BuiltAt < Ttl;

    private async Task<Snapshot> BuildAsync(CancellationToken ct)
    {
        var config = VaultConfig.Parse(await _store.ReadAsync(VaultConfig.FileName, ct).ConfigureAwait(false));
        var notes = new List<VaultNote>();
        if (config.AllowedFolders.Count > 0)
        {
            foreach (var file in await _store.ListAsync(ct).ConfigureAwait(false))
            {
                if (!config.IsAllowed(file.Path)) continue;
                var text = await _store.ReadAsync(file.Path, ct).ConfigureAwait(false);
                if (text is null) continue;
                notes.Add(NoteParser.Parse(file, text));
            }
        }
        return new Snapshot(config, notes, _time.GetUtcNow());
    }
}
