namespace Lingarr.Contracts.Models;

/// <summary>
/// A previously translated subtitle used as paired source/target context for a subsequent translation.
/// </summary>
public sealed record TranslationContextPair(string Source, string Target);
