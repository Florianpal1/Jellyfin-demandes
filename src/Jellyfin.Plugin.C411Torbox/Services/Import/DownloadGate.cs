using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.C411Torbox.Services.Import;

/// <summary>
/// Limite le nombre de téléchargements simultanés, tous imports confondus. Contrairement à un
/// <see cref="SemaphoreSlim"/>, la limite est relue à chaque entrée : un changement de configuration
/// s'applique sans redémarrage (les téléchargements en cours vont à leur terme). Ordre d'arrivée respecté.
/// </summary>
public sealed class DownloadGate
{
    private readonly object _sync = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    private readonly Func<int> _limit;
    private int _active;

    public DownloadGate(Func<int> limit)
    {
        _limit = limit;
    }

    /// <summary>Attend une place libre ; la place est rendue en disposant le résultat.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        TaskCompletionSource tcs;
        LinkedListNode<TaskCompletionSource> node;
        lock (_sync)
        {
            // La limite a pu être relevée depuis : on sert d'abord ceux qui attendent.
            Wake();
            if (_waiters.Count == 0 && _active < Limit())
            {
                _active++;
                return new Slot(this);
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(tcs);
        }

        using (ct.Register(() =>
        {
            lock (_sync)
            {
                // Déjà servi : la place est à nous, elle sera rendue par l'appelant ci-dessous.
                if (node.List is null)
                {
                    return;
                }

                _waiters.Remove(node);
            }

            tcs.TrySetCanceled(ct);
        }))
        {
            await tcs.Task.ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
        {
            // Place obtenue au moment même de l'annulation : on la rend aussitôt.
            Release();
            ct.ThrowIfCancellationRequested();
        }

        return new Slot(this);
    }

    private int Limit() => Math.Max(1, _limit());

    private void Release()
    {
        lock (_sync)
        {
            _active--;
            Wake();
        }
    }

    /// <summary>Donne les places libres aux premiers en attente (appelé sous verrou).</summary>
    private void Wake()
    {
        while (_waiters.First is { } first && _active < Limit())
        {
            _waiters.RemoveFirst();
            _active++;
            first.Value.TrySetResult();
        }
    }

    private sealed class Slot : IDisposable
    {
        private DownloadGate? _gate;

        public Slot(DownloadGate gate)
        {
            _gate = gate;
        }

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
