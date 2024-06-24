module Aws

open System.Collections.Generic
open PulumiSchema
open PulumiSchema.Types
open HtmlAgilityPack
open System.Threading.Tasks

let inferAncestorTypes(schemaVersion: string) =

    let knownAncestorTypes = Map.ofList [
        "aws:lb/loadBalancer:LoadBalancer", [
            "aws:ec2/vpc:Vpc"
            "aws:ec2/securityGroup:SecurityGroup"
        ]

        "aws:lb/listener:Listener", [
            "aws:lb/targetGroup:TargetGroup"
        ]

        "aws:vpc/securityGroupEgressRule:SecurityGroupEgressRule", [
            "aws:ec2/securityGroup:SecurityGroup"
        ]

        "aws:vpc/securityGroupIngressRule:SecurityGroupIngressRule", [
            "aws:ec2/securityGroup:SecurityGroup"
        ]
    ]

    let ancestorTypesByProperty = Map.ofList [
        "vpcId", "aws:ec2/vpc:Vpc"
        "loadBalancerArn", "aws:lb/loadBalancer:LoadBalancer"
        "securityGroups", "aws:ec2/securityGroup:SecurityGroup"
    ]

    match SchemaLoader.FromPulumi("aws", schemaVersion) with
    | Error error ->
        failwith $"Error while loading AWS schema version {schemaVersion}: {error}"
    | Ok schema ->
        let ancestorsByType = Dictionary<string, ResizeArray<string>>()
        let availableTypes = [ for (resourceType, _) in Map.toList schema.resources -> resourceType ]
        for (resourceType, resourceSchema) in Map.toList schema.resources do
            let ancestors = ResizeArray<string>()
            match Map.tryFind resourceType knownAncestorTypes with
            | Some ancestorTypes ->
                ancestors.AddRange ancestorTypes
            | None -> ()

            for (propertyName, propertySchema) in Map.toList resourceSchema.properties do
                match Map.tryFind propertyName ancestorTypesByProperty with
                | Some ancestorType ->
                    ancestors.Add ancestorType
                | None -> ()

            if ancestors.Count > 0 then
                let distinctAncestors = ancestors |> Seq.distinct |> ResizeArray
                ancestorsByType.Add(resourceType, distinctAncestors)

        ancestorsByType, availableTypes

type AwsImportSpec = {
    resourceType: string
    requiresArn: bool
    importInstructions: string list
}

let loadAwsImportSpec(resourceType: string, moduleName:string, resourceName:string) = task {
     try
         let url = $"https://www.pulumi.com/registry/packages/aws/api-docs/{moduleName.ToLower()}/{resourceName.ToLower()}"
         let web = HtmlWeb()
         let! doc = web.LoadFromWebAsync(url)
         let codeElements = doc.DocumentNode.DescendantsAndSelf()
         let importInstructions =
             codeElements
             |> Seq.filter (fun element ->
                 element.Name = "code"
                 && element.InnerText <> null
                 && element.InnerText.StartsWith("$ pulumi import"))
             |> Seq.map (fun element -> element.InnerText)
             |> Seq.toList

         return Some {
             resourceType = resourceType
             requiresArn = importInstructions |> List.exists (fun instruction -> instruction.Contains "arn:")
             importInstructions = importInstructions
         }
     with
     | ex ->
         printfn $"Error while scraping import instructions for {resourceType}: {ex.Message}"
         return None
}

let scrapeAwsImportSpecs (availableTypes: string list) = task {
    let scrapeTasks =
        availableTypes
        |> Seq.choose (fun resourceType ->
            match resourceType.Split ':' with
            | [| _; moduleName; resourceName |] ->
                match moduleName.Split "/" with
                | [| moduleName; resource |] ->
                    Some (loadAwsImportSpec(resourceType, moduleName, resourceName))
                | _ ->
                    None
            | _ ->
                None)

    let results = ResizeArray()
    let batches = Seq.chunkBySize 50 scrapeTasks
    for batch in batches do
        do! Task.Delay 1000
        let! taskResults = Task.WhenAll batch
        results.AddRange(Array.choose id taskResults)

    return results
}

let generateLookupModule(schemaVersion) =
    printfn $"Loading schema types for AWS schema version {schemaVersion}"
    let (ancestorsByType, availableTypes) = inferAncestorTypes schemaVersion
    printfn "Scraping AWS import specifications"
    let importSpecs = (scrapeAwsImportSpecs availableTypes).GetAwaiter().GetResult()
    let moduleBuilder = System.Text.StringBuilder()
    let append (line: string) = moduleBuilder.AppendLine line |> ignore
    append "// This file is auto generated using the build project, do not edit this file directly."
    append "// To regenerate it, run `dotnet run GenerateAwsSchemaTypes` in the root of the repository."
    append $"// generated from AWS schema version {schemaVersion}"
    append "[<RequireQualifiedAccess>]"
    append "module AwsSchemaTypes"
    append ""
    append "let ancestorsByType = Map.ofList ["
    for pair in ancestorsByType do
        let resourceType = pair.Key
        let ancestorTypes = pair.Value

        append $"    \"{resourceType}\", ["
        for ancestorType in ancestorTypes do
            append $"        \"{ancestorType}\""
        append "    ]"
    append "]"
    append ""
    append "let availableTypes = set [|"
    for resourceType in availableTypes do
        append $"    \"{resourceType}\""
    append "|]"
    let sortedImportSpecs =
        importSpecs
        |> Seq.sortBy (fun spec -> spec.resourceType)
        |> Seq.toArray
    append ""
    append "let typeRequiresFullArnToImport = set [|"
    for spec in sortedImportSpecs do
        if spec.requiresArn then
            append $"    \"{spec.resourceType}\""
    append "|]"

    let importIdRequiringMultiplePieces (code: string) =
        let parts = code.Split(" ")
        if parts.Length > 0 then
            let last = parts.[parts.Length - 1]
            last.Contains("/") || last.Contains("_")
        else
            false

    // when printing our odd import format resources, we want to filter out resources
    // that we know not requiring multiple pieces to import OR we have already handled it in the importer
    let skip = set [
        // skipping this because we map to either an egress or ingress rule
        "aws:ec2/securityGroupRule:SecurityGroupRule"
        // these look like they just need an ID
        "aws:dax/subnetGroup:SubnetGroup"
        "aws:ssm/parameter:Parameter"
        "aws:cognito/userPool:UserPool"
        "aws:cloudfront/function:Function"
        "aws:cloudfront/keyValueStore:KeyValueStore"
        "aws:cloudwatch/dashboard:Dashboard"
        "aws:cloudwatch/logDestination:LogDestination"
        "aws:cloudwatch/logDestinationPolicy:LogDestinationPolicy"
        "aws:dax/cluster:Cluster"
        "aws:dax/parameterGroup:ParameterGroup"
        "aws:dax/subnetGroup:SubnetGroup"
        "aws:devopsguru/resourceCollection:ResourceCollection"
        "aws:eks/cluster:Cluster"
        "aws:elasticache/cluster:Cluster"
        "aws:elasticache/serverlessCache:ServerlessCache"
        "aws:elasticsearch/domain:Domain"
        "aws:elasticsearch/domainSamlOptions:DomainSamlOptions"
        "aws:glacier/vault:Vault"
        "aws:iam/role:Role" // TODO:verify this
        "aws:keyspaces/keyspace:Keyspace"
        "aws:lambda/function:Function" // TODO:verify this
        "aws:lambda/functionUrl:FunctionUrl" // TODO:verify this
        "aws:opensearch/domain:Domain"
        "aws:opensearch/domainSamlOptions:DomainSamlOptions"
        "aws:schemas/registry:Registry"
        "aws:ses/activeReceiptRuleSet:ActiveReceiptRuleSet"
        // these we have handled in the importer
        "aws:sqs/queue:Queue"
    ]

    let oddlyFormattedImportSpecs =
        sortedImportSpecs
        |> Array.filter (fun spec ->
            not spec.requiresArn
            && List.exists importIdRequiringMultiplePieces spec.importInstructions
            && not (skip.Contains spec.resourceType))

    append ""
    append $"// The following {oddlyFormattedImportSpecs.Length} resources could require an odd format to import"
    append "// an odd format being something other than just the resource ID or resource ARN"
    append ""
    append "let resourcesWithOddImportFormat = Map.ofList ["
    for spec in oddlyFormattedImportSpecs do
        if spec.importInstructions.Length > 0 then
            append $"    \"{spec.resourceType}\", ["
            for instruction in spec.importInstructions do
                append $"        \"{instruction.TrimEnd()}\""
            append "    ]"
    append "]"
    moduleBuilder.ToString()