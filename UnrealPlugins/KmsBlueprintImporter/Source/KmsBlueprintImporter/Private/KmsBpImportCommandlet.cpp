#include "KmsBpImportCommandlet.h"

#include "AssetRegistry/AssetData.h"
#include "EdGraph/EdGraph.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "K2Node_VariableSet.h"
#include "KmsBpImporterLibrary.h"
#include "UObject/SoftObjectPath.h"

namespace
{
static int32 CountLinkedExecPins(const UEdGraphNode* Node, const EEdGraphPinDirection Direction)
{
    if (Node == nullptr)
    {
        return 0;
    }

    int32 Count = 0;
    for (const UEdGraphPin* Pin : Node->Pins)
    {
        if (Pin != nullptr
            && Pin->Direction == Direction
            && Pin->PinType.PinCategory == UEdGraphSchema_K2::PC_Exec
            && Pin->LinkedTo.Num() > 0)
        {
            ++Count;
        }
    }
    return Count;
}

static bool ValidateGeneratedExecLinks(const TArray<FString>& ImportedAssets)
{
    bool bOk = true;
    for (const FString& ImportedAsset : ImportedAssets)
    {
        UBlueprint* Blueprint = Cast<UBlueprint>(FSoftObjectPath(ImportedAsset).TryLoad());
        if (Blueprint == nullptr)
        {
            UE_LOG(LogTemp, Error, TEXT("ValidateExec: failed to load blueprint %s"), *ImportedAsset);
            bOk = false;
            continue;
        }

        TArray<UEdGraph*> Graphs;
        Graphs.Append(Blueprint->FunctionGraphs);
        Graphs.Append(Blueprint->UbergraphPages);

        for (UEdGraph* Graph : Graphs)
        {
            if (Graph == nullptr)
            {
                continue;
            }

            for (UEdGraphNode* Node : Graph->Nodes)
            {
                const UK2Node_VariableSet* SetNode = Cast<UK2Node_VariableSet>(Node);
                if (SetNode == nullptr)
                {
                    continue;
                }

                const int32 InputLinks = CountLinkedExecPins(SetNode, EGPD_Input);
                const int32 OutputLinks = CountLinkedExecPins(SetNode, EGPD_Output);
                UE_LOG(
                    LogTemp,
                    Display,
                    TEXT("ValidateExec: %s.%s %s at (%d,%d) exec-in=%d exec-out=%d"),
                    *ImportedAsset,
                    *Graph->GetName(),
                    *SetNode->GetNodeTitle(ENodeTitleType::ListView).ToString(),
                    SetNode->NodePosX,
                    SetNode->NodePosY,
                    InputLinks,
                    OutputLinks);

                if (InputLinks == 0 || OutputLinks == 0)
                {
                    UE_LOG(
                        LogTemp,
                        Error,
                        TEXT("ValidateExec: disconnected set node in %s.%s: %s"),
                        *ImportedAsset,
                        *Graph->GetName(),
                        *SetNode->GetNodeTitle(ENodeTitleType::ListView).ToString());
                    bOk = false;
                }
            }
        }
    }
    return bOk;
}
}

UKmsBpImportCommandlet::UKmsBpImportCommandlet()
{
    IsClient = false;
    IsEditor = true;
    IsServer = false;
    LogToConsole = true;
}

int32 UKmsBpImportCommandlet::Main(const FString& Params)
{
    FString JsonPath;
    if (!FParse::Value(*Params, TEXT("Json="), JsonPath) && !FParse::Value(*Params, TEXT("json="), JsonPath))
    {
        UE_LOG(LogTemp, Error, TEXT("Missing -Json=<path>."));
        return 1;
    }

    FKmsBpImportOptions Options;
    Options.bCompile = !FParse::Param(*Params, TEXT("NoCompile"));
    Options.bSave = !FParse::Param(*Params, TEXT("NoSave"));
    Options.bClearGeneratedComponents = !FParse::Param(*Params, TEXT("KeepComponents"));
    Options.bCreateProcedureGraphs = !FParse::Param(*Params, TEXT("NoGraphs"));

    FKmsBpImportResult Result;
    const bool bSuccess = UKmsBpImporterLibrary::ImportKmsBlueprintJson(JsonPath, Options, Result);
    for (const FString& Message : Result.Messages)
    {
        UE_LOG(LogTemp, Display, TEXT("%s"), *Message);
    }

    for (const FString& ImportedAsset : Result.ImportedAssets)
    {
        UE_LOG(LogTemp, Display, TEXT("Imported: %s"), *ImportedAsset);
    }

    if (bSuccess && FParse::Param(*Params, TEXT("ValidateExec")) && !ValidateGeneratedExecLinks(Result.ImportedAssets))
    {
        return 3;
    }

    return bSuccess ? 0 : 2;
}
