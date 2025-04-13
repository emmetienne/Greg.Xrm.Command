using Greg.Xrm.Command;
using Greg.Xrm.Command.Commands.Solution;
using Greg.Xrm.Command.Services.Connection;
using Greg.Xrm.Command.Services.Output;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

public class CopyCommandExecutor : ICommandExecutor<CopyCommand>
{
    private readonly IOutput output;
    private readonly IOrganizationServiceRepository organizationServiceRepository;
    private IOrganizationServiceAsync2 crm;

    public CopyCommandExecutor(IOutput output, IOrganizationServiceRepository organizationServiceRepository)
    {
        this.output = output;
        this.organizationServiceRepository = organizationServiceRepository;
    }

    public async Task<CommandResult> ExecuteAsync(CopyCommand command, CancellationToken cancellationToken)
    {
        output.Write("Connecting to the current dataverse environment...");
        crm = await organizationServiceRepository.GetCurrentConnectionAsync();
        output.WriteLine("Done", ConsoleColor.Green);

        var sourceSolutions = command.SourceSolutions.Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sourceSolutions.Length == 0)
            return CommandResult.Fail("No valid source solutions provided.");

        var solutionComponents = await GetSolutionsComponents(sourceSolutions);
        var groupedComponents = solutionComponents.Entities
            .GroupBy(x => x.GetAttributeValue<EntityReference>("solutionid").Id);

