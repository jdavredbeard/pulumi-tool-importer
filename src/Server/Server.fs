module Server

open System
open System.IO
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Newtonsoft.Json
open Saturn
open CliWrap
open CliWrap.Buffered
open Newtonsoft.Json.Linq
open Shared
open System.Threading.Tasks
open Octokit
open Amazon
open Amazon.ResourceExplorer2
open Amazon.ResourceExplorer2.Model
open Amazon.Runtime
open Amazon.SecurityToken
open Amazon.SecurityToken.Model
open Azure.Identity
open Azure.ResourceManager

let github = new GitHubClient(ProductHeaderValue "PulumiBot")

let githubClient() =
    let githubToken = Environment.GetEnvironmentVariable "GITHUB_TOKEN"
    if String.IsNullOrWhiteSpace(githubToken) then
        github
    else
        github.Credentials <- Octokit.Credentials(githubToken)
        github

let resourceExplorerClient() =
    let credentials = EnvironmentVariablesAWSCredentials()
    new AmazonResourceExplorer2Client(credentials)

let securityTokenServiceClient() =
    let credentials = EnvironmentVariablesAWSCredentials()
    new AmazonSecurityTokenServiceClient(credentials)

let armClient() = ArmClient(AzureCliCredential())

let getResourceGroups() = task {
    try
        let subscription = armClient().GetDefaultSubscription()
        let groups = subscription.GetResourceGroups()
        return Ok [ for group in groups -> group.Id.Name ]
    with
    | ex ->
        let errorType = ex.GetType().Name
        return Error $"{errorType}: {ex.Message}"
}

let getCallerIdentity() = task {
    try
        let client = securityTokenServiceClient()
        let! response = client.GetCallerIdentityAsync(GetCallerIdentityRequest())

        if response.HttpStatusCode <> System.Net.HttpStatusCode.OK then
            return Error $"Failed to get caller identity: {response.HttpStatusCode}"
        else
            return Ok {
                accountId = response.Account
                userId = response.UserId
                arn = response.Arn
            }
    with
    | error ->
        let errorType = error.GetType().Name
        return Error $"{errorType}: {error.Message}"
}

let rec searchAws' (request: SearchRequest) (nextToken: string option) = task {
   let resources = ResizeArray()
   let explorer = resourceExplorerClient()
   nextToken |> Option.iter (fun token -> request.NextToken <- token)
   let! response = explorer.SearchAsync(request)
   resources.AddRange response.Resources
   if not (isNull response.NextToken) then
       let! next = searchAws' request (Some response.NextToken)
       resources.AddRange next
   return resources
}

let capitalize (input: string) =
    match input with
    | null -> ""
    | "" -> ""
    | _ -> input.[0].ToString().ToUpper() + input.Substring(1)

let normalizeTypeName (input: string) =
    [ for part in input.Split "-" -> capitalize part ]
    |> String.concat ""

let normalizeModuleName (input: string) =
    [ for part in input.Split "-" -> part ]
    |> String.concat ""

let awsResourceId (resource: Amazon.ResourceExplorer2.Model.Resource) : string =
    let arn = Arn.Parse resource.Arn
    let id = arn.Resource
    match resource.ResourceType.Split ":" with
    | [| serviceName; resourceType |] ->
        if id.StartsWith resourceType then
            id.Substring(resourceType.Length + 1)
        else
            id
    | _ ->
        id

let searchAws (query: string) = task {
    try
        let! results = searchAws' (SearchRequest(QueryString=query, MaxResults=1000)) None
        let resources = [
            for resource in results -> {
                resourceType = resource.ResourceType
                resourceId = awsResourceId resource
                region = resource.Region
                service = resource.Service
                arn = resource.Arn
                owningAccountId = resource.OwningAccountId
            }
        ]

        let pulumiImportJson = JObject()
        let resourcesJson = JArray()
        for resource in results do
            let resourceJson = JObject()
            match resource.ResourceType.Split ":" with
            | [| serviceName; resourceType |] ->
                let pulumiType = $"aws:{serviceName}/{normalizeModuleName resourceType}:{normalizeTypeName resourceType}"
                resourceJson.Add("type", pulumiType)
            | _ ->
                resourceJson.Add("type", $"aws:{resource.ResourceType}")

            let resourceId = awsResourceId resource
            resourceJson.Add("id", resourceId)
            resourceJson.Add("name", resourceId.Replace("-", "_"))
            resourcesJson.Add(resourceJson)

        pulumiImportJson.Add("resources", resourcesJson)

        return Ok {
            resources = resources
            pulumiImportJson = pulumiImportJson.ToString(Formatting.Indented)
        }
    with
    | error ->
        let errorType = error.GetType().Name
        return Error $"{errorType}: {error.Message}"
}


