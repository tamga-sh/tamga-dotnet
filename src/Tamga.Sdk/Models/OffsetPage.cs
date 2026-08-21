namespace Tamga.Sdk.Models;

/// <summary>
/// One page of an OFFSET-paginated listing: the rows, plus the server's own count of how many
/// matched and how many pages that makes.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a separate type from <see cref="Page{T}"/> rather than an extra field on it,
/// because the two paginate on different mechanisms and mixing them silently loses rows in both
/// directions:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Page{T}"/> is KEYSET. The server sends no pagination metadata at all on those
/// routes, so <see cref="Page{T}.NextCursor"/> has to be synthesized from the last id of a page
/// that came back full — a row-count comparison, and the only end-of-list signal available.
/// </description></item>
/// <item><description>
/// <see cref="OffsetPage{T}"/> is OFFSET. The server sends
/// <c>meta.page{number,size,total,totalPages}</c> built from a real <c>COUNT(*)</c> over the same
/// filter that selected the rows, so end-of-list is <see cref="HasMore"/> — exact, and needing no
/// guess about whether a full page means there is another one.
/// </description></item>
/// </list>
/// <para>
/// Reaching for a cursor here, or for <see cref="Total"/> there, is the same mistake in opposite
/// directions. The machine collection is the only listing in this SDK that is offset-paginated.
/// </para>
/// </remarks>
/// <typeparam name="T">The resource type in this page.</typeparam>
public sealed record OffsetPage<T>
{
    /// <summary>The rows in this page.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>The 1-based page number this page represents.</summary>
    public int Number { get; init; }

    /// <summary>The page size the server actually applied, after its own <c>1..100</c> clamp.</summary>
    public int Size { get; init; }

    /// <summary>Total rows matching the request's filters — NOT the size of the table.</summary>
    public long Total { get; init; }

    /// <summary>Total pages at this <see cref="Size"/>. <c>0</c> when <see cref="Total"/> is <c>0</c>.</summary>
    public int TotalPages { get; init; }

    /// <summary>
    /// Whether a further page exists. Ask for <see cref="Number"/> + 1 while this is
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// The server caps the computed offset at 100 000 rows and answers <c>400 PAGE_OUT_OF_RANGE</c>
    /// past it, so a walk over a very large collection at a small <see cref="Size"/> can run out of
    /// pages before it runs out of rows. At the maximum size of 100 that ceiling is page 1001.
    /// </remarks>
    public bool HasMore => Number < TotalPages;
}
