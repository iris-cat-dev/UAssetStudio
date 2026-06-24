#pragma once

#include "CoreMinimal.h"
#include "KmsBpImportTypes.generated.h"

USTRUCT(BlueprintType)
struct KMSBLUEPRINTIMPORTER_API FKmsBpImportOptions
{
    GENERATED_BODY()

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "KMS Blueprint")
    bool bCompile = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "KMS Blueprint")
    bool bSave = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "KMS Blueprint")
    bool bClearGeneratedComponents = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "KMS Blueprint")
    bool bCreateProcedureGraphs = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "KMS Blueprint")
    bool bFormatGraphs = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "KMS Blueprint")
    bool bRecreateExistingBlueprint = false;
};

USTRUCT(BlueprintType)
struct KMSBLUEPRINTIMPORTER_API FKmsBpImportResult
{
    GENERATED_BODY()

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "KMS Blueprint")
    bool bSuccess = false;

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "KMS Blueprint")
    TArray<FString> ImportedAssets;

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "KMS Blueprint")
    TArray<FString> Messages;
};
