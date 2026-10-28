using Mafi;
using Mafi.Collections;
using Mafi.Core.Game;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;

namespace MapShare;

// Entry point. All work is in global dependencies (MapSession, MapCapture, MapWindow.Controller),
// which the game finds in this assembly by their attribute.
public sealed class MapShareMod : IMod
{
    public ModManifest Manifest { get; }

    // Reads game state and talks to the network, never changes the simulation.
    public bool IsUiOnly => true;

#pragma warning disable CS0618 // required by IMod
    public Option<IConfig> ModConfig => Option<IConfig>.None;
#pragma warning restore CS0618

    public ModJsonConfig JsonConfig { get; }

    public MapShareMod(ModManifest manifest)
    {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
    }

    public void RegisterPrototypes(ProtoRegistrator registrator) { }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb, bool gameWasLoaded) { }

    public void EarlyInit(DependencyResolver resolver) { }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded) { }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }

    public void Dispose() { }
}
