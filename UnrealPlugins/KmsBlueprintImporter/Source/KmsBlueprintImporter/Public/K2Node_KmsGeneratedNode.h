#pragma once

#include "CoreMinimal.h"
#include "K2Node.h"
#include "K2Node_KmsGeneratedNode.generated.h"

UCLASS()
class KMSBLUEPRINTIMPORTER_API UK2Node_KmsGeneratedNode : public UK2Node
{
    GENERATED_BODY()

public:
    UPROPERTY(EditAnywhere, Category = "KMS")
    FString Kind;

    UPROPERTY(EditAnywhere, Category = "KMS", meta = (MultiLine = true))
    FString Text;

    UPROPERTY(EditAnywhere, Category = "KMS")
    bool bStatement = false;

    virtual void AllocateDefaultPins() override;
    virtual FText GetTooltipText() const override;
    virtual FText GetNodeTitle(ENodeTitleType::Type TitleType) const override;
    virtual FLinearColor GetNodeTitleColor() const override;
    virtual bool IsNodePure() const override;
    virtual bool ShouldShowNodeProperties() const override { return true; }
    virtual FNodeHandlingFunctor* CreateNodeHandler(FKismetCompilerContext& CompilerContext) const override;
    virtual void ExpandNode(FKismetCompilerContext& CompilerContext, UEdGraph* SourceGraph) override;
};
