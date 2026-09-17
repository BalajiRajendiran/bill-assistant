using System.Globalization;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

// Generates synthetic household utility bills as text-layer PDFs, for development and tests.
// No real account data: providers, addresses and account numbers are invented.

var outputDir = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples");

outputDir = Path.GetFullPath(outputDir);
Directory.CreateDirectory(outputDir);

foreach (var bill in SampleBills.All())
{
    var path = Path.Combine(outputDir, bill.FileName);
    File.WriteAllBytes(path, Render(bill));
    Console.WriteLine($"wrote {path}");
}

Console.WriteLine($"\n{SampleBills.All().Count()} sample bills written to {outputDir}");

static byte[] Render(Bill bill)
{
    var builder = new PdfDocumentBuilder { IncludeDocumentInformation = true };
    builder.DocumentInformation.Title = $"{bill.Provider} - {bill.PeriodLabel}";
    builder.DocumentInformation.Producer = "bill-assistant sample generator";

    var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
    var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
    var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);

    const double left = 50;
    const double top = 790;
    var y = top;

    void Line(string text, double size = 10, bool isBold = false, double gap = 14, double x = left)
    {
        if (!string.IsNullOrEmpty(text))
        {
            page.AddText(text, size, new PdfPoint(x, y), isBold ? bold : regular);
        }

        y -= gap;
    }

    // --- header ---
    Line(bill.Provider, 18, isBold: true, gap: 22);
    Line(bill.ProviderTagline, 9, gap: 24);

    Line($"{bill.UtilityLabel} statement", 13, isBold: true, gap: 20);

    // --- account block ---
    Line($"Account number:      {bill.AccountNumber}");
    Line($"Service address:     {bill.ServiceAddress}");
    Line($"Statement date:      {bill.StatementDate:yyyy-MM-dd}");
    Line($"Service period:      {bill.PeriodStart:yyyy-MM-dd} to {bill.PeriodEnd:yyyy-MM-dd}");
    Line($"Payment due date:    {bill.DueDate:yyyy-MM-dd}", gap: 24);

    // --- summary ---
    Line("Account summary", 12, isBold: true, gap: 18);
    Line($"Previous balance                                  {Money(bill.PreviousBalance)}");
    Line($"Payment received {bill.PaymentDate:yyyy-MM-dd}                     -{Money(bill.PaymentReceived)}");
    Line($"Current charges                                   {Money(bill.CurrentCharges)}", gap: 18);
    Line($"TOTAL AMOUNT DUE                                  {Money(bill.AmountDue)}", 12, isBold: true, gap: 26);

    // --- usage ---
    Line("Usage this period", 12, isBold: true, gap: 18);
    Line($"Meter reading (current)    {bill.MeterEnd:N0} {bill.Unit}");
    Line($"Meter reading (previous)   {bill.MeterStart:N0} {bill.Unit}");
    Line($"Total usage                {bill.Usage:N0} {bill.Unit}");
    Line($"Same period last year      {bill.UsageLastYear:N0} {bill.Unit}");
    Line($"Daily average              {bill.Usage / bill.Days:N1} {bill.Unit}/day", gap: 24);

    // --- rate table ---
    Line("Charge detail", 12, isBold: true, gap: 18);
    Line($"{"Description",-34}{"Rate",10}{"Quantity",14}{"Amount",12}", 9, isBold: true);
    foreach (var row in bill.Charges)
    {
        Line($"{row.Description,-34}{row.Rate,10:0.0000}{row.Quantity + " " + bill.Unit,14}{Money(row.Amount),12}", 9, gap: 12);
    }

    Line($"{"Subtotal",-34}{"",10}{"",14}{Money(bill.Charges.Sum(c => c.Amount)),12}", 9, isBold: true, gap: 12);
    foreach (var fee in bill.Fees)
    {
        Line($"{fee.Description,-34}{"",10}{"",14}{Money(fee.Amount),12}", 9, gap: 12);
    }

    Line($"{"Current charges",-34}{"",10}{"",14}{Money(bill.CurrentCharges),12}", 9, isBold: true, gap: 24);

    // --- notes ---
    Line("Important information", 11, isBold: true, gap: 16);
    foreach (var note in bill.Notes)
    {
        Line(note, 9, gap: 12);
    }

    return builder.Build();
}

static string Money(decimal value) => "$" + value.ToString("0.00", CultureInfo.InvariantCulture);

record ChargeRow(string Description, decimal Rate, int Quantity, decimal Amount);

record Fee(string Description, decimal Amount);

record Bill(
    string FileName,
    string Provider,
    string ProviderTagline,
    string UtilityLabel,
    string AccountNumber,
    string ServiceAddress,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly StatementDate,
    DateOnly DueDate,
    DateOnly PaymentDate,
    string Unit,
    int Usage,
    int UsageLastYear,
    int MeterStart,
    decimal PreviousBalance,
    decimal PaymentReceived,
    IReadOnlyList<ChargeRow> Charges,
    IReadOnlyList<Fee> Fees,
    IReadOnlyList<string> Notes)
{
    public int Days => PeriodEnd.DayNumber - PeriodStart.DayNumber + 1;
    public int MeterEnd => MeterStart + Usage;
    public decimal CurrentCharges => Charges.Sum(c => c.Amount) + Fees.Sum(f => f.Amount);
    public decimal AmountDue => CurrentCharges + PreviousBalance - PaymentReceived;
    public string PeriodLabel => $"{PeriodStart:yyyy-MM-dd} to {PeriodEnd:yyyy-MM-dd}";
}
