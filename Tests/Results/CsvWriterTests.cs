using System.Collections.Generic;

namespace LabEquipmentController.Tests;

/// <summary>
/// The one CSV writer every Save CSV goes through. The rule it enforces is RFC 4180's:
/// quote a field only when it holds a comma, a quote or a line break.
///
/// The case that brought it into being is <see cref="A_reply_full_of_commas_stays_one_column"/>
/// — a bench generator's answer to <c>C1:OUTP?</c>, recorded into a column and written out
/// unquoted, arrived in a spreadsheet as five columns.
/// </summary>
public class CsvWriterTests
{
    private static IReadOnlyList<string> Row(params string[] values) => values;

    [Fact]
    public void A_plain_number_is_left_alone()
    {
        Assert.Equal("1.6e-01", CsvWriter.Field("1.6e-01"));
    }

    [Fact]
    public void A_field_holding_a_comma_is_quoted()
    {
        Assert.Equal("\"1,2\"", CsvWriter.Field("1,2"));
    }

    [Fact]
    public void A_field_holding_a_quote_has_it_doubled()
    {
        Assert.Equal("\"say \"\"hi\"\"\"", CsvWriter.Field("say \"hi\""));
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("two\r\nlines")]
    public void A_field_holding_a_line_break_is_quoted(string value)
    {
        Assert.StartsWith("\"", CsvWriter.Field(value));
        Assert.EndsWith("\"", CsvWriter.Field(value));
    }

    [Fact]
    public void A_missing_field_is_an_empty_one()
    {
        Assert.Equal("", CsvWriter.Field(null));
    }

    [Fact]
    public void The_header_is_escaped_like_any_other_row()
    {
        string csv = CsvWriter.Table(["Time (s)", "Reading, as sent"], []);
        Assert.Equal("Time (s),\"Reading, as sent\"\r\n", csv);
    }

    [Fact]
    public void Rows_follow_the_header_and_end_with_crlf()
    {
        string csv = CsvWriter.Table(["A", "B"], [Row("1", "2"), Row("3", "4")]);
        Assert.Equal("A,B\r\n1,2\r\n3,4\r\n", csv);
    }

    [Fact]
    public void Without_columns_there_is_no_header_line()
    {
        Assert.Equal("1,2\r\n", CsvWriter.Table([], [Row("1", "2")]));
    }

    /// <summary>
    /// A Siglent SDG answers <c>C1:OUTP?</c> with its whole output state in one reply. It is
    /// one reading and it belongs in one cell — the file has to say so, or the row it is in
    /// grows four columns that the header never declared.
    /// </summary>
    [Fact]
    public void A_reply_full_of_commas_stays_one_column()
    {
        const string Reply = "C1:OUTP OFF,LOAD,HZ,PLRT,NOR";
        string csv = CsvWriter.Table(["Pass", "Gen CH1 output"], [Row("1", Reply)]);

        Assert.Equal("Pass,Gen CH1 output\r\n1,\"C1:OUTP OFF,LOAD,HZ,PLRT,NOR\"\r\n", csv);
    }

    /// <summary>Every *IDN? carries three commas, so this is the common case, not the odd one.</summary>
    [Fact]
    public void An_identity_stays_one_column()
    {
        const string Idn = "RIGOL TECHNOLOGIES,DS2202,DS2A152001051,00.01.00";
        Assert.Equal("\"" + Idn + "\"", CsvWriter.Field(Idn));
    }
}
