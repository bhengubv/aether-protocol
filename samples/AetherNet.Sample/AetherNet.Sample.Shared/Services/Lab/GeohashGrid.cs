// SPDX-License-Identifier: MIT

using AetherNet.Map;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// A small rectangular block of <b>real</b> geohash cells, laid out as a grid a demo can draw. The cells
/// are not invented: the centre is <see cref="Geohash.Encode"/>d from a coordinate, and every other cell
/// is reached by walking <see cref="Geohash.Adjacent"/> east/west along the centre row and north/south
/// down each column — so a step on the grid is a step on the actual geohash lattice, and the Chebyshev
/// distance between two grid indices is exactly the number of cell-hops between their geohashes.
///
/// That equivalence is the whole point: the breadcrumb page's "within N cells" flood-guard and the map
/// page's cell+neighbours proximity query are both measured against this grid, and both stay honest
/// because the grid is the geohash neighbourhood, not a picture of one. Row 0 is the northern edge (top),
/// column 0 the western edge (left) — a north-up map.
/// </summary>
public sealed class GeohashGrid
{
    private readonly string[,] _cells;
    private readonly Dictionary<string, (int Row, int Col)> _index = new(StringComparer.Ordinal);

    public GeohashGrid(double centerLat, double centerLon, int rows, int cols, int precision = 6)
    {
        if (rows < 1 || cols < 1)
            throw new ArgumentOutOfRangeException(nameof(rows), "Grid must be at least 1×1.");

        Rows = rows;
        Cols = cols;
        Precision = precision;
        _cells = new string[rows, cols];

        var (cr, cc) = (rows / 2, cols / 2);
        _cells[cr, cc] = Geohash.Encode(centerLat, centerLon, precision);

        // Centre row: walk east to the right edge, west to the left edge.
        for (var c = cc + 1; c < cols; c++)
            _cells[cr, c] = Geohash.Adjacent(_cells[cr, c - 1], Geohash.Direction.East);
        for (var c = cc - 1; c >= 0; c--)
            _cells[cr, c] = Geohash.Adjacent(_cells[cr, c + 1], Geohash.Direction.West);

        // Every column: walk north up to row 0, south down to the last row, off the centre row.
        for (var c = 0; c < cols; c++)
        {
            for (var r = cr - 1; r >= 0; r--)
                _cells[r, c] = Geohash.Adjacent(_cells[r + 1, c], Geohash.Direction.North);
            for (var r = cr + 1; r < rows; r++)
                _cells[r, c] = Geohash.Adjacent(_cells[r - 1, c], Geohash.Direction.South);
        }

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
            _index[_cells[r, c]] = (r, c);
    }

    public int Rows { get; }
    public int Cols { get; }
    public int Precision { get; }

    /// <summary>The centre cell's grid index — where a demo drops its origin.</summary>
    public (int Row, int Col) Center => (Rows / 2, Cols / 2);

    /// <summary>The geohash of the cell at (<paramref name="row"/>, <paramref name="col"/>).</summary>
    public string Cell(int row, int col) => _cells[row, col];

    /// <summary>Grid index of a geohash cell, if it is one of ours.</summary>
    public bool TryIndexOf(string cell, out (int Row, int Col) index)
        => _index.TryGetValue(cell, out index);

    /// <summary>The cell-hop (Chebyshev) distance between two grid indices — equal to their geohash cell distance.</summary>
    public static int Distance((int Row, int Col) a, (int Row, int Col) b)
        => Math.Max(Math.Abs(a.Row - b.Row), Math.Abs(a.Col - b.Col));
}
