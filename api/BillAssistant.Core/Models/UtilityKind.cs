namespace BillAssistant.Core.Models;

/// <summary>The kind of household utility a bill is for.</summary>
public enum UtilityKind
{
    Unknown = 0,
    Electricity,
    Water,
    Gas,
    Internet,
    Waste,
    Other
}
