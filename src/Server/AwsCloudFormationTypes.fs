module AwsCloudFormationTypes

open Amazon.EC2.Model
open Amazon.ElasticLoadBalancingV2.Model
open Amazon.IdentityManagement.Model
open Newtonsoft.Json.Linq

open System.Collections.Generic
open Shared


type RemappedSpecResult = {
    resourceType: string
    logicalId: string
    importId: string
}

type AwsResourceContext = {
    loadBalancers: Map<string,LoadBalancer>
    elasticIps: Map<string,Address>
    routeTables: List<RouteTable>
    iamPolicies: List<ManagedPolicy>
    gatewayAttachmentImportIds: Dictionary<string,string>
    securityGroupIngressRules: Map<string,JObject>
    securityGroupEgressRules: Map<string,JObject>
}
    with
        static member Empty = {
            loadBalancers = Map.empty
            elasticIps = Map.empty
            routeTables = ResizeArray()
            iamPolicies = ResizeArray()
            gatewayAttachmentImportIds = Dictionary()
            securityGroupIngressRules = Map.empty
            securityGroupEgressRules = Map.empty
        }

type CustomRemapSpecification = {
    pulumiType: string
    delimiter: string
    importIdentityParts: string list
    remapFunc: AwsCloudFormationResource -> Dictionary<string, Dictionary<string,string>> -> AwsResourceContext -> CustomRemapSpecification -> RemappedSpecResult
    validatorFunc: AwsCloudFormationResource -> Dictionary<string, Dictionary<string,string>> -> CustomRemapSpecification -> bool
}

type ImportIdentityResolver = {
    importIdentityParts: string list
    resolveImportIdentity: AwsCloudFormationResource -> Dictionary<string,string> -> AwsResourceContext -> Result<string, string>
}

type RemapSpecification = {
    pulumiType: string
    delimiter: string
    importIdentityParts: string list
}

type ImportIdentityParts = {
    delimiter: string
    importIdentityParts: string list
}

type CloudControlResourceDescription = {
    identifier: string
    properties: JObject
}



