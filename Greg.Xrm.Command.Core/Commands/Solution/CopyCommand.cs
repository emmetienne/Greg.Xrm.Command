using System.ComponentModel.DataAnnotations;

namespace Greg.Xrm.Command.Commands.Solution
{
    [Command("solution", "copy", HelpText = "this command copies solution components from one or more solution to another")]

    public class CopyCommand
    {
        [Option("sourceSolutions", "ss", HelpText = "The name or the names of the solution used to copy components separated by ','")]
        [Required]
        public string SourceSolutions { get; set; }
        [Option("targetSolution", "ts", HelpText = "The target name of the solution, if it's not existing it will be created")]
        [Required]
        public string TargetSolution { get; set; }
        [Option("publisherPrefix", "pp", HelpText = "The prefix of the publisher used to create the solution if needed")]
        public string? PublisherPrefix { get; set; }
    }
}