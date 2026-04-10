namespace FieldCure.Mcp.Rag.Models;

/// <summary>Indicates which search strategy was used.</summary>
public enum SearchMode
{
    Hybrid,
    VectorOnly,
    Bm25Only
}
