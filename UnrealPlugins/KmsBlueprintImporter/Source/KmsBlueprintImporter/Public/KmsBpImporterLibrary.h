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
};
