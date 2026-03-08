namespace Litmus.Models;

public class MethodDetail
{
    public required string Name { get; set; }
    public int Complexity { get; set; }
    public double? CoverageRate { get; set; }
}
