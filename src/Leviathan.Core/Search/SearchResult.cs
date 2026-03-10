namespace Leviathan.Core.Search;

/// <summary>
/// A match found by the search engine: byte offset and length within the document.
/// </summary>
public readonly record struct SearchResult(long Offset, long Length);
