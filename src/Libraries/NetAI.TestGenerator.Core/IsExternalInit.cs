namespace System.Runtime.CompilerServices
{
    /// <summary>
    ///     Polyfill für den vom Compiler benötigten Typ zur Unterstützung von
    ///     `init`-only Settern und `record`-Typen auf älteren Zielframeworks.
    /// </summary>
    internal static class IsExternalInit { }
}