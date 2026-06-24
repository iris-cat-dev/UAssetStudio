#include "K2Node_KmsGeneratedNode.h"

#include "EdGraphSchema_K2.h"
#include "KismetCompiler.h"
#include "KismetCompilerMisc.h"

class FKmsGeneratedNodeHandler : public FNodeHandlingFunctor
{
public:
    explicit FKmsGeneratedNodeHandler(FKismetCompilerContext& InCompilerContext)
        : FNodeHandlingFunctor(InCompilerContext)
    {
    }

    virtual void Compile(FKismetFunctionContext& Context, UEdGraphNode* Node) override
    {
        if (const UK2Node_KmsGeneratedNode* KmsNode = Cast<UK2Node_KmsGeneratedNode>(Node); KmsNode != nullptr && KmsNode->bStatement)
        {
            GenerateSimpleThenGoto(Context, *Node);
        }
    }
};

void UK2Node_KmsGeneratedNode::AllocateDefaultPins()
{
    if (bStatement)
    {
        CreatePin(EGPD_Input, UEdGraphSchema_K2::PC_Exec, UEdGraphSchema_K2::PN_Execute);
        CreatePin(EGPD_Output, UEdGraphSchema_K2::PC_Exec, UEdGraphSchema_K2::PN_Then);
    }

    UEdGraphPin* TextPin = CreatePin(EGPD_Output, UEdGraphSchema_K2::PC_String, TEXT("Text"));
    if (TextPin != nullptr)
    {
        TextPin->DefaultValue = Text;
    }
}

FText UK2Node_KmsGeneratedNode::GetTooltipText() const
{
    return FText::FromString(Text);
}

FText UK2Node_KmsGeneratedNode::GetNodeTitle(ENodeTitleType::Type TitleType) const
{
    const FString Prefix = bStatement ? TEXT("KMS Statement") : TEXT("KMS Expression");
    return Kind.IsEmpty()
        ? FText::FromString(Prefix)
        : FText::FromString(FString::Printf(TEXT("%s: %s"), *Prefix, *Kind));
}

FLinearColor UK2Node_KmsGeneratedNode::GetNodeTitleColor() const
{
    return bStatement ? FLinearColor(0.20f, 0.42f, 0.80f) : FLinearColor(0.20f, 0.60f, 0.42f);
}

bool UK2Node_KmsGeneratedNode::IsNodePure() const
{
    return !bStatement;
}

FNodeHandlingFunctor* UK2Node_KmsGeneratedNode::CreateNodeHandler(FKismetCompilerContext& CompilerContext) const
{
    return new FKmsGeneratedNodeHandler(CompilerContext);
}

void UK2Node_KmsGeneratedNode::ExpandNode(FKismetCompilerContext& CompilerContext, UEdGraph* SourceGraph)
{
    Super::ExpandNode(CompilerContext, SourceGraph);
}
