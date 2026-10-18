using System.Windows;
using System.Windows.Controls;

namespace MediaOrganizer.App.Controls;

/// <summary>Lays square tiles out in rows of a fixed count, sized so that a row
/// always fills the whole width.
///
/// <para>Before this there was a <c>WrapPanel</c> with tiles of a fixed pixel
/// size. It fitted in as many as happened to fit and left the remainder empty,
/// so the right-hand edge was ragged and the gap changed with every resize of
/// the window. Here the count is what is set and the size is what follows — the
/// zoom slider therefore counts tiles per row, not pixels.</para>
///
/// <para>The tiles carry no margin of their own; the spacing between them
/// belongs to the panel, because it has to come out of the width before the
/// size can be divided up.</para></summary>
public sealed class UniformTilePanel : Panel
{
    /// <summary>Fallback while the width is not yet known — inside a panel that
    /// measures with infinite width there is nothing to divide up. It only ever
    /// shows during the first measure pass.</summary>
    private const double FallbackTileSize = 120;

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(UniformTilePanel),
        new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>How many tiles stand side by side. Their size follows from it.</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(UniformTilePanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Gap between two tiles, horizontally and vertically.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Edge length a tile currently gets. The window reads it to scroll
    /// by whole rows; nothing else depends on it.</summary>
    public double TileSize { get; private set; } = FallbackTileSize;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Max(1, Columns);
        TileSize = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? FallbackTileSize
            : Math.Max(1, (availableSize.Width - ((columns - 1) * Spacing)) / columns);

        var childSize = new Size(TileSize, TileSize);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(childSize);
        }

        var rows = (InternalChildren.Count + columns - 1) / columns;
        var height = rows == 0 ? 0 : (rows * TileSize) + ((rows - 1) * Spacing);
        var width = double.IsInfinity(availableSize.Width)
            ? (columns * TileSize) + ((columns - 1) * Spacing)
            : availableSize.Width;

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, Columns);
        var tile = Math.Max(1, (finalSize.Width - ((columns - 1) * Spacing)) / columns);
        TileSize = tile;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            InternalChildren[i].Arrange(new Rect(
                column * (tile + Spacing),
                row * (tile + Spacing),
                tile,
                tile));
        }

        return finalSize;
    }
}
