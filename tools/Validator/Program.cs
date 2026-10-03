using Workshop.Validator.Cli;
using Workshop.Validator.Commands;

// One executable, several commands; the workflows under .github/workflows call these.
//   intake      issue -> validated item folder + package + result.json
//   finalize    stamp the uploaded asset's URL into an item manifest
//   validate    check item folders (schema, contents vs asset, author on updates)
//   build-index write index/ and the generated README listings
//   migrate     rewrite every manifest to the current schema
//   inspect     print what the validator sees in a package (development aid)

var args2 = Args.Parse(args);
if (args2.Command is null)
{
    Console.Error.WriteLine("usage: workshop-validator <intake|finalize|validate|build-index|migrate|inspect> [--option value ...]");
    return 2;
}

try
{
    return args2.Command switch
    {
        "intake" => await IntakeCommand.RunAsync(args2),
        "finalize" => FinalizeCommand.Run(args2),
        "validate" => await ValidateCommand.RunAsync(args2),
        "build-index" => BuildIndexCommand.Run(args2),
        "migrate" => MigrateCommand.Run(args2),
        "inspect" => InspectCommand.Run(args2),
        _ => Fail($"Unknown command '{args2.Command}'.")
    };
}
catch (ValidationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
