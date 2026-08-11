using LabEquipmentController.Cli;

// Ctrl+C stops the work rather than the process: a scan or a sweep that is interrupted
// should still close its sockets and return the instrument to local control, which is the
// same courtesy the GUI's Stop button extends.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var cmd = CommandLine.Parse(args);

if (cmd.Error is not null)
{
    Console.Error.Write(cmd.Error + "\nRun 'lec --help' for usage.\n");
    return Commands.Misused;
}

try
{
    return cmd.Verb switch
    {
        "help" or "--help"    => Print(CommandLine.Usage),
        "version" or "-v"     => Commands.Version(cmd, Console.Out),
        "interfaces" or "ifs" => Commands.Interfaces(cmd, Console.Out),
        "scan"                => await Commands.Scan(cmd, Console.Out, Console.Error, cts.Token),
        "id" or "identify"    => await Commands.Identify(cmd, Console.Out, Console.Error, cts.Token),
        "send" or "query"     => await Commands.Send(cmd, Console.Out, Console.Error, cts.Token),
        "run"                 => await Commands.Run(cmd, Console.Out, Console.Error, cts.Token),
        "seq" or "sequence"   => await Commands.Sequence(cmd, Console.Out, Console.Error, cts.Token),
        "watch" or "poll"     => await Commands.Watch(cmd, Console.Out, Console.Error, cts.Token),
        "screenshot" or "shot" => await Commands.Screenshot(cmd, Console.Out, Console.Error, cts.Token),
        "capture" or "wave"   => await Commands.CaptureWaveform(cmd, Console.Out, Console.Error, cts.Token),
        "plot"                => Commands.PlotFile(cmd, Console.Out, Console.Error),
        "catalog" or "cat"    => Commands.Catalog(cmd, Console.Out, Console.Error),
        _                     => Unknown(cmd.Verb),
    };
}
catch (OperationCanceledException)
{
    Console.Error.Write("Cancelled.\n");
    return Commands.Failed;
}

static int Print(string text)
{
    Console.Out.Write(text + "\n");
    return Commands.Ok;
}

static int Unknown(string verb)
{
    Console.Error.Write($"Unknown command '{verb}'. Run 'lec --help' for the list.\n");
    return Commands.Misused;
}
