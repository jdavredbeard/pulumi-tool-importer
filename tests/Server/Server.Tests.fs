module Server.Tests

open System.Collections.Generic
open Newtonsoft.Json.Linq
open Expecto

open AwsCloudFormationTypes
open Shared
open Server

let syntax = testList "syntax" [ 
    test "A simple test" {
        let subject = "Hello World"
        Expect.equal subject "Hello World" "The strings should be equal"
    }
    test "using Maps" {
        let myMap = Map [("blah","boink"); ("foo","bar")]
        let myNestedMap = Map [("nested", myMap)]
        Expect.equal (myMap.TryGetValue "blah") (true, "boink") "That's not how maps work"
        Expect.equal (myMap.TryGetValue "forp") (false, null) "That's not how maps work"
        Expect.equal (myMap |> Map.tryFind "foo") (Some "bar") "That's not how maps work"
        Expect.equal myNestedMap.["nested"].["foo"] "bar" "That's not how maps work"
    }    
]

let getImportIdentityParts = testList "getImportIdentityParts" [
    test "resourceType not in remapSpec returns None" {
        Expect.equal (getImportIdentityParts "foo") None ""
    }
    test "resourceType in remapSpec returns Some(parts)" {
        Expect.equal (getImportIdentityParts "AWS::S3::BucketPolicy") (Some ["Bucket"]) ""
    }
]

let addImportIdentityParts = testList "addImportIdentityParts" [
    test "adds defined string props to resourceData" {
        let resourceId = "myResource"
        let resourceType = "AWS::S3::BucketPolicy"
        let properties = JObject()
        properties.Add("Bucket", "bucketName")
        let data = Dictionary<string, Dictionary<string,string>>()
        addImportIdentityParts resourceId resourceType properties data
        Expect.equal ((data["myResource"])["Bucket"]) "bucketName" ""
    }
    test "adds defined strings and json props as strings to resourceData" {
        let resourceId = "myResource"
        let resourceType = "AWS::ElasticLoadBalancingV2::ListenerCertificate"
        
        let properties = JObject()
        properties.Add("ListenerArn", "listenerArn")
        let certificate = JObject()
        certificate.Add("CertificateArn", "certificateArn")
        let certificates = JArray()
        certificates.Add(certificate)
        properties.Add("Certificates", certificates)

        let data = Dictionary<string, Dictionary<string,string>>()
        
        addImportIdentityParts resourceId resourceType properties data
        Expect.equal ((data["myResource"])["ListenerArn"]) "listenerArn" ""
        let expected = "[
  {
    \"CertificateArn\": \"certificateArn\"
  }
]"
        Expect.equal ((data["myResource"])["Certificates"]) expected ""
    }
]

let getRemappedImportProps = testList "getRemappedImportProps" [
    test "resourceType not in remapSpec returns None" {
        let resource = {
                logicalId = "foo"
                resourceId = "bar"
                resourceType = "boink"
        }
        let resourceData = Dictionary<string, Dictionary<string, string>>()
        Expect.equal (getRemappedImportProps resource resourceData) None ""
    }
    test "resourceType in remapSpec with missing resourceData returns None" {
        let resource = {
                logicalId = "foo"
                resourceId = "bar"
                resourceType = "AWS::ApiGateway::Deployment"
        }
        let resourceData = Dictionary<string, Dictionary<string, string>>()
        Expect.equal (getRemappedImportProps resource resourceData) None ""
    }
    test "ApiGateway Deployment remaps with remapFromImportIdentityParts)" {
        let resource = {
            logicalId = "foo-foo"
            resourceId = "bar"
            resourceType = "AWS::ApiGateway::Deployment"
        }
        let resourceData = Dictionary<string, Dictionary<string,string>>()
        resourceData.Add("foo-foo", Dictionary<string,string>())
        resourceData["foo-foo"].Add("RestApiId", "fonce")
        resourceData["foo-foo"].Add("Id", "foo-foo")
        let expectedResult : RemappedSpecResult = {
            resourceType = "aws:apigateway/deployment:Deployment"
            logicalId = "foo_foo"
            importId = "fonce/foo-foo"
        }
        Expect.equal (getRemappedImportProps resource resourceData) (Some(expectedResult)) ""
    }
    test "ECS Service remaps with remapFromIdAsArn" {
        let logicalId = "myService"
        let resource = {
            logicalId = logicalId
            resourceId = "arn:aws:ecs:us-west-2:051081605780:service/dev2-environment-ECSCluster-2JDFODYBOS1/dev2-dropbeacon-ECSServiceV2-zFfDnUyTGIgg"
            resourceType = "AWS::ECS::Service"
        }
        let resourceData = Dictionary<string, Dictionary<string,string>>()
        resourceData.Add(logicalId, Dictionary<string,string>())
        resourceData[logicalId].Add("Id", "arn:aws:ecs:us-west-2:051081605780:service/dev2-environment-ECSCluster-2JDFODYBOS1/dev2-dropbeacon-ECSServiceV2-zFfDnUyTGIgg")
        let expectedResult : RemappedSpecResult = {
            resourceType = "aws:ecs/service:Service"
            logicalId = "myService"
            importId = "dev2-environment-ECSCluster-2JDFODYBOS1/dev2-dropbeacon-ECSServiceV2-zFfDnUyTGIgg"
        } 
        Expect.equal (getRemappedImportProps resource resourceData) (Some(expectedResult)) ""
    }
    test "Listener Certificate remaps with remapFromImportIdentityPartsListenerCertificate" {
        let logicalId = "myListenerCertificate"
        let resource = {
            logicalId = logicalId
            resourceId = "foo"
            resourceType = "AWS::ElasticLoadBalancingV2::ListenerCertificate"
        }
        let resourceData = Dictionary<string, Dictionary<string,string>>()
        resourceData.Add(logicalId, Dictionary<string,string>())
        resourceData[logicalId].Add("Id", "foo")
        let certificates = "[
  {
    \"CertificateArn\": \"certificateArn\"
  }
]"
        resourceData[logicalId].Add("Certificates", certificates)
        resourceData[logicalId].Add("ListenerArn", "listenerArn")
        let expectedResult : RemappedSpecResult = {
            resourceType = "aws:lb/listenerCertificate:ListenerCertificate"
            logicalId = "myListenerCertificate"
            importId = "listenerArn_certificateArn"
        } 
        Expect.equal (getRemappedImportProps resource resourceData) (Some(expectedResult)) ""
    }
]

let all =
    testList "All"
        [
            Shared.Tests.shared
            syntax
            getRemappedImportProps
            getImportIdentityParts
            addImportIdentityParts
        ]

[<EntryPoint>]
let main _ = runTestsWithCLIArgs [] [||] all