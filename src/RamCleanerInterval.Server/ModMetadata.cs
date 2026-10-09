using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Web;

namespace RamCleanerInterval.Server;

/// <summary>
/// The server half exists only so the SPT launcher / web panel lists the RAM cleaner's page (<see cref="HomePage"/>);
/// the page itself comes from the game (<see cref="PageProxyController"/>).
/// </summary>
public record ModMetadata : IModMetadata, IModBlazorMetadata
{
    public string ModGuid { get; init; } = "com.cactuspie.ramcleanerinterval.server";
    public string Name { get; init; } = "RAM Cleaner (RamCleanerInterval)";
    public string Author { get; init; } = "CactusPie";
    public List<string>? Contributors { get; init; } = ["R_F (SPT 4.1 port)"];
    public SemanticVersioning.Version Version { get; init; } = new("2.13.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.5");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/danyhappy564-cmyk/SPT-RamCleanerInterval-zzap--Bootleg--";
    public string License { get; init; } = "GPL-3.0";

    public string? WWWRootUrl { get; init; }
    public string? HomePage { get; init; } = "/ramcleaner/";
    public string? HomePageDescription { get; init; } = "게임 메모리·FPS 실시간 (게임 켜져 있을 때) · Live game memory/FPS (while the game runs)";
}
