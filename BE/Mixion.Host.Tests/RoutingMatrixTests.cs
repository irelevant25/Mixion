using Mixion.Host.State;
using Xunit;

namespace Mixion.Host.Tests;

public class RoutingMatrixTests
{
    [Fact]
    public void Resize_KeepsExistingRoutesAndAddsUnroutedCells()
    {
        var matrix = new RoutingMatrix(2, 2).With(0, 1, true).With(1, 0, true);

        var grown = matrix.Resize(3, 3);

        Assert.True(grown[0, 1]);
        Assert.True(grown[1, 0]);
        Assert.False(grown[0, 0]);
        Assert.False(grown[2, 2]);
        Assert.False(grown[0, 2]);
        Assert.Equal(3, grown.Inputs);
        Assert.Equal(3, grown.Outputs);
    }

    [Fact]
    public void Resize_ShrinkingDropsCellsOutsideTheNewBounds()
    {
        var matrix = new RoutingMatrix(2, 2).With(1, 1, true).With(0, 0, true);

        var shrunk = matrix.Resize(1, 1);

        Assert.True(shrunk[0, 0]);
        Assert.Equal(1, shrunk.Inputs);
    }

    [Fact]
    public void Resize_ToTheSameSize_ReturnsTheSameInstance()
    {
        var matrix = new RoutingMatrix(2, 3);

        Assert.Same(matrix, matrix.Resize(2, 3));
    }
}
