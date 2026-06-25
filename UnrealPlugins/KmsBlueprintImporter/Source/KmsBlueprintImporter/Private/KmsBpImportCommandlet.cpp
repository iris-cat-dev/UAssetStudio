#include "KmsBpImportCommandlet.h"

#include "AssetRegistry/AssetData.h"
#include "EdGraph/EdGraph.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "FileHelpers.h"
#include "K2Node_VariableSet.h"
#include "K2Node_KmsGeneratedNode.h"
#include "KmsBpImporterLibrary.h"
#include "Misc/FileHelper.h"
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

static FString DescribeExecLinks(const UEdGraphNode* Node, const EEdGraphPinDirection Direction)
{
    TArray<FString> Links;
    if (Node == nullptr)
    {
        return TEXT("");
    }

    for (const UEdGraphPin* Pin : Node->Pins)
    {
        if (Pin == nullptr || Pin->Direction != Direction || Pin->PinType.PinCategory != UEdGraphSchema_K2::PC_Exec)
        {
            continue;
        }

        for (const UEdGraphPin* LinkedPin : Pin->LinkedTo)
        {
            const UEdGraphNode* LinkedNode = LinkedPin != nullptr ? LinkedPin->GetOwningNode() : nullptr;
            Links.Add(FString::Printf(
                TEXT("%s->%s.%s"),
                *Pin->PinName.ToString(),
                LinkedNode != nullptr ? *LinkedNode->GetName() : TEXT("<null>"),
                LinkedPin != nullptr ? *LinkedPin->PinName.ToString() : TEXT("<null>")));
        }
    }

    return FString::Join(Links, TEXT(","));
}

static FString DescribeNodeForReport(const UEdGraphNode* Node)
{
    return Node != nullptr ? Node->GetNodeTitle(ENodeTitleType::ListView).ToString() : TEXT("<null>");
}

static bool IsGeneratedKmsReportNode(const UEdGraphNode* Node)
{
    return Node != nullptr && Node->NodeComment.StartsWith(TEXT("[KMS-BP NODE]"));
}

