#include "KmsBpImportCommandlet.h"

#include "KmsBpImporterLibrary.h"

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

    return bSuccess ? 0 : 2;
}