let rec searchGithub (term: string) =
    task {
        try
            let request = SearchRepositoriesRequest term
            let! searchResults = githubClient().Search.SearchRepo(request)
            let bestResults =
                searchResults.Items
                |> Seq.map (fun repo -> repo.FullName)

            return RateLimited.Response(List.ofSeq bestResults)
        with
            | :? RateLimitExceededException as error ->
                return RateLimited.RateLimitReached
    }

let version (release: Release) =
    if not (String.IsNullOrWhiteSpace(release.Name)) then
        Some (release.Name.Substring(1, release.Name.Length - 1))
    elif not (String.IsNullOrWhiteSpace(release.TagName)) then
        Some (release.TagName.Substring(1, release.TagName.Length - 1))
    else
        None

let findGithubReleases (repo: string) =
    task {
        try
            match repo.Split "/" with
            | [| owner; repoName |] ->
                let! releases = githubClient().Repository.Release.GetAll(owner, repoName)
                return
                    List.choose id [ for release in releases -> version release ]
                    |> RateLimited.Response
            | _ ->
                return
                    RateLimited.Response []
        with
        | :? RateLimitExceededException as error ->
               return RateLimited.RateLimitReached
    }

let pulumiCliBinary() : Task<string> = task {
    try
        // try to get the version of pulumi installed on the system
        let! pulumiVersionResult =
            Cli.Wrap("pulumi")
                .WithArguments("version")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync()

        let version = pulumiVersionResult.StandardOutput.Trim()
        let versionRegex = Text.RegularExpressions.Regex("v[0-9]+\\.[0-9]+\\.[0-9]+")
        if versionRegex.IsMatch version then
            return "pulumi"
        else
            return! failwith "Pulumi not installed"
    with
    | error ->
        printfn "%A" error
        // when pulumi is not installed, try to get the version of of the dev build
        // installed on the system using `make install` in the pulumi repo
        let homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        let pulumiPath = System.IO.Path.Combine(homeDir, ".pulumi-dev", "bin", "pulumi")
        if System.IO.File.Exists pulumiPath then
            return pulumiPath
        elif System.IO.File.Exists $"{pulumiPath}.exe" then
            return $"{pulumiPath}.exe"
        else
            return "pulumi"
}

let rec getSchemaVersionsFromGithub (owner, repo) =
    task {
        try
            let client = githubClient()
            let! repository = client.Repository.Get(owner, repo)
            let! releases = client.Repository.Release.GetAll(repository.Id)
            return
                releases
                |> Seq.choose (fun release -> version release)
                |> List.ofSeq
                |> RateLimited.Response
        with
            | :? RateLimitExceededException ->
                return RateLimited.RateLimitReached
    }

let getPulumiVersion() = task {
    let! binary = pulumiCliBinary()
    let! output = Cli.Wrap(binary).WithArguments("version").ExecuteBufferedAsync()
    return output.StandardOutput
}


let azureAccount() = task {
    try
        let! output = Cli.Wrap("az").WithArguments("account show").ExecuteBufferedAsync()
        if output.ExitCode = 0 then
            let json = JObject.Parse(output.StandardOutput)
            return Ok {
                subscriptionId = json.["id"].ToObject<string>()
                subscriptionName =  json.["name"].ToObject<string>()
                userName =
                    if json.ContainsKey "user" then
                        json.["user"].["name"].ToObject<string>()
                    else
                        ""
            }
        else
            return Error $"Error while called 'az account show': {output.StandardError}"
    with
    | error ->
        let errorType = error.GetType().Name
        return Error $"{errorType}: {error.Message}"
}



let getResourcesUnderResourceGroup (resourceGroupName: string) = task {
    try
        let subscription = armClient().GetDefaultSubscription()
        let resourceGroup = subscription.GetResourceGroup(resourceGroupName)
        if not resourceGroup.HasValue then
            return Error "Could not find the resource group"
        else
            let resources: AzureResource list = [
                for page in resourceGroup.Value.GetGenericResources().AsPages() do
                for resource in page.Values do
                    yield {
                        resourceId = resource.Data.Id.ToString()
                        resourceType = resource.Id.ResourceType.ToString()
                        name = resource.Id.Name
                    }
            ]

            let pulumiImportJson = JObject()
            let resourcesJson = JArray()
            let resourceGroupJson = JObject()
            resourceGroupJson.Add("type", "azure-native:resources:ResourceGroup")
            resourceGroupJson.Add("name", resourceGroupName)
            resourceGroupJson.Add("id", resourceGroup.Value.Id.ToString())
            resourcesJson.Add(resourceGroupJson)

            for resource in resources do
                let resourceJson = JObject()
                resourceJson.Add("type", AzureResourceTokens.fromAzureSpecToPulumi resource.resourceType)
                resourceJson.Add("name", resource.name)
                resourceJson.Add("id", resource.resourceId)
                resourcesJson.Add(resourceJson)

            pulumiImportJson.Add("resources", resourcesJson)

            return Ok {
                azureResources = resources
                pulumiImportJson = pulumiImportJson.ToString(Formatting.Indented)
            }

    with
    | error ->
        let errorType = error.GetType().Name
        return Error $"{errorType}: {error.Message}"
}