static bool ValidateNoGeneratedKmsNodes(const TArray<FString>& ImportedAssets)
{
    bool bOk = true;
    for (const FString& ImportedAsset : ImportedAssets)
    {
        UBlueprint* Blueprint = Cast<UBlueprint>(FSoftObjectPath(ImportedAsset).TryLoad());
        if (Blueprint == nullptr)
        {
            UE_LOG(LogTemp, Error, TEXT("ValidateNoGenerated: failed to load blueprint %s"), *ImportedAsset);
            bOk = false;
            continue;
        }

        TArray<UEdGraph*> Graphs;
        Graphs.Append(Blueprint->FunctionGraphs);
        Graphs.Append(Blueprint->UbergraphPages);
        Graphs.Append(Blueprint->DelegateSignatureGraphs);

        for (UEdGraph* Graph : Graphs)
        {
            if (Graph == nullptr)
            {
                continue;
            }

            for (UEdGraphNode* Node : Graph->Nodes)
            {
                if (Cast<UK2Node_KmsGeneratedNode>(Node) != nullptr)
                {
                    UE_LOG(
                        LogTemp,
                        Error,
                        TEXT("ValidateNoGenerated: generated fallback node remains in %s.%s: %s"),
                        *ImportedAsset,
                        *Graph->GetName(),
                        *DescribeNodeForReport(Node));
                    bOk = false;
                }
            }
        }
    }
    return bOk;
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

        TMap<FName, int32> GraphNameCounts;
        for (UEdGraph* Graph : Graphs)
        {
            if (Graph != nullptr)
            {
                ++GraphNameCounts.FindOrAdd(Graph->GetFName());
            }
        }

        for (const TPair<FName, int32>& GraphNameCount : GraphNameCounts)
        {
            if (GraphNameCount.Value > 1)
            {
                UE_LOG(
                    LogTemp,
                    Error,
                    TEXT("ValidateExec: duplicate graph name in %s: %s count=%d"),
                    *ImportedAsset,
                    *GraphNameCount.Key.ToString(),
                    GraphNameCount.Value);
                bOk = false;
            }
        }

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
                    TEXT("ValidateExec: %s.%s %s at (%d,%d) exec-in=%d [%s] exec-out=%d [%s]"),
                    *ImportedAsset,
                    *Graph->GetName(),
                    *SetNode->GetNodeTitle(ENodeTitleType::ListView).ToString(),
                    SetNode->NodePosX,
                    SetNode->NodePosY,
                    InputLinks,
                    *DescribeExecLinks(SetNode, EGPD_Input),
                    OutputLinks,
                    *DescribeExecLinks(SetNode, EGPD_Output));

                if (InputLinks == 0)
                {
                    UE_LOG(
                        LogTemp,
                        Error,
                        TEXT("ValidateExec: disconnected set node input in %s.%s: %s"),
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

static FString BuildGeneratedExecLinkReport(const TArray<FString>& ImportedAssets)
{
    TArray<FString> Lines;
    for (const FString& ImportedAsset : ImportedAssets)
    {
        UBlueprint* Blueprint = Cast<UBlueprint>(FSoftObjectPath(ImportedAsset).TryLoad());
        if (Blueprint == nullptr)
        {
            Lines.Add(FString::Printf(TEXT("ERROR failed to load blueprint %s"), *ImportedAsset));
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
                if (Node == nullptr || (Cast<UK2Node_VariableSet>(Node) == nullptr && !IsGeneratedKmsReportNode(Node)))
                {
                    continue;
                }

                Lines.Add(FString::Printf(
                    TEXT("%s.%s %s %s at (%d,%d) exec-in=%d [%s] exec-out=%d [%s]"),
                    *ImportedAsset,
                    *Graph->GetName(),
                    *Node->GetClass()->GetName(),
                    *DescribeNodeForReport(Node),
                    Node->NodePosX,
                    Node->NodePosY,
                    CountLinkedExecPins(Node, EGPD_Input),
                    *DescribeExecLinks(Node, EGPD_Input),
                    CountLinkedExecPins(Node, EGPD_Output),
                    *DescribeExecLinks(Node, EGPD_Output)));
            }
        }
    }

    return FString::Join(Lines, TEXT("\n"));
}

static bool WriteGeneratedExecLinkReport(const TArray<FString>& ImportedAssets, const FString& ReportPath)
{
    if (ReportPath.IsEmpty())
    {
        return true;
    }

    const FString Report = BuildGeneratedExecLinkReport(ImportedAssets);
    if (!FFileHelper::SaveStringToFile(Report, *ReportPath))
    {
        UE_LOG(LogTemp, Error, TEXT("DumpExec: failed to write report '%s'."), *ReportPath);
        return false;
    }

    UE_LOG(LogTemp, Display, TEXT("DumpExec: wrote %s"), *ReportPath);
    return true;
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
    FString DumpExecPath;
    FParse::Value(*Params, TEXT("DumpExec="), DumpExecPath);
    if (DumpExecPath.IsEmpty())
    {
        FParse::Value(*Params, TEXT("dumpexec="), DumpExecPath);
    }

    FString AssetPath;
    if (FParse::Value(*Params, TEXT("ValidateAsset="), AssetPath) || FParse::Value(*Params, TEXT("validateasset="), AssetPath))
    {
        TArray<FString> Assets;
        Assets.Add(AssetPath);
        const bool bValid = ValidateGeneratedExecLinks(Assets);
        const bool bDumped = WriteGeneratedExecLinkReport(Assets, DumpExecPath);
        return bValid && bDumped ? 0 : 3;
    }

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
    Options.bFormatGraphs = !FParse::Param(*Params, TEXT("NoFormatGraph"));
    Options.bRecreateExistingBlueprint = FParse::Param(*Params, TEXT("RecreateAsset"));

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

    if (bSuccess && FParse::Param(*Params, TEXT("ValidateNoGeneratedKmsNodes")) && !ValidateNoGeneratedKmsNodes(Result.ImportedAssets))
    {
        return 3;
    }

    if (bSuccess && !WriteGeneratedExecLinkReport(Result.ImportedAssets, DumpExecPath))
    {
        return 3;
    }

    return bSuccess ? 0 : 2;
}
