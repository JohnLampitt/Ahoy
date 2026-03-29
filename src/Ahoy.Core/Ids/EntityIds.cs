namespace Ahoy.Core.Ids;

public readonly record struct PortId(Guid Value)
{
    public static PortId New() => new(Guid.NewGuid());
    public override string ToString() => $"Port:{Value:N}";
}

public readonly record struct RegionId(Guid Value)
{
    public static RegionId New() => new(Guid.NewGuid());
    public override string ToString() => $"Region:{Value:N}";
}

public readonly record struct FactionId(Guid Value)
{
    public static FactionId New() => new(Guid.NewGuid());
    public override string ToString() => $"Faction:{Value:N}";
}

public readonly record struct ShipId(Guid Value)
{
    public static ShipId New() => new(Guid.NewGuid());
    public override string ToString() => $"Ship:{Value:N}";
}

public readonly record struct IndividualId(Guid Value)
{
    public static IndividualId New() => new(Guid.NewGuid());
    public override string ToString() => $"Individual:{Value:N}";
}

public readonly record struct KnowledgeFactId(Guid Value)
{
    public static KnowledgeFactId New() => new(Guid.NewGuid());
    public override string ToString() => $"Fact:{Value:N}";
}

public readonly record struct NavalDeploymentId(Guid Value)
{
    public static NavalDeploymentId New() => new(Guid.NewGuid());
    public override string ToString() => $"NavalDeployment:{Value:N}";
}