let tempDirectory (f: string -> 't) =
    let tempDir = System.IO.Path.GetTempPath()
    let dir = System.IO.Path.Combine(tempDir, $"pulumi-test-{Guid.NewGuid()}")
    try
        let info = System.IO.Directory.CreateDirectory dir
        f info.FullName
    finally
        System.IO.Directory.Delete(dir, true)

let importPreview (request: ImportPreviewRequest) = task {
    try
        let! pulumiCli = pulumiCliBinary()
        let response = tempDirectory <| fun tempDir ->
            let exec (args:string) =
                Cli.Wrap(pulumiCli)
                   .WithWorkingDirectory(tempDir)
                   .WithArguments(args)
                   .WithEnvironmentVariables(fun config ->
                       config.Set("PULUMI_CONFIG_PASSPHRASE", "whatever").Build()
                       |> ignore)
                   .WithValidation(CommandResultValidation.None)
                   .ExecuteBufferedAsync()
                   .GetAwaiter()
                   .GetResult()

            let newCommandArgs = $"new {request.language} --yes --generate-only"
            let pulumiNewOutput = exec newCommandArgs

            if pulumiNewOutput.ExitCode <> 0 then
                Error $"Error occurred while running 'pulumi {newCommandArgs}' command: {pulumiNewOutput.StandardError}"
            else
            Path.Combine(tempDir, "state") |> Directory.CreateDirectory |> ignore
            let pulumiLoginOutput = exec $"login file://./state"
            if pulumiLoginOutput.ExitCode <> 0 then
                Error $"Error occurred while running 'pulumi login file://./state' command: {pulumiLoginOutput.StandardError}"
            else

            let initStack = exec $"stack init dev"
            if initStack.ExitCode <> 0 then
                Error $"Error occurred while running 'pulumi stack init dev' command: {initStack.StandardError}"
            else
            let importFilePath = Path.Combine(tempDir, "import.json")
            File.WriteAllText(importFilePath, request.pulumiImportJson)

            let generatedCodePath = System.IO.Path.Combine(tempDir, "generated.txt")
            let pulumiImportOutput = exec $"import --file {importFilePath} --yes --out {generatedCodePath}"
            if pulumiImportOutput.ExitCode <> 0 then
                Error $"Error occurred while running 'pulumi import --file {importFilePath} --yes --out {generatedCodePath}' command: {pulumiImportOutput.StandardError}"
            else
            let generatedCode = System.IO.File.ReadAllText(generatedCodePath)
            Ok { generatedCode = generatedCode; stackState = "{}" }

        return response
    with
    | error ->
        let errorType = error.GetType().Name
        return Error $"{errorType}: {error.Message}"
}

let schemaExplorerApi = {
    getPulumiVersion = getPulumiVersion >> Async.AwaitTask
    awsCallerIdentity = getCallerIdentity >> Async.AwaitTask
    searchAws = searchAws >> Async.AwaitTask
    getResourceGroups = getResourceGroups >> Async.AwaitTask
    azureAccount = azureAccount >> Async.AwaitTask
    getResourcesUnderResourceGroup = getResourcesUnderResourceGroup >> Async.AwaitTask
    importPreview = importPreview >> Async.AwaitTask
}

let pulumiSchemaDocs = Remoting.documentation "Pulumi Schema Explorer" [ ]

let webApp =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder Route.builder
    |> Remoting.withErrorHandler (fun error routeInfo ->
        printfn "%A" error
        Ignore
    )
    |> Remoting.fromValue schemaExplorerApi
    |> Remoting.withDocs "/api/docs" pulumiSchemaDocs
    |> Remoting.buildHttpHandler

let getExecutingAssembly() =
    let assembly = System.Reflection.Assembly.GetExecutingAssembly()
    System.IO.DirectoryInfo(assembly.Location).Parent.FullName

let app = application {
    use_router webApp
    memory_cache
    use_static (System.IO.Path.Combine(getExecutingAssembly(), "public"))
    use_gzip
}

[<EntryPoint>]
let main _ =
    run app
    0