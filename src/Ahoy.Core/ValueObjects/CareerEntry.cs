using Ahoy.Core.Enums;
using Ahoy.Core.Ids;

namespace Ahoy.Core.ValueObjects;

/// <summary>A single entry in an Individual's career history.</summary>
public sealed record CareerEntry(
    WorldDate Date,
    IndividualRole Role,
    FactionId? FactionId,
    PortId? PortId,
    string Description
);