        var existingSolutions = solutionComponents.Entities
            .Select(x => x.GetAttributeValue<AliasedValue>("solution.friendlyname")?.Value?.ToString())
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceSolution in sourceSolutions)
        {
            if (!existingSolutions.Contains(sourceSolution))
                return CommandResult.Fail($"The solution {sourceSolution} does not exist.");
        }

        await EnsureTargetSolutionExistsAsync(command);

        AddComponentsToSolution(command, groupedComponents);

        if (command.CheckCopiedComponents.GetValueOrDefault(true))
            await PerformPostCopyChecks(command.TargetSolution, solutionComponents, command.PruneAutoAddedComponentFromTargetSolution);

        return CommandResult.Success();
    }

    private async Task<EntityCollection> GetSolutionsComponents(string[] solutionNames)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("solutionid", "componenttype", "objectid")
        };

        var link = new LinkEntity("solutioncomponent", "solution", "solutionid", "solutionid", JoinOperator.Inner)
        {
            EntityAlias = "solution",
            Columns = new ColumnSet("uniquename", "friendlyname"),
            LinkCriteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.In, solutionNames) } }
        };

        query.LinkEntities.Add(link);
        return await crm.RetrieveMultipleAsync(query);
    }

    private async Task EnsureTargetSolutionExistsAsync(CopyCommand command)
    {
        var existingSolutions = await CheckIfTargetSolutionExistsAsync(command.TargetSolution);

        if (existingSolutions.Entities.Count > 1)
            throw new Exception($"Multiple solutions found with the name {command.TargetSolution}.");

        if (existingSolutions.Entities.Count == 1)
        {
            output.WriteLine($"Target solution {command.TargetSolution} exists.", ConsoleColor.Green);
            return;
        }

        if (string.IsNullOrEmpty(command.PublisherPrefix))
            throw new Exception($"Target solution {command.TargetSolution} does not exist, and no publisher prefix was provided.");

        var publishers = await GetPublisherByPrefixAsync(command.PublisherPrefix);

        if (publishers.Entities.Count != 1)
            throw new Exception($"Publisher with prefix {command.PublisherPrefix} not found or ambiguous.");

        CreateSolution(command.TargetSolution, publishers.Entities[0].Id);
        output.WriteLine($"Created solution {command.TargetSolution} with publisher prefix {command.PublisherPrefix}.", ConsoleColor.Green);
    }

    private async Task<EntityCollection> CheckIfTargetSolutionExistsAsync(string name)
    {
        var query = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("solutionid", "uniquename", "friendlyname"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, name) } }
        };

        return await crm.RetrieveMultipleAsync(query);
    }

    private async Task<EntityCollection> GetPublisherByPrefixAsync(string prefix)
    {
        var query = new QueryExpression("publisher")
        {
            NoLock = true,
            Criteria = { Conditions = { new ConditionExpression("customizationprefix", ConditionOperator.Equal, prefix) } }
        };

        return await crm.RetrieveMultipleAsync(query);
    }

    private void CreateSolution(string solutionName, Guid publisherId)
    {
        var solution = new Entity("solution")
        {
            ["uniquename"] = solutionName,
            ["friendlyname"] = solutionName,
            ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisherId)
        };

        crm.Create(solution);
    }

    private void AddComponentsToSolution(CopyCommand command, IEnumerable<IGrouping<Guid, Entity>> groupedComponents)
    {
        foreach (var group in groupedComponents)
        {
            var solutionName = group.First().GetAttributeValue<AliasedValue>("solution.friendlyname").Value.ToString();
            output.WriteLine($"Copying components from solution: {solutionName}", ConsoleColor.Blue);

            foreach (var component in group.OrderBy(x => x.GetAttributeValue<OptionSetValue>("componenttype").Value))
            {
                var componentType = component.GetAttributeValue<OptionSetValue>("componenttype").Value;
                var objectId = component.GetAttributeValue<Guid>("objectid");

                output.Write($"Copying Component Type: {componentType}, Object Id: {objectId}...");
                AddComponentToSolution(objectId, componentType, command.TargetSolution);
                output.WriteLine("Done", ConsoleColor.Green);
            }
        }
    }

    public void AddComponentToSolution(Guid objectId, int componentType, string solutionUniqueName)
    {
        var request = new AddSolutionComponentRequest
        {
            ComponentType = componentType,
            ComponentId = objectId,
            SolutionUniqueName = solutionUniqueName,
            AddRequiredComponents = false,
            DoNotIncludeSubcomponents = componentType == 1
        };

        crm.Execute(request);
    }

    private async Task PerformPostCopyChecks(string targetSolution, EntityCollection sourceComponents, bool? pruneAutoAddedComponents)
    {
        output.WriteLine("Performing checks for copied components...", ConsoleColor.Blue);

        var targetComponents = await GetSolutionsComponents(new[] { targetSolution });

        ValidateCopiedComponents(sourceComponents, targetComponents);

        var autoAddedComponents = IdentifyAutoAddedComponents(sourceComponents, targetComponents);

        if (pruneAutoAddedComponents.HasValue && !pruneAutoAddedComponents.Value)
            return;

        await PruneAutoAddedComponents(autoAddedComponents);

        output.WriteLine("Sanity checks after pruning...", ConsoleColor.Blue);
        var postPruningComponents = await GetSolutionsComponents(new[] { targetSolution });
        IdentifyAutoAddedComponents(sourceComponents, postPruningComponents);
    }

    private void ValidateCopiedComponents(EntityCollection sourceComponents, EntityCollection targetComponents)
    {
        output.WriteLine("Validating copied components...");

        var missingComponents = sourceComponents.Entities
            .Where(source => !targetComponents.Entities.Any(target => target.GetAttributeValue<Guid>("objectid") == source.GetAttributeValue<Guid>("objectid")))
            .ToList();

        if (missingComponents.Count == 0)
            output.WriteLine("All components were copied successfully.", ConsoleColor.Green);
        else
        {
            output.WriteLine($"{missingComponents.Count} components were not copied successfully:", ConsoleColor.Red);
            foreach (var component in missingComponents)
            {
                var objectId = component.GetAttributeValue<Guid>("objectid");
                var componentType = component.GetAttributeValue<OptionSetValue>("componenttype")?.Value;
                output.WriteLine($"- Object ID: {objectId}, Component Type: {componentType}", ConsoleColor.Yellow);
            }
        }
    }

    private List<Entity> IdentifyAutoAddedComponents(EntityCollection sourceComponents, EntityCollection targetComponents)
    {
        output.WriteLine("Checking for auto-added components in the target solution...");

        var autoAddedComponents = targetComponents.Entities
            .Where(target => !sourceComponents.Entities.Any(source => source.GetAttributeValue<Guid>("objectid") == target.GetAttributeValue<Guid>("objectid")))
            .ToList();

        if (autoAddedComponents.Count == 0)
            output.WriteLine("No auto-added components found.", ConsoleColor.Green);
        else
        {
            output.WriteLine($"{autoAddedComponents.Count} auto-added components found:", ConsoleColor.Red);
            foreach (var component in autoAddedComponents)
            {
                var objectId = component.GetAttributeValue<Guid>("objectid");
                var componentType = component.GetAttributeValue<OptionSetValue>("componenttype")?.Value;
                output.WriteLine($"- Object ID: {objectId}, Component Type: {componentType}", ConsoleColor.Yellow);
            }
        }

        return autoAddedComponents;
    }

    private async Task PruneAutoAddedComponents(List<Entity> componentsToPrune)
    {
        output.WriteLine("Pruning auto-added components from the target solution...", ConsoleColor.Blue);

        foreach (var component in componentsToPrune.OrderByDescending(x => x.GetAttributeValue<OptionSetValue>("componenttype").Value))
        {
            var componentId = component.GetAttributeValue<Guid>("objectid");
            try
            {
                output.Write($"Pruning component {componentId}...");

                var request = new RemoveSolutionComponentRequest
                {
                    ComponentType = component.GetAttributeValue<OptionSetValue>("componenttype").Value,
                    ComponentId = componentId,
                    SolutionUniqueName = component.GetAliasedValue<string>("solution.uniquename")
                };

                await crm.ExecuteAsync(request);
                output.WriteLine("Done", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                output.WriteLine($"Failed to remove component {componentId}: {ex.Message}", ConsoleColor.Red);
            }
        }
    }
}
