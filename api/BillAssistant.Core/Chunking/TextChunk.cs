namespace BillAssistant.Core.Chunking;

/// <summary>A chunk of page text, before embedding.</summary>
public sealed record TextChunk(int PageNumber, int ChunkIndex, string Text);
