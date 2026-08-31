using BinPacking.Web.Models;

namespace BinPacking.Web.Algorithms;

public interface IPackingAlgorithm
{
    PackingAttempt Pack(BoxType box, IReadOnlyList<PackingItemUnit> items);
}
