using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// Answers about a script that more than one caller asks for, built in one place.
/// </summary>
/// <remarks>
/// The API route and the dial-out channel both answer "what does this script declare", and they
/// have to answer it identically — a host that reads a procedure over one and runs it over the
/// other must not find the columns disagreeing. Two lambdas would be one answer and one guess.
/// </remarks>
public static class SequenceAnswers
{
    /// <summary>
    /// Everything a script says about itself, read without running it: the instrument models its
    /// DEVICE lines name, the values its INPUT lines take, and the columns it records.
    /// </summary>
    public static SequenceDeclarationDto Declares(string script) => new(
        SequenceRunner.Requirements(script).Select(r => r.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        SequenceRunner.Inputs(script),
        SequenceRunner.Columns(script));
}
