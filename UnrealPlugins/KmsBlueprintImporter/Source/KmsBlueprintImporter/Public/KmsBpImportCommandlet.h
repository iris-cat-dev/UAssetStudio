#pragma once

#include "CoreMinimal.h"
#include "Commandlets/Commandlet.h"
#include "KmsBpImportCommandlet.generated.h"

UCLASS()
class KMSBLUEPRINTIMPORTER_API UKmsBpImportCommandlet : public UCommandlet
{
    GENERATED_BODY()

public:
    UKmsBpImportCommandlet();

    virtual int32 Main(const FString& Params) override;
};
