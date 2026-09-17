using System.Globalization;

/// <summary>
/// The synthetic household: one address, four utilities, several months.
/// Figures are internally consistent (rows sum to the subtotal, usage matches the meter readings) so
/// that retrieval answers and SQL totals can be checked against each other.
/// Water usage trends upward across the quarters on purpose - it gives the demo a real trend to find.
/// </summary>
internal static class SampleBills
{
    private const string Address = "14 Alder Lane, Springfield, OR 97477";

    public static IEnumerable<Bill> All()
    {
        yield return Electricity("electric-2025-05.pdf", new(2025, 5, 1), new(2025, 5, 31), usage: 402, lastYear: 431, meterStart: 71_240, previous: 0m, paid: 0m);
        yield return Electricity("electric-2025-06.pdf", new(2025, 6, 1), new(2025, 6, 30), usage: 518, lastYear: 495, meterStart: 71_642, previous: 84.13m, paid: 84.13m);
        yield return Electricity("electric-2025-07.pdf", new(2025, 7, 1), new(2025, 7, 31), usage: 734, lastYear: 612, meterStart: 72_160, previous: 103.77m, paid: 103.77m);

        yield return Water("water-2025-q2.pdf", new(2025, 4, 1), new(2025, 6, 30), usage: 14, lastYear: 13, meterStart: 3_182);
        yield return Water("water-2025-q3.pdf", new(2025, 7, 1), new(2025, 9, 30), usage: 19, lastYear: 15, meterStart: 3_196);

        yield return Gas();
        yield return Internet();
    }

    private static Bill Electricity(string file, DateOnly start, DateOnly end, int usage, int lastYear, int meterStart, decimal previous, decimal paid)
    {
        // Three-tier residential rate: the first 300 kWh are cheapest, everything above 600 is dearest.
        var tier1 = Math.Min(usage, 300);
        var tier2 = Math.Clamp(usage - 300, 0, 300);
        var tier3 = Math.Max(usage - 600, 0);

        var charges = new List<ChargeRow>
        {
            new("Basic service charge", 0m, 1, 12.50m),
            new("Energy charge tier 1 (0-300)", 0.1802m, tier1, Round(tier1 * 0.1802m)),
        };

        if (tier2 > 0)
        {
            charges.Add(new ChargeRow("Energy charge tier 2 (301-600)", 0.2431m, tier2, Round(tier2 * 0.2431m)));
        }

        if (tier3 > 0)
        {
            charges.Add(new ChargeRow("Energy charge tier 3 (601+)", 0.3109m, tier3, Round(tier3 * 0.3109m)));
        }

        charges.Add(new ChargeRow("Transmission and distribution", 0.0411m, usage, Round(usage * 0.0411m)));

        var fees = new List<Fee>
        {
            new("State energy efficiency levy", 1.85m),
            new("Local franchise fee (3.5%)", Round(charges.Sum(c => c.Amount) * 0.035m))
        };

        return new Bill(
            file,
            "Cascade Power & Light",
            "Member-owned electric cooperative - cascadepower.example",
            "Electricity",
            AccountNumber("8830117422", start),
            Address,
            start,
            end,
            end.AddDays(3),
            end.AddDays(21),
            start.AddDays(-6),
            "kWh",
            usage,
            lastYear,
            meterStart,
            previous,
            paid,
            charges,
            fees,
            [
                "Rates shown are per kWh and include the approved 2025 tariff adjustment.",
                "A late payment charge of 1.5% per month applies to balances unpaid after the due date.",
                "Budget billing is available; call 555-0142 to level your monthly payments.",
                "Report an outage at any time on 555-0199."
            ]);
    }

    private static Bill Water(string file, DateOnly start, DateOnly end, int usage, int lastYear, int meterStart)
    {
        // Billed in CCF (hundred cubic feet); sewer is charged on the same volume.
        var charges = new List<ChargeRow>
        {
            new("Water base charge (5/8\" meter)", 0m, 1, 18.40m),
            new("Water volume charge", 3.9100m, usage, Round(usage * 3.91m)),
            new("Sewer volume charge", 5.2400m, usage, Round(usage * 5.24m)),
            new("Stormwater management", 0m, 1, 9.75m)
        };

        var fees = new List<Fee> { new("Utility tax (2.1%)", Round(charges.Sum(c => c.Amount) * 0.021m)) };

        return new Bill(
            file,
            "Springfield Municipal Water",
            "City of Springfield Public Works - water and sewer services",
            "Water and sewer",
            AccountNumber("4471209955", start),
            Address,
            start,
            end,
            end.AddDays(2),
            end.AddDays(25),
            start.AddDays(-11),
            "CCF",
            usage,
            lastYear,
            meterStart,
            0m,
            0m,
            charges,
            fees,
            [
                "One CCF equals 748 gallons.",
                "Quarterly billing. Consumption above 18 CCF per quarter may indicate a leak.",
                "Free leak-detection kits are available at the Public Works counter.",
                "Sewer volume is billed on metered water use."
            ]);
    }

    private static Bill Gas() =>
        new(
            "gas-2025-07.pdf",
            "Willamette Natural Gas",
            "Safe, reliable natural gas since 1954 - wng.example",
            "Natural gas",
            AccountNumber("2299473310", new DateOnly(2025, 7, 1)),
            Address,
            new DateOnly(2025, 7, 1),
            new DateOnly(2025, 7, 31),
            new DateOnly(2025, 8, 2),
            new DateOnly(2025, 8, 20),
            new DateOnly(2025, 6, 24),
            "therms",
            18,
            21,
            9_044,
            0m,
            0m,
            [
                new ChargeRow("Monthly customer charge", 0m, 1, 9.95m),
                new ChargeRow("Distribution charge", 0.4820m, 18, Round(18 * 0.482m)),
                new ChargeRow("Gas commodity cost", 0.6135m, 18, Round(18 * 0.6135m))
            ],
            [new Fee("Carbon reduction program", 0.92m)],
            [
                "Summer usage reflects water heating only; expect higher winter volumes.",
                "Equal-pay plan spreads winter heating costs across twelve months.",
                "Smell gas? Leave the building and call 555-0177 immediately."
            ]);

    private static Bill Internet() =>
        new(
            "internet-2025-07.pdf",
            "Fiberline Communications",
            "Gigabit fibre to the home - fiberline.example",
            "Internet service",
            AccountNumber("6610338877", new DateOnly(2025, 7, 1)),
            Address,
            new DateOnly(2025, 7, 1),
            new DateOnly(2025, 7, 31),
            new DateOnly(2025, 7, 1),
            new DateOnly(2025, 7, 15),
            new DateOnly(2025, 6, 12),
            "GB",
            1_284,
            940,
            0,
            0m,
            0m,
            [
                new ChargeRow("Fibre 1 Gbps residential", 0m, 1, 74.99m),
                new ChargeRow("Router rental", 0m, 1, 9.00m),
                new ChargeRow("Promotional credit (months 1-12)", 0m, 1, -15.00m)
            ],
            [new Fee("Regulatory recovery fee", 2.14m)],
            [
                "Service is not metered; the usage figure is informational only.",
                "Your promotional rate ends 2026-03-31, after which standard rates apply.",
                "Speeds quoted are maximum wired speeds."
            ]);

    /// <summary>Stable per-utility account number with a period-derived suffix, like a real statement.</summary>
    private static string AccountNumber(string root, DateOnly period) =>
        $"{root[..4]}-{root[4..8]}-{root[8..]}{period:MM}";

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
