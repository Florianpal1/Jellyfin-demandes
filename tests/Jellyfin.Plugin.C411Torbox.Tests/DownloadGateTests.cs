using Jellyfin.Plugin.C411Torbox.Services.Import;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class DownloadGateTests
{
    [Fact]
    public async Task Limite_le_nombre_de_places_et_respecte_l_ordre_d_arrivee()
    {
        var gate = new DownloadGate(() => 2);
        var a = await gate.EnterAsync(CancellationToken.None);
        var b = await gate.EnterAsync(CancellationToken.None);
        var c = gate.EnterAsync(CancellationToken.None);
        var d = gate.EnterAsync(CancellationToken.None);
        Assert.False(c.IsCompleted);
        Assert.False(d.IsCompleted);

        a.Dispose();
        var slotC = await c.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(d.IsCompleted);

        // Une place rendue deux fois ne compte qu'une fois.
        a.Dispose();
        Assert.False(d.IsCompleted);

        b.Dispose();
        (await d.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        slotC.Dispose();
    }

    [Fact]
    public async Task Une_hausse_de_la_limite_s_applique_a_la_prochaine_entree()
    {
        int limit = 1;
        var gate = new DownloadGate(() => limit);
        using var a = await gate.EnterAsync(CancellationToken.None);
        var blocked = gate.EnterAsync(CancellationToken.None);
        Assert.False(blocked.IsCompleted);

        limit = 3;
        using var b = await gate.EnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        (await blocked.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Une_attente_annulee_ne_consomme_pas_de_place()
    {
        var gate = new DownloadGate(() => 1);
        var a = await gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(cts.Token);
        var next = gate.EnterAsync(CancellationToken.None);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        a.Dispose();
        (await next.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        (await gate.EnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
