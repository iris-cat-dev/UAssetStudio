#pragma once

#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "KmsBpImportTypes.h"
#include "KmsBpImporterLibrary.generated.h"

UCLASS()
class KMSBLUEPRINTIMPORTER_API UKmsBpImporterLibrary : public UBlueprintFunctionLibrary
{
    GENERATED_BODY()

public:
    UFUNCTION(BlueprintCallable, CallInEditor, Category = "KMS Blueprint")
    static bool ImportKmsBlueprintJson(const FString& JsonPath, const FKmsBpImportOptions& Options, FKmsBpImportResult& Result);

    UFUNCTION(BlueprintPure, Category = "KMS Blueprint|Generated", meta = (DisplayName = "KMS Expression"))
    static FString KmsGeneratedExpression(const FString& Kind, const FString& Text);

    UFUNCTION(BlueprintCallable, Category = "KMS Blueprint|Generated", meta = (DisplayName = "KMS Statement"))
    static void KmsGeneratedStatement(const FString& Kind, const FString& Text);

    UFUNCTION(BlueprintPure, Category = "KMS Blueprint|Generated", meta = (DisplayName = "KMS Statement"))
    static FString KmsGeneratedStatementPure(const FString& Kind, const FString& Text);
};
