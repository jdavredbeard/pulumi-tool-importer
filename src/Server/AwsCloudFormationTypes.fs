module AwsCloudFormationTypes

open System.Collections.Generic
open Shared

type RemappedSpecResult = {
    resourceType: string
    logicalId: string
    importId: string
}

type CustomRemapSpecification = {
    pulumiType: string
    delimiter: string
    importIdentityParts: string list
    remapFunc: AwsCloudFormationResource -> Dictionary<string, Dictionary<string,string>> -> CustomRemapSpecification -> RemappedSpecResult
    validatorFunc: AwsCloudFormationResource -> Dictionary<string, Dictionary<string,string>> -> CustomRemapSpecification -> bool
}

type RemapSpecification = {
    pulumiType: string
    delimiter: string
    importIdentityParts: string list
}



