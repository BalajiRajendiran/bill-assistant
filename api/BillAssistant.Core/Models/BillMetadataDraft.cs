using System.ComponentModel;

namespace BillAssistant.Core.Models;

/// <summary>
/// Raw shape requested from the chat model. Everything is a nullable string or number: a small model
/// asked for a strongly typed date will happily invent one, so parsing and validation happen here in
/// code (see BillMetadataValidator) rather than being trusted from the model.
/// </summary>
public sealed class BillMetadataDraft
{
    [Description("One of: Electricity, Water, Gas, Internet, Waste, Other. Use Other if unclear.")]
    public string? Utility { get; set; }

    [Description("The utility company that issued the bill, e.g. 'Pacific Gas & Electric'.")]
    public string? ProviderName { get; set; }

    [Description("The account number exactly as printed on the bill, or null if not present.")]
    public string? AccountNumber { get; set; }

    [Description("First day of the service period, formatted yyyy-MM-dd.")]
    public string? PeriodStart { get; set; }

    [Description("Last day of the service period, formatted yyyy-MM-dd.")]
    public string? PeriodEnd { get; set; }

    [Description("Total amount due for this bill as a plain number, e.g. 84.21. No currency symbol.")]
    public double? AmountDue { get; set; }

    [Description("ISO currency code such as USD, EUR, GBP. Default to USD when a '$' is shown.")]
    public string? Currency { get; set; }

    [Description("Payment due date, formatted yyyy-MM-dd.")]
    public string? DueDate { get; set; }

    [Description("Total metered usage for the period as a plain number, e.g. 412.0.")]
    public double? UsageQuantity { get; set; }

    [Description("Unit for the usage figure: kWh, therms, CCF, gallons, GB.")]
    public string? UsageUnit { get; set; }
}
