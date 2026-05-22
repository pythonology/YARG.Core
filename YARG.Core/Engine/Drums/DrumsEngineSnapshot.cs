namespace YARG.Core.Engine.Drums
{
    // Empty subtype — drums have no extra per-tick state beyond the base fields and
    // <see cref="DrumsStats"/>. Exists so RestoreSnapshot can discriminate on subtype.
    public sealed class DrumsEngineSnapshot : EngineSnapshot
    {
    }
}
