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
        this.output.Write($"Connecting to the current dataverse environment...");
        crm = await this.organizationServiceRepository.GetCurrentConnectionAsync();
        this.output.WriteLine("Done", ConsoleColor.Green);

        var solutionComponents = await GetSolutionsComponents(command);

        var groupedSolutionComponentsBySolution = solutionComponents.Entities.GroupBy(x => x.GetAttributeValue<EntityReference>("solutionid").Id);

        await CheckExistingTargetSolutionOrCreateItAsync(command);

        AddComponentsToSolution(command, groupedSolutionComponentsBySolution);

        return CommandResult.Success();
    }

    private void AddComponentsToSolution(CopyCommand command, IEnumerable<IGrouping<Guid, Entity>> groupedSolutionComponentsBySolution)
    {
        foreach (var solution in groupedSolutionComponentsBySolution)
        {
            var solutionName = solution.First().GetAttributeValue<AliasedValue>("solution.friendlyname").Value.ToString();
            this.output.WriteLine($"Solution: {solutionName}", ConsoleColor.Yellow);

            foreach (var component in solution)
            {
                var componentType = component.GetAttributeValue<OptionSetValue>("componenttype").Value;
                var objectId = component.GetAttributeValue<Guid>("objectid");
                this.output.Write($"Copying Component Type: {componentType.ToString().PadRight(5)}, Object Id: {objectId}...");

                AddComponentToSolution(objectId, componentType, command.TargetSolution);

                this.output.WriteLine("Done", ConsoleColor.Green);
            }
        }
    }

    private async Task<EntityCollection> GetSolutionsComponents(CopyCommand command)
    {
        var solutionsComponentsQuery = new QueryExpression("solutioncomponent");

        solutionsComponentsQuery.ColumnSet = new ColumnSet("solutionid", "componenttype", "objectid");

        var linkedSolution = new LinkEntity("solutioncomponent", "solution", "solutionid", "solutionid", JoinOperator.Inner);
        linkedSolution.EntityAlias = "solution";

        linkedSolution.Columns.AddColumns("uniquename", "friendlyname");
        linkedSolution.LinkCriteria.AddCondition(new ConditionExpression("uniquename", ConditionOperator.In, command.SourceSolutions.Split(',').Select(s => s.Trim()).ToArray()));

        solutionsComponentsQuery.LinkEntities.Add(linkedSolution);

        var results = await crm.RetrieveMultipleAsync(solutionsComponentsQuery);
        return results;
    }

    public void AddComponentToSolution(Guid objectId, int componenType, string solutionUniqueName)
    {
        var addSolutionComponentRequest = new AddSolutionComponentRequest();

        addSolutionComponentRequest.ComponentType = componenType;
        addSolutionComponentRequest.ComponentId = objectId;
        addSolutionComponentRequest.SolutionUniqueName = solutionUniqueName;
        addSolutionComponentRequest.AddRequiredComponents = false;

        if (componenType == 1)
            addSolutionComponentRequest.DoNotIncludeSubcomponents = true;

        if (componenType == 20)
            addSolutionComponentRequest.DoNotIncludeSubcomponents = false;

        crm.Execute(addSolutionComponentRequest);
    }

    public async Task CheckExistingTargetSolutionOrCreateItAsync(CopyCommand command)
    {
        EntityCollection solutionResults = await CheckIfTargetSolutionExistsAsync(command.TargetSolution);

        if (solutionResults.Entities.Count > 1)
            throw new Exception($"There are multiple solutions with the name {command.TargetSolution}");

        if (solutionResults.Entities.Count == 1)
        {
            this.output.WriteLine($"The target solution {command.TargetSolution} exists and it will be used as the target solution", ConsoleColor.Green);
            return;
        }

        if (command.PublisherPrefix == null)
            throw new Exception($"The target solution {command.TargetSolution} does not exist and no publisher was provided");

        var publishers = await GetPublisherIdByPublisherPrefixAsync(command);

        if (publishers.Entities.Count == 0)
            throw new Exception($"No publisher found with the prefix {command.PublisherPrefix}");

        if (publishers.Entities.Count > 1)
            throw new Exception($"There are multiple publishers with the prefix {command.PublisherPrefix}");

        CreateSolution(command.TargetSolution, publishers.Entities[0].Id);

        this.output.WriteLine($"Solution {command.TargetSolution} created with publisher prefix {command.PublisherPrefix}", ConsoleColor.Green);
    }

    private async Task<EntityCollection> GetPublisherIdByPublisherPrefixAsync(CopyCommand command)
    {
        var publisherQuery = new QueryExpression("publisher");
        publisherQuery.NoLock = true;
        publisherQuery.Criteria.AddCondition(new ConditionExpression("customizationprefix", ConditionOperator.Equal, command.PublisherPrefix));
        return await crm.RetrieveMultipleAsync(publisherQuery);
    }

    private void CreateSolution(string solutionName, Guid publisherId)
    {
        var targetSolution = new Entity("solution");
        targetSolution["uniquename"] = solutionName;
        targetSolution["friendlyname"] = solutionName;
        targetSolution["version"] = "1.0.0.0";
        targetSolution["publisherid"] = new EntityReference("publisher", publisherId);

        crm.Create(targetSolution);
    }

    private async Task<EntityCollection> CheckIfTargetSolutionExistsAsync(string name)
    {
        var solutionQuery = new QueryExpression("solution");
        solutionQuery.ColumnSet = new ColumnSet("solutionid", "uniquename", "friendlyname");

        solutionQuery.Criteria.Filters.Add(new FilterExpression(LogicalOperator.Or));

        solutionQuery.Criteria.AddCondition(new ConditionExpression("uniquename", ConditionOperator.Equal, name));
        solutionQuery.Criteria.AddCondition(new ConditionExpression("friendlyname", ConditionOperator.Equal, name));
        var solutionResults = await crm.RetrieveMultipleAsync(solutionQuery);
        return solutionResults;
    }
}
