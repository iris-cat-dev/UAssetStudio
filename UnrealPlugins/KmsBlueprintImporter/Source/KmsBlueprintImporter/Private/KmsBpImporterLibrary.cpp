#include "KmsBpImporterLibrary.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Components/SceneComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Dom/JsonObject.h"
#include "EdGraph/EdGraph.h"
#include "EdGraphNode_Comment.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/SCS_Node.h"
#include "Engine/SimpleConstructionScript.h"
#include "Engine/StaticMesh.h"
#include "FileHelpers.h"
#include "GameFramework/Actor.h"
#include "K2Node_CallArrayFunction.h"
#include "K2Node_AddDelegate.h"
#include "K2Node_CallDelegate.h"
#include "K2Node_CreateDelegate.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "K2Node_CallFunction.h"
#include "K2Node_Event.h"
#include "K2Node_IfThenElse.h"
#include "K2Node_KmsGeneratedNode.h"
#include "K2Node_MacroInstance.h"
#include "K2Node_MakeArray.h"
#include "K2Node_RemoveDelegate.h"
#include "K2Node_VariableGet.h"
#include "K2Node_VariableSet.h"
#include "Kismet/KismetArrayLibrary.h"
#include "Kismet/KismetMathLibrary.h"
#include "Kismet/KismetSystemLibrary.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "ObjectTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/Package.h"
#include "UObject/UnrealType.h"

namespace
{
constexpr const TCHAR* KmsBpSchemaVersion = TEXT("kms-bp-export-v1");
constexpr const TCHAR* KmsGeneratedCommentPrefix = TEXT("[KMS-BP]");
constexpr const TCHAR* KmsGeneratedNodePrefix = TEXT("[KMS-BP NODE]");

struct FKmsBpJsonProperty
{
    FString Name;
    FString Type;
    TSharedPtr<FJsonObject> Value;
};

struct FKmsBpJsonComponent
{
    FString Name;
    FString Type;
    bool bIsRoot = false;
    FString AttachTarget;
    TMap<FString, TArray<TSharedPtr<FJsonObject>>> Metadata;
    TArray<FKmsBpJsonProperty> Properties;
};

struct FKmsBpJsonVariable
{
    FString Name;
    FString Type;
    bool bIsEditable = false;
    FString Category;
    TMap<FString, TArray<TSharedPtr<FJsonObject>>> Metadata;
    TSharedPtr<FJsonObject> Initializer;
};

struct FKmsBpJsonParameter
{
    FString Name;
    FString Type;
    FString Modifier;
};

struct FKmsBpJsonProcedure
{
    FString Name;
    FString Kind;
    FString ReturnType;
    FString EventName;
    FString Category;
    TMap<FString, TArray<TSharedPtr<FJsonObject>>> Metadata;
    TArray<FKmsBpJsonParameter> Parameters;
    TSharedPtr<FJsonObject> Body;
};

struct FKmsBpJsonBlueprint
{
    FString Name;
    FString ParentType;
    FString AssetPath;
    TMap<FString, TArray<TSharedPtr<FJsonObject>>> Metadata;
    TArray<FKmsBpJsonComponent> Components;
    TArray<FKmsBpJsonVariable> Variables;
    TArray<FKmsBpJsonProcedure> Procedures;
};

struct FKmsBpJsonDocument
{
    FString SchemaVersion;
    FString LanguageVersion;
    FString SourcePath;
    FString SourceSha256;
    TArray<FKmsBpJsonBlueprint> Blueprints;
};

static void AddMessage(FKmsBpImportResult& Result, const FString& Message)
{
    Result.Messages.Add(Message);
    UE_LOG(LogTemp, Display, TEXT("KMS-BP: %s"), *Message);
}

static void AddWarning(FKmsBpImportResult& Result, const FString& Message)
{
    Result.Messages.Add(FString::Printf(TEXT("Warning: %s"), *Message));
    UE_LOG(LogTemp, Warning, TEXT("KMS-BP: %s"), *Message);
}

static void AddError(FKmsBpImportResult& Result, const FString& Message)
{
    Result.Messages.Add(FString::Printf(TEXT("Error: %s"), *Message));
    UE_LOG(LogTemp, Error, TEXT("KMS-BP: %s"), *Message);
}

static FString GetStringField(const TSharedPtr<FJsonObject>& Object, const TCHAR* FieldName)
{
    FString Value;
    if (Object.IsValid())
    {
        Object->TryGetStringField(FieldName, Value);
    }
    return Value;
}

static bool GetBoolField(const TSharedPtr<FJsonObject>& Object, const TCHAR* FieldName, bool DefaultValue = false)
{
    bool Value = DefaultValue;
    if (Object.IsValid())
    {
        Object->TryGetBoolField(FieldName, Value);
    }
    return Value;
}

static TSharedPtr<FJsonObject> GetObjectField(const TSharedPtr<FJsonObject>& Object, const TCHAR* FieldName)
{
    const TSharedPtr<FJsonObject>* Value = nullptr;
    if (Object.IsValid() && Object->TryGetObjectField(FieldName, Value))
    {
        return *Value;
    }
    return nullptr;
}

static bool GetArrayField(const TSharedPtr<FJsonObject>& Object, const TCHAR* FieldName, const TArray<TSharedPtr<FJsonValue>>*& OutArray)
{
    OutArray = nullptr;
    return Object.IsValid() && Object->TryGetArrayField(FieldName, OutArray);
}

static bool TryGetLiteralString(const TSharedPtr<FJsonObject>& Expression, FString& OutValue);
static bool TryGetLiteralNumber(const TSharedPtr<FJsonObject>& Expression, double& OutValue);

static TMap<FString, TArray<TSharedPtr<FJsonObject>>> ParseMetadata(const TSharedPtr<FJsonObject>& Object)
{
    TMap<FString, TArray<TSharedPtr<FJsonObject>>> Metadata;
    const TSharedPtr<FJsonObject> MetadataObject = GetObjectField(Object, TEXT("metadata"));
    if (!MetadataObject.IsValid())
    {
        return Metadata;
    }

    for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : MetadataObject->Values)
    {
        const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
        if (!MetadataObject->TryGetArrayField(Pair.Key, Values))
        {
            continue;
        }

        TArray<TSharedPtr<FJsonObject>>& Arguments = Metadata.FindOrAdd(Pair.Key.ToLower());
        for (const TSharedPtr<FJsonValue>& Value : *Values)
        {
            TSharedPtr<FJsonObject> ArgumentObject = Value.IsValid() ? Value->AsObject() : nullptr;
            if (ArgumentObject.IsValid())
            {
                Arguments.Add(ArgumentObject);
            }
        }
    }

    return Metadata;
}

static bool HasMetadata(const TMap<FString, TArray<TSharedPtr<FJsonObject>>>& Metadata, const TCHAR* Name)
{
    return Metadata.Contains(FString(Name).ToLower());
}

static TSharedPtr<FJsonObject> GetMetadataArgumentValue(
    const TMap<FString, TArray<TSharedPtr<FJsonObject>>>& Metadata,
    const TCHAR* Name,
    const int32 Index = 0)
{
    const TArray<TSharedPtr<FJsonObject>>* Arguments = Metadata.Find(FString(Name).ToLower());
    if (Arguments == nullptr || !Arguments->IsValidIndex(Index))
    {
        return nullptr;
    }

    return GetObjectField((*Arguments)[Index], TEXT("value"));
}

static FString GetMetadataString(
    const TMap<FString, TArray<TSharedPtr<FJsonObject>>>& Metadata,
    const TCHAR* Name,
    const int32 Index = 0)
{
    FString Value;
    TryGetLiteralString(GetMetadataArgumentValue(Metadata, Name, Index), Value);
    return Value;
}

static bool GetMetadataNumber(
    const TMap<FString, TArray<TSharedPtr<FJsonObject>>>& Metadata,
    const TCHAR* Name,
    const int32 Index,
    double& OutValue)
{
    return TryGetLiteralNumber(GetMetadataArgumentValue(Metadata, Name, Index), OutValue);
}

static TSharedPtr<FJsonObject> GetArgumentExpression(const TSharedPtr<FJsonValue>& ArgumentValue)
{
    if (!ArgumentValue.IsValid())
    {
        return nullptr;
    }

    const TSharedPtr<FJsonObject> ArgumentObject = ArgumentValue->AsObject();
    if (!ArgumentObject.IsValid())
    {
        return nullptr;
    }

    TSharedPtr<FJsonObject> ExpressionObject = GetObjectField(ArgumentObject, TEXT("expression"));
    return ExpressionObject.IsValid() ? ExpressionObject : ArgumentObject;
}

static FString GetArgumentName(const TSharedPtr<FJsonValue>& ArgumentValue)
{
    const TSharedPtr<FJsonObject> ArgumentObject = ArgumentValue.IsValid() ? ArgumentValue->AsObject() : nullptr;
    return GetStringField(ArgumentObject, TEXT("name"));
}

static bool IsOutArgument(const TSharedPtr<FJsonValue>& ArgumentValue)
{
    const TSharedPtr<FJsonObject> ArgumentObject = ArgumentValue.IsValid() ? ArgumentValue->AsObject() : nullptr;
    return GetStringField(ArgumentObject, TEXT("modifier")).Equals(TEXT("out"), ESearchCase::IgnoreCase);
}

static bool TrySplitConstructedType(const FString& TypeText, FString& OutBaseType, TArray<FString>& OutArguments)
{
    FString Trimmed = TypeText;
    Trimmed.TrimStartAndEndInline();

    const int32 OpenIndex = Trimmed.Find(TEXT("<"));
    if (OpenIndex == INDEX_NONE || !Trimmed.EndsWith(TEXT(">")))
    {
        return false;
    }

    OutBaseType = Trimmed.Left(OpenIndex);
    OutBaseType.TrimStartAndEndInline();

    FString Inner = Trimmed.Mid(OpenIndex + 1, Trimmed.Len() - OpenIndex - 2);
    int32 Depth = 0;
    FString Current;
    for (int32 Index = 0; Index < Inner.Len(); ++Index)
    {
        const TCHAR Ch = Inner[Index];
        if (Ch == TCHAR('<'))
        {
            ++Depth;
        }
        else if (Ch == TCHAR('>'))
        {
            --Depth;
        }

        if (Ch == TCHAR(',') && Depth == 0)
        {
            Current.TrimStartAndEndInline();
            OutArguments.Add(Current);
            Current.Reset();
            continue;
        }

        Current.AppendChar(Ch);
    }

    Current.TrimStartAndEndInline();
    if (!Current.IsEmpty())
    {
        OutArguments.Add(Current);
    }

    return !OutBaseType.IsEmpty() && OutArguments.Num() > 0;
}

static bool IsLiteralExpression(const TSharedPtr<FJsonObject>& Expression, const FString& ExpectedType)
{
    return GetStringField(Expression, TEXT("kind")).Equals(TEXT("literal"), ESearchCase::IgnoreCase)
        && GetStringField(Expression, TEXT("type")).Equals(ExpectedType, ESearchCase::IgnoreCase);
}

static bool TryGetLiteralString(const TSharedPtr<FJsonObject>& Expression, FString& OutValue)
{
    if (!Expression.IsValid() || !IsLiteralExpression(Expression, TEXT("string")))
    {
        return false;
    }

    return Expression->TryGetStringField(TEXT("value"), OutValue);
}

static bool TryGetLiteralBool(const TSharedPtr<FJsonObject>& Expression, bool& OutValue)
{
    if (!Expression.IsValid() || !IsLiteralExpression(Expression, TEXT("bool")))
    {
        return false;
    }

    return Expression->TryGetBoolField(TEXT("value"), OutValue);
}

static bool TryGetLiteralNumber(const TSharedPtr<FJsonObject>& Expression, double& OutValue)
{
    if (!Expression.IsValid() || !GetStringField(Expression, TEXT("kind")).Equals(TEXT("literal"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    const FString Type = GetStringField(Expression, TEXT("type"));
    if (!Type.Equals(TEXT("int"), ESearchCase::IgnoreCase)
        && !Type.Equals(TEXT("int64"), ESearchCase::IgnoreCase)
        && !Type.Equals(TEXT("uint32"), ESearchCase::IgnoreCase)
        && !Type.Equals(TEXT("uint64"), ESearchCase::IgnoreCase)
        && !Type.Equals(TEXT("float"), ESearchCase::IgnoreCase)
        && !Type.Equals(TEXT("double"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    return Expression->TryGetNumberField(TEXT("value"), OutValue);
}

static bool TryGetAssetReference(const TSharedPtr<FJsonObject>& Expression, FString& OutPath, FString& OutType)
{
    if (!Expression.IsValid()
        || !GetStringField(Expression, TEXT("kind")).Equals(TEXT("call"), ESearchCase::IgnoreCase)
        || !GetStringField(Expression, TEXT("name")).Equals(TEXT("asset"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* TypeArguments = nullptr;
    if (GetArrayField(Expression, TEXT("typeArguments"), TypeArguments) && TypeArguments->Num() > 0 && (*TypeArguments)[0].IsValid())
    {
        (*TypeArguments)[0]->TryGetString(OutType);
    }

    const TArray<TSharedPtr<FJsonValue>>* Arguments = nullptr;
    if (!GetArrayField(Expression, TEXT("arguments"), Arguments) || Arguments->Num() == 0 || !(*Arguments)[0].IsValid())
    {
        return false;
    }

    return TryGetLiteralString(GetArgumentExpression((*Arguments)[0]), OutPath);
}

static FString ExpressionToText(const TSharedPtr<FJsonObject>& Expression);
static FString StatementToText(const TSharedPtr<FJsonObject>& Statement, int32 Indent = 0);
static UK2Node_FunctionEntry* FindFunctionEntryNode(UEdGraph* Graph);
static UK2Node_FunctionResult* FindFunctionResultNode(UEdGraph* Graph);
static bool ResolvePinType(const FString& TypeText, FEdGraphPinType& OutPinType, FKmsBpImportResult& Result, bool bIsReference);
static UClass* LoadClassByPathOrName(const FString& TypeName, UClass* RequiredBaseClass);
static FString DefaultValueToString(const TSharedPtr<FJsonObject>& Initializer);
static void MaterializeExpressionNodes(UEdGraph* Graph, const TSharedPtr<FJsonObject>& Expression, int32 Depth, int32& NextY);

static FString JsonValueToText(const TSharedPtr<FJsonValue>& Value)
{
    if (!Value.IsValid())
    {
        return TEXT("");
    }

    switch (Value->Type)
    {
    case EJson::String:
        {
            FString StringValue;
            Value->TryGetString(StringValue);
            return FString::Printf(TEXT("\"%s\""), *StringValue);
        }
    case EJson::Number:
        return FString::SanitizeFloat(Value->AsNumber());
    case EJson::Boolean:
        return Value->AsBool() ? TEXT("true") : TEXT("false");
    default:
        return TEXT("");
    }
}

static FString ExpressionArrayToText(const TArray<TSharedPtr<FJsonValue>>& Values, const FString& Separator)
{
    TArray<FString> Items;
    for (const TSharedPtr<FJsonValue>& Value : Values)
    {
        Items.Add(ExpressionToText(Value.IsValid() ? Value->AsObject() : nullptr));
    }
    return FString::Join(Items, *Separator);
}

static FString ExpressionItemsToText(const TSharedPtr<FJsonObject>& Expression, const TCHAR* FieldName, const FString& Separator)
{
    const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
    return GetArrayField(Expression, FieldName, Items) ? ExpressionArrayToText(*Items, Separator) : TEXT("");
}

static FString ExpressionToText(const TSharedPtr<FJsonObject>& Expression)
{
    if (!Expression.IsValid())
    {
        return TEXT("<none>");
    }

    const FString Kind = GetStringField(Expression, TEXT("kind"));
    if (Kind.Equals(TEXT("literal"), ESearchCase::IgnoreCase))
    {
        const TSharedPtr<FJsonValue>* Value = Expression->Values.Find(TEXT("value"));
        return Value != nullptr ? JsonValueToText(*Value) : TEXT("<literal>");
    }

    if (Kind.Equals(TEXT("identifier"), ESearchCase::IgnoreCase))
    {
        return GetStringField(Expression, TEXT("name"));
    }

    if (Kind.Equals(TEXT("call"), ESearchCase::IgnoreCase))
    {
        FString TypeSuffix;
        const TArray<TSharedPtr<FJsonValue>>* TypeArguments = nullptr;
        if (GetArrayField(Expression, TEXT("typeArguments"), TypeArguments) && TypeArguments->Num() > 0)
        {
            TArray<FString> Types;
            for (const TSharedPtr<FJsonValue>& TypeArgument : *TypeArguments)
            {
                FString TypeText;
                if (TypeArgument.IsValid() && TypeArgument->TryGetString(TypeText))
                {
                    Types.Add(TypeText);
                }
            }
            TypeSuffix = FString::Printf(TEXT("<%s>"), *FString::Join(Types, TEXT(", ")));
        }

        return FString::Printf(
            TEXT("%s%s(%s)"),
            *GetStringField(Expression, TEXT("name")),
            *TypeSuffix,
            *ExpressionItemsToText(Expression, TEXT("arguments"), TEXT(", ")));
    }

    if (Kind.Equals(TEXT("binary"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("%s %s %s"),
            *ExpressionToText(GetObjectField(Expression, TEXT("left"))),
            *GetStringField(Expression, TEXT("op")),
            *ExpressionToText(GetObjectField(Expression, TEXT("right"))));
    }

    if (Kind.Equals(TEXT("unary"), ESearchCase::IgnoreCase))
    {
        const FString Op = GetStringField(Expression, TEXT("op"));
        const FString Operand = ExpressionToText(GetObjectField(Expression, TEXT("operand")));
        if (Op.Equals(TEXT("post++"), ESearchCase::IgnoreCase))
        {
            return Operand + TEXT("++");
        }
        if (Op.Equals(TEXT("post--"), ESearchCase::IgnoreCase))
        {
            return Operand + TEXT("--");
        }
        if (Op.Equals(TEXT("++pre"), ESearchCase::IgnoreCase))
        {
            return TEXT("++") + Operand;
        }
        if (Op.Equals(TEXT("--pre"), ESearchCase::IgnoreCase))
        {
            return TEXT("--") + Operand;
        }
        return FString::Printf(TEXT("%s%s"), *Op, *Operand);
    }

    if (Kind.Equals(TEXT("member"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("%s%s%s"),
            *ExpressionToText(GetObjectField(Expression, TEXT("context"))),
            *GetStringField(Expression, TEXT("op")),
            *ExpressionToText(GetObjectField(Expression, TEXT("member"))));
    }

    if (Kind.Equals(TEXT("subscript"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("%s[%s]"),
            *ExpressionToText(GetObjectField(Expression, TEXT("context"))),
            *ExpressionToText(GetObjectField(Expression, TEXT("index"))));
    }

    if (Kind.Equals(TEXT("cast"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("(%s)%s"),
            *GetStringField(Expression, TEXT("type")),
            *ExpressionToText(GetObjectField(Expression, TEXT("operand"))));
    }

    if (Kind.Equals(TEXT("typeof"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(TEXT("typeof(%s)"), *GetStringField(Expression, TEXT("type")));
    }

    if (Kind.Equals(TEXT("conditional"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("%s ? %s : %s"),
            *ExpressionToText(GetObjectField(Expression, TEXT("condition"))),
            *ExpressionToText(GetObjectField(Expression, TEXT("then"))),
            *ExpressionToText(GetObjectField(Expression, TEXT("else"))));
    }

    if (Kind.Equals(TEXT("array"), ESearchCase::IgnoreCase))
    {
        return TEXT("[") + ExpressionItemsToText(Expression, TEXT("items"), TEXT(", ")) + TEXT("]");
    }

    if (Kind.Equals(TEXT("initializer"), ESearchCase::IgnoreCase))
    {
        return TEXT("{ ") + ExpressionItemsToText(Expression, TEXT("items"), TEXT(", ")) + TEXT(" }");
    }

    if (Kind.Equals(TEXT("object"), ESearchCase::IgnoreCase))
    {
        TArray<FString> EntryTexts;
        const TArray<TSharedPtr<FJsonValue>>* Entries = nullptr;
        if (GetArrayField(Expression, TEXT("entries"), Entries))
        {
            for (const TSharedPtr<FJsonValue>& EntryValue : *Entries)
            {
                const TSharedPtr<FJsonObject> Entry = EntryValue.IsValid() ? EntryValue->AsObject() : nullptr;
                EntryTexts.Add(FString::Printf(
                    TEXT("%s: %s"),
                    *ExpressionToText(GetObjectField(Entry, TEXT("key"))),
                    *ExpressionToText(GetObjectField(Entry, TEXT("value")))));
            }
        }
        return TEXT("{ ") + FString::Join(EntryTexts, TEXT(", ")) + TEXT(" }");
    }

    if (Kind.Equals(TEXT("new"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("new %s { %s }"),
            *GetStringField(Expression, TEXT("type")),
            *ExpressionItemsToText(Expression, TEXT("items"), TEXT(", ")));
    }

    return Kind.IsEmpty() ? TEXT("<expression>") : Kind;
}

static FString StatementBodyToText(const TSharedPtr<FJsonObject>& Statement, int32 Indent)
{
    const FString Padding = FString::ChrN(Indent * 4, TCHAR(' '));
    if (!Statement.IsValid())
    {
        return TEXT("{\n") + Padding + TEXT("}");
    }

    const FString Kind = GetStringField(Statement, TEXT("kind"));
    if (Kind.Equals(TEXT("block"), ESearchCase::IgnoreCase))
    {
        TArray<FString> Lines;
        const TArray<TSharedPtr<FJsonValue>>* Statements = nullptr;
        if (GetArrayField(Statement, TEXT("statements"), Statements))
        {
            for (const TSharedPtr<FJsonValue>& Child : *Statements)
            {
                Lines.Add(StatementToText(Child.IsValid() ? Child->AsObject() : nullptr, Indent + 1));
            }
        }

        const FString Body = FString::Join(Lines, TEXT("\n"));
        return Body.IsEmpty()
            ? TEXT("{\n") + Padding + TEXT("}")
            : TEXT("{\n") + Body + TEXT("\n") + Padding + TEXT("}");
    }

    return TEXT("{\n") + StatementToText(Statement, Indent + 1) + TEXT("\n") + Padding + TEXT("}");
}

static FString StatementToInlineText(const TSharedPtr<FJsonObject>& Statement)
{
    FString Text = StatementToText(Statement, 0);
    Text.TrimStartAndEndInline();
    if (Text.EndsWith(TEXT(";")))
    {
        Text.LeftChopInline(1);
    }
    Text.ReplaceInline(TEXT("\n"), TEXT(" "));
    return Text;
}

static FString StatementToText(const TSharedPtr<FJsonObject>& Statement, int32 Indent)
{
    if (!Statement.IsValid())
    {
        return TEXT("");
    }

    const FString Padding = FString::ChrN(Indent * 4, TCHAR(' '));
    const FString Kind = GetStringField(Statement, TEXT("kind"));

    if (Kind.Equals(TEXT("block"), ESearchCase::IgnoreCase))
    {
        TArray<FString> Lines;
        const TArray<TSharedPtr<FJsonValue>>* Statements = nullptr;
        if (GetArrayField(Statement, TEXT("statements"), Statements))
        {
            for (const TSharedPtr<FJsonValue>& Child : *Statements)
            {
                Lines.Add(StatementToText(Child.IsValid() ? Child->AsObject() : nullptr, Indent));
            }
        }
        return FString::Join(Lines, TEXT("\n"));
    }

    if (Kind.Equals(TEXT("expression"), ESearchCase::IgnoreCase))
    {
        return Padding + ExpressionToText(GetObjectField(Statement, TEXT("expression"))) + TEXT(";");
    }

    if (Kind.Equals(TEXT("var"), ESearchCase::IgnoreCase))
    {
        const TSharedPtr<FJsonObject> Initializer = GetObjectField(Statement, TEXT("initializer"));
        if (!Initializer.IsValid())
        {
            return FString::Printf(
                TEXT("%svar %s: %s;"),
                *Padding,
                *GetStringField(Statement, TEXT("name")),
                *GetStringField(Statement, TEXT("type")));
        }

        return FString::Printf(
            TEXT("%svar %s: %s = %s;"),
            *Padding,
            *GetStringField(Statement, TEXT("name")),
            *GetStringField(Statement, TEXT("type")),
            *ExpressionToText(Initializer));
    }

    if (Kind.Equals(TEXT("return"), ESearchCase::IgnoreCase))
    {
        const TSharedPtr<FJsonObject> Value = GetObjectField(Statement, TEXT("value"));
        return Value.IsValid()
            ? Padding + TEXT("return ") + ExpressionToText(Value) + TEXT(";")
            : Padding + TEXT("return;");
    }

    if (Kind.Equals(TEXT("if"), ESearchCase::IgnoreCase))
    {
        FString Text = FString::Printf(
            TEXT("%sif (%s) %s"),
            *Padding,
            *ExpressionToText(GetObjectField(Statement, TEXT("condition"))),
            *StatementBodyToText(GetObjectField(Statement, TEXT("then")), Indent));

        const TSharedPtr<FJsonObject> Else = GetObjectField(Statement, TEXT("else"));
        if (Else.IsValid())
        {
            Text += TEXT(" else ") + StatementBodyToText(Else, Indent);
        }
        return Text;
    }

    if (Kind.Equals(TEXT("while"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("%swhile (%s) %s"),
            *Padding,
            *ExpressionToText(GetObjectField(Statement, TEXT("condition"))),
            *StatementBodyToText(GetObjectField(Statement, TEXT("body")), Indent));
    }

    if (Kind.Equals(TEXT("for"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("%sfor (%s; %s; %s) %s"),
            *Padding,
            *StatementToInlineText(GetObjectField(Statement, TEXT("initializerStatement"))),
            *ExpressionToText(GetObjectField(Statement, TEXT("condition"))),
            *ExpressionToText(GetObjectField(Statement, TEXT("after"))),
            *StatementBodyToText(GetObjectField(Statement, TEXT("body")), Indent));
    }

    if (Kind.Equals(TEXT("foreach"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("%sforeach (%s: %s in %s) %s"),
            *Padding,
            *GetStringField(Statement, TEXT("name")),
            *GetStringField(Statement, TEXT("type")),
            *ExpressionToText(GetObjectField(Statement, TEXT("collection"))),
            *StatementBodyToText(GetObjectField(Statement, TEXT("body")), Indent));
    }

    if (Kind.Equals(TEXT("switch"), ESearchCase::IgnoreCase))
    {
        TArray<FString> CaseTexts;
        const TArray<TSharedPtr<FJsonValue>>* Cases = nullptr;
        if (GetArrayField(Statement, TEXT("cases"), Cases))
        {
            for (const TSharedPtr<FJsonValue>& CaseValue : *Cases)
            {
                const TSharedPtr<FJsonObject> CaseObject = CaseValue.IsValid() ? CaseValue->AsObject() : nullptr;
                const bool bDefault = GetBoolField(CaseObject, TEXT("isDefault"));
                const FString Header = bDefault
                    ? FString::Printf(TEXT("%s    default:"), *Padding)
                    : FString::Printf(TEXT("%s    case %s:"), *Padding, *ExpressionToText(GetObjectField(CaseObject, TEXT("condition"))));
                CaseTexts.Add(Header + TEXT("\n") + StatementToText(GetObjectField(CaseObject, TEXT("body")), Indent + 2));
            }
        }

        return FString::Printf(
            TEXT("%sswitch (%s) {\n%s\n%s}"),
            *Padding,
            *ExpressionToText(GetObjectField(Statement, TEXT("switchOn"))),
            *FString::Join(CaseTexts, TEXT("\n")),
            *Padding);
    }

    if (Kind.Equals(TEXT("bind"), ESearchCase::IgnoreCase) || Kind.Equals(TEXT("unbind"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("%s%s %s %s %s;"),
            *Padding,
            *Kind,
            *ExpressionToText(GetObjectField(Statement, TEXT("target"))),
            Kind.Equals(TEXT("bind"), ESearchCase::IgnoreCase) ? TEXT("+=") : TEXT("-="),
            *ExpressionToText(GetObjectField(Statement, TEXT("handler"))));
    }

    if (Kind.Equals(TEXT("break"), ESearchCase::IgnoreCase))
    {
        return Padding + TEXT("break;");
    }

    if (Kind.Equals(TEXT("continue"), ESearchCase::IgnoreCase))
    {
        return Padding + TEXT("continue;");
    }

    return Padding + Kind;
}

static UK2Node_KmsGeneratedNode* SpawnGeneratedKmsNode(
    UEdGraph* Graph,
    const bool bStatement,
    const FString& Kind,
    const FString& Text,
    const int32 X,
    const int32 Y)
{
    if (Graph == nullptr)
    {
        return nullptr;
    }

    UK2Node_KmsGeneratedNode* Node = NewObject<UK2Node_KmsGeneratedNode>(Graph);
    Node->Kind = Kind;
    Node->Text = Text;
    Node->bStatement = bStatement;
    Node->CreateNewGuid();
    Node->PostPlacedNewNode();
    Node->SetFlags(RF_Transactional);
    Node->AllocateDefaultPins();
    Node->NodePosX = X;
    Node->NodePosY = Y;
    Node->NodeComment = FString::Printf(TEXT("%s %s"), KmsGeneratedNodePrefix, *Kind);

    UEdGraphSchema_K2::SetNodeMetaData(Node, FNodeMetadata::DefaultGraphNode);
    Graph->AddNode(Node, true, false);
    return Node;
}

static bool IsGeneratedKmsNode(const UEdGraphNode* Node)
{
    return Node != nullptr && Node->NodeComment.StartsWith(KmsGeneratedNodePrefix);
}

static void MarkGeneratedKmsNode(UEdGraphNode* Node, const FString& Kind)
{
    if (Node != nullptr)
    {
        Node->NodeComment = FString::Printf(TEXT("%s %s"), KmsGeneratedNodePrefix, *Kind);
    }
}

static void ClearGeneratedKmsNodes(UEdGraph* Graph)
{
    if (Graph == nullptr)
    {
        return;
    }

    TArray<UEdGraphNode*> NodesToRemove;
    for (UEdGraphNode* Node : Graph->Nodes)
    {
        if (IsGeneratedKmsNode(Node))
        {
            NodesToRemove.Add(Node);
        }
    }

    for (UEdGraphNode* Node : NodesToRemove)
    {
        Graph->RemoveNode(Node);
    }
}

static void TryConnectExecPins(UEdGraph* Graph, UEdGraphPin*& ExecTail, UK2Node_KmsGeneratedNode* Node)
{
    if (Graph == nullptr || ExecTail == nullptr || Node == nullptr)
    {
        return;
    }

    UEdGraphPin* ExecutePin = Node->FindPin(UEdGraphSchema_K2::PN_Execute);
    UEdGraphPin* ThenPin = Node->FindPin(UEdGraphSchema_K2::PN_Then);
    if (ExecutePin == nullptr || ThenPin == nullptr)
    {
        return;
    }

    if (const UEdGraphSchema_K2* K2Schema = Cast<const UEdGraphSchema_K2>(Graph->GetSchema()))
    {
        K2Schema->TryCreateConnection(ExecTail, ExecutePin);
    }
    ExecTail = ThenPin;
}

struct FKmsBpNativeGenContext
{
    UBlueprint* Blueprint = nullptr;
    UEdGraph* Graph = nullptr;
    const UEdGraphSchema_K2* K2Schema = nullptr;
    FKmsBpImportResult* ImportResult = nullptr;
    TMap<FString, FString> VariableTypes;
    TMap<FString, FString> VariableAliases;
    TSet<FString> LocalVariables;
    TSet<FString> DelegateVariables;
    int32 NextExpressionY = 380;
};

static TArray<UEdGraphPin*> MakeExecTails(UEdGraphPin* Tail)
{
    TArray<UEdGraphPin*> Tails;
    if (Tail != nullptr)
    {
        Tails.Add(Tail);
    }
    return Tails;
}

static FString ResolveGeneratedVariableName(const FKmsBpNativeGenContext& Context, const FString& Name)
{
    if (const FString* Alias = Context.VariableAliases.Find(Name))
    {
        return *Alias;
    }
    return Name;
}

static FString NormalizeKmsType(FString TypeText)
{
    TypeText.TrimStartAndEndInline();
    if (TypeText.Equals(TEXT("int32"), ESearchCase::IgnoreCase))
    {
        return TEXT("int");
    }
    if (TypeText.Equals(TEXT("boolean"), ESearchCase::IgnoreCase))
    {
        return TEXT("bool");
    }
    return TypeText;
}

static bool IsFloatLikeType(const FString& TypeText)
{
    return TypeText.Equals(TEXT("float"), ESearchCase::IgnoreCase)
        || TypeText.Equals(TEXT("double"), ESearchCase::IgnoreCase);
}

static bool IsIntLikeType(const FString& TypeText)
{
    return TypeText.Equals(TEXT("int"), ESearchCase::IgnoreCase)
        || TypeText.Equals(TEXT("int64"), ESearchCase::IgnoreCase)
        || TypeText.Equals(TEXT("uint32"), ESearchCase::IgnoreCase)
        || TypeText.Equals(TEXT("uint64"), ESearchCase::IgnoreCase)
        || TypeText.Equals(TEXT("byte"), ESearchCase::IgnoreCase);
}

static bool TryGetArrayElementType(FString TypeText, FString& OutElementType)
{
    TypeText.TrimStartAndEndInline();
    if (TypeText.StartsWith(TEXT("Array<")) && TypeText.EndsWith(TEXT(">")))
    {
        OutElementType = TypeText.Mid(6, TypeText.Len() - 7);
        OutElementType.TrimStartAndEndInline();
        return !OutElementType.IsEmpty();
    }
    if (TypeText.EndsWith(TEXT("[]")))
    {
        OutElementType = TypeText.LeftChop(2);
        OutElementType.TrimStartAndEndInline();
        return !OutElementType.IsEmpty();
    }
    return false;
}

static FString InferExpressionType(const FKmsBpNativeGenContext& Context, const TSharedPtr<FJsonObject>& Expression)
{
    if (!Expression.IsValid())
    {
        return FString();
    }

    const FString Kind = GetStringField(Expression, TEXT("kind"));
    if (Kind.Equals(TEXT("literal"), ESearchCase::IgnoreCase))
    {
        if (GetStringField(Expression, TEXT("type")).Equals(TEXT("none"), ESearchCase::IgnoreCase))
        {
            return TEXT("Object");
        }
        return NormalizeKmsType(GetStringField(Expression, TEXT("type")));
    }
    if (Kind.Equals(TEXT("array"), ESearchCase::IgnoreCase))
    {
        const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
        if (GetArrayField(Expression, TEXT("items"), Items) && Items->Num() > 0)
        {
            const FString ElementType = InferExpressionType(Context, (*Items)[0].IsValid() ? (*Items)[0]->AsObject() : nullptr);
            if (!ElementType.IsEmpty())
            {
                return FString::Printf(TEXT("Array<%s>"), *ElementType);
            }
        }
        return FString();
    }
    if (Kind.Equals(TEXT("identifier"), ESearchCase::IgnoreCase))
    {
        const FString Name = GetStringField(Expression, TEXT("name"));
        if (Name.Equals(TEXT("none"), ESearchCase::IgnoreCase))
        {
            return TEXT("Object");
        }
        if (Name.Equals(TEXT("self"), ESearchCase::IgnoreCase)
            || Name.Equals(TEXT("KismetSystemLibrary"), ESearchCase::IgnoreCase)
            || Name.Equals(TEXT("MathLibrary"), ESearchCase::IgnoreCase)
            || Name.Equals(TEXT("KismetMathLibrary"), ESearchCase::IgnoreCase))
        {
            return FString();
        }
        if (const FString* Type = Context.VariableTypes.Find(ResolveGeneratedVariableName(Context, Name)))
        {
            return NormalizeKmsType(*Type);
        }
        return FString();
    }
    if (Kind.Equals(TEXT("cast"), ESearchCase::IgnoreCase) || Kind.Equals(TEXT("typeof"), ESearchCase::IgnoreCase))
    {
        return NormalizeKmsType(GetStringField(Expression, TEXT("type")));
    }
    if (Kind.Equals(TEXT("subscript"), ESearchCase::IgnoreCase))
    {
        FString ContextType = InferExpressionType(Context, GetObjectField(Expression, TEXT("context")));
        ContextType.TrimStartAndEndInline();
        if (ContextType.StartsWith(TEXT("Array<")) && ContextType.EndsWith(TEXT(">")))
        {
            return NormalizeKmsType(ContextType.Mid(6, ContextType.Len() - 7));
        }
        return FString();
    }
    if (Kind.Equals(TEXT("member"), ESearchCase::IgnoreCase))
    {
        const FString MemberName = GetStringField(GetObjectField(Expression, TEXT("member")), TEXT("name"));
        const FString ContextType = InferExpressionType(Context, GetObjectField(Expression, TEXT("context")));
        if (MemberName.Equals(TEXT("Length"), ESearchCase::IgnoreCase)
            && (ContextType.StartsWith(TEXT("Array<"), ESearchCase::IgnoreCase) || ContextType.EndsWith(TEXT("[]"))))
        {
            return TEXT("int");
        }
        return FString();
    }
    if (Kind.Equals(TEXT("binary"), ESearchCase::IgnoreCase))
    {
        const FString Op = GetStringField(Expression, TEXT("op"));
        if (Op.Equals(TEXT("&&")) || Op.Equals(TEXT("||")) || Op.Equals(TEXT("==")) || Op.Equals(TEXT("!="))
            || Op.Equals(TEXT("<")) || Op.Equals(TEXT("<=")) || Op.Equals(TEXT(">")) || Op.Equals(TEXT(">=")))
        {
            return TEXT("bool");
        }
        const FString LeftType = InferExpressionType(Context, GetObjectField(Expression, TEXT("left")));
        const FString RightType = InferExpressionType(Context, GetObjectField(Expression, TEXT("right")));
        return IsFloatLikeType(LeftType) || IsFloatLikeType(RightType) ? TEXT("float") : TEXT("int");
    }
    if (Kind.Equals(TEXT("unary"), ESearchCase::IgnoreCase))
    {
        return GetStringField(Expression, TEXT("op")).Equals(TEXT("!"), ESearchCase::CaseSensitive)
            ? TEXT("bool")
            : InferExpressionType(Context, GetObjectField(Expression, TEXT("operand")));
    }
    if (Kind.Equals(TEXT("conditional"), ESearchCase::IgnoreCase))
    {
        return InferExpressionType(Context, GetObjectField(Expression, TEXT("then")));
    }

    return FString();
}

static UFunction* FindKismetMathFunction(const TCHAR* Name)
{
    return UKismetMathLibrary::StaticClass()->FindFunctionByName(FName(Name));
}

static UFunction* FindKismetSystemFunction(const TCHAR* Name)
{
    return UKismetSystemLibrary::StaticClass()->FindFunctionByName(FName(Name));
}

static UFunction* FindKismetArrayFunction(const TCHAR* Name)
{
    return UKismetArrayLibrary::StaticClass()->FindFunctionByName(FName(Name));
}

static UClass* ResolveFunctionOwnerClass(const FString& TypeName)
{
    if (TypeName.Equals(TEXT("Actor"), ESearchCase::IgnoreCase) || TypeName.StartsWith(TEXT("Object<Actor"), ESearchCase::IgnoreCase))
    {
        return AActor::StaticClass();
    }
    if (TypeName.Equals(TEXT("SceneComponent"), ESearchCase::IgnoreCase))
    {
        return USceneComponent::StaticClass();
    }
    if (TypeName.Equals(TEXT("StaticMeshComponent"), ESearchCase::IgnoreCase))
    {
        return UStaticMeshComponent::StaticClass();
    }
    if (UClass* LoadedClass = LoadClassByPathOrName(TypeName, UObject::StaticClass()))
    {
        return LoadedClass;
    }
    return nullptr;
}

static UFunction* FindFunctionByKmsName(UClass* OwnerClass, const FString& Name)
{
    if (OwnerClass == nullptr || Name.IsEmpty())
    {
        return nullptr;
    }

    TArray<FName> CandidateNames;
    CandidateNames.Add(FName(*Name));
    if (Name.Equals(TEXT("SetRelativeRotation"), ESearchCase::IgnoreCase))
    {
        CandidateNames.Add(GET_FUNCTION_NAME_CHECKED(USceneComponent, K2_SetRelativeRotation));
    }

    for (const FName CandidateName : CandidateNames)
    {
        if (UFunction* Function = OwnerClass->FindFunctionByName(CandidateName))
        {
            return Function;
        }
    }

    return nullptr;
}

static UEdGraphPin* FindPinByName(UEdGraphNode* Node, const TCHAR* PinName)
{
    return Node != nullptr ? Node->FindPin(FName(PinName)) : nullptr;
}

static UEdGraphPin* FindPinByDirection(UEdGraphNode* Node, EEdGraphPinDirection Direction, FName ExcludedPinName = NAME_None)
{
    if (Node == nullptr)
    {
        return nullptr;
    }

    for (UEdGraphPin* Pin : Node->Pins)
    {
        if (Pin != nullptr && Pin->Direction == Direction && Pin->PinName != ExcludedPinName)
        {
            return Pin;
        }
    }
    return nullptr;
}

static UEdGraphPin* FindExecPinByName(UEdGraphNode* Node, FName PinName, EEdGraphPinDirection Direction)
{
    if (Node == nullptr)
    {
        return nullptr;
    }

    for (UEdGraphPin* Pin : Node->Pins)
    {
        if (Pin != nullptr && Pin->Direction == Direction && Pin->PinType.PinCategory == UEdGraphSchema_K2::PC_Exec && Pin->PinName == PinName)
        {
            return Pin;
        }
    }
    return nullptr;
}

static UEdGraphPin* FindExecPinByDisplayText(UEdGraphNode* Node, const FString& PinText, EEdGraphPinDirection Direction)
{
    if (Node == nullptr)
    {
        return nullptr;
    }

    for (UEdGraphPin* Pin : Node->Pins)
    {
        if (Pin == nullptr || Pin->Direction != Direction || Pin->PinType.PinCategory != UEdGraphSchema_K2::PC_Exec)
        {
            continue;
        }

        const FString PinName = Pin->PinName.ToString();
        const FString DisplayName = Pin->GetDisplayName().ToString();
        if (PinName.Equals(PinText, ESearchCase::IgnoreCase)
            || DisplayName.Equals(PinText, ESearchCase::IgnoreCase)
            || PinName.Contains(PinText, ESearchCase::IgnoreCase)
            || DisplayName.Contains(PinText, ESearchCase::IgnoreCase))
        {
            return Pin;
        }
    }

    return nullptr;
}

static bool TryConnectPins(const FKmsBpNativeGenContext& Context, UEdGraphPin* SourcePin, UEdGraphPin* TargetPin)
{
    return Context.K2Schema != nullptr
        && SourcePin != nullptr
        && TargetPin != nullptr
        && Context.K2Schema->TryCreateConnection(SourcePin, TargetPin);
}

static void AddGeneratedNodeToGraph(UEdGraph* Graph, UEdGraphNode* Node, const FString& Kind, int32 X, int32 Y)
{
    if (Graph == nullptr || Node == nullptr)
    {
        return;
    }

    Node->CreateNewGuid();
    Node->PostPlacedNewNode();
    Node->SetFlags(RF_Transactional);
    Node->AllocateDefaultPins();
    Node->NodePosX = X;
    Node->NodePosY = Y;
    MarkGeneratedKmsNode(Node, Kind);
    UEdGraphSchema_K2::SetNodeMetaData(Node, FNodeMetadata::DefaultGraphNode);
    Graph->AddNode(Node, true, false);
}

static UK2Node_CallFunction* SpawnCallFunctionNode(FKmsBpNativeGenContext& Context, UFunction* Function, const FString& Kind, int32 X, int32 Y)
{
    if (Context.Graph == nullptr || Function == nullptr)
    {
        return nullptr;
    }

    UK2Node_CallFunction* Node = NewObject<UK2Node_CallFunction>(Context.Graph);
    Node->SetFromFunction(Function);
    AddGeneratedNodeToGraph(Context.Graph, Node, Kind, X, Y);
    Node->ReconstructNode();
    return Node;
}

static UK2Node_CallArrayFunction* SpawnCallArrayFunctionNode(FKmsBpNativeGenContext& Context, UFunction* Function, const FString& Kind, int32 X, int32 Y)
{
    if (Context.Graph == nullptr || Function == nullptr)
    {
        return nullptr;
    }

    UK2Node_CallArrayFunction* Node = NewObject<UK2Node_CallArrayFunction>(Context.Graph);
    Node->SetFromFunction(Function);
    AddGeneratedNodeToGraph(Context.Graph, Node, Kind, X, Y);
    Node->ReconstructNode();
    return Node;
}

static UEdGraph* LoadStandardMacroGraph(const TCHAR* MacroName)
{
    UBlueprint* StandardMacros = LoadObject<UBlueprint>(
        nullptr,
        TEXT("/Engine/EditorBlueprintResources/StandardMacros.StandardMacros"));
    if (StandardMacros == nullptr)
    {
        return nullptr;
    }

    for (UEdGraph* MacroGraph : StandardMacros->MacroGraphs)
    {
        if (MacroGraph != nullptr && MacroGraph->GetName().Equals(MacroName, ESearchCase::CaseSensitive))
        {
            return MacroGraph;
        }
    }

    return nullptr;
}

static UK2Node_MacroInstance* SpawnStandardMacroNode(FKmsBpNativeGenContext& Context, const TCHAR* MacroName, const FString& Kind, int32 X, int32 Y)
{
    UEdGraph* MacroGraph = LoadStandardMacroGraph(MacroName);
    if (Context.Graph == nullptr || MacroGraph == nullptr)
    {
        return nullptr;
    }

    UK2Node_MacroInstance* Node = NewObject<UK2Node_MacroInstance>(Context.Graph);
    Node->SetMacroGraph(MacroGraph);
    AddGeneratedNodeToGraph(Context.Graph, Node, Kind, X, Y);
    Node->ReconstructNode();
    return Node;
}

static bool TryResolvePinTypeForNativeGen(FKmsBpNativeGenContext& Context, const FString& TypeText, FEdGraphPinType& OutPinType)
{
    if (Context.ImportResult == nullptr)
    {
        return false;
    }

    return ResolvePinType(TypeText, OutPinType, *Context.ImportResult, false);
}

static UK2Node_VariableGet* SpawnVariableGetNode(FKmsBpNativeGenContext& Context, const FString& Name, int32 X, int32 Y)
{
    if (Context.Graph == nullptr || Context.Blueprint == nullptr)
    {
        return nullptr;
    }

    const FString GeneratedName = ResolveGeneratedVariableName(Context, Name);
    UK2Node_VariableGet* Node = NewObject<UK2Node_VariableGet>(Context.Graph);
    const FName VariableName(*GeneratedName);
    if (Context.LocalVariables.Contains(GeneratedName))
    {
        Node->VariableReference.SetLocalMember(VariableName, Context.Graph->GetName(), FBlueprintEditorUtils::FindLocalVariableGuidByName(Context.Blueprint, Context.Graph, VariableName));
    }
    else
    {
        Node->VariableReference.SetSelfMember(VariableName, FBlueprintEditorUtils::FindMemberVariableGuidByName(Context.Blueprint, VariableName));
    }

    AddGeneratedNodeToGraph(Context.Graph, Node, FString::Printf(TEXT("get %s"), *GeneratedName), X, Y);
    Node->ReconstructNode();
    return Node;
}

static UK2Node_VariableSet* SpawnVariableSetNode(FKmsBpNativeGenContext& Context, const FString& Name, int32 X, int32 Y)
{
    if (Context.Graph == nullptr || Context.Blueprint == nullptr)
    {
        return nullptr;
    }

    const FString GeneratedName = ResolveGeneratedVariableName(Context, Name);
    UK2Node_VariableSet* Node = NewObject<UK2Node_VariableSet>(Context.Graph);
    const FName VariableName(*GeneratedName);
    if (Context.LocalVariables.Contains(GeneratedName))
    {
        Node->VariableReference.SetLocalMember(VariableName, Context.Graph->GetName(), FBlueprintEditorUtils::FindLocalVariableGuidByName(Context.Blueprint, Context.Graph, VariableName));
    }
    else
    {
        Node->VariableReference.SetSelfMember(VariableName, FBlueprintEditorUtils::FindMemberVariableGuidByName(Context.Blueprint, VariableName));
    }

    AddGeneratedNodeToGraph(Context.Graph, Node, FString::Printf(TEXT("set %s"), *GeneratedName), X, Y);
    Node->ReconstructNode();
    return Node;
}

static UEdGraphPin* GetVariableSetValuePin(UK2Node_VariableSet* Node)
{
    return Node != nullptr ? Node->FindPin(Node->GetVarName(), EGPD_Input) : nullptr;
}

static UEdGraphPin* FindSelfPin(UEdGraphNode* Node)
{
    if (Node == nullptr)
    {
        return nullptr;
    }

    for (UEdGraphPin* Pin : Node->Pins)
    {
        if (Pin != nullptr
            && Pin->Direction == EGPD_Input
            && Pin->PinType.PinCategory == UEdGraphSchema_K2::PC_Object
            && (Pin->PinName == UEdGraphSchema_K2::PN_Self || Pin->PinName == TEXT("self") || Pin->PinName == TEXT("Target")))
        {
            return Pin;
        }
    }

    return nullptr;
}

static UEdGraphPin* FindNextValueInputPin(UEdGraphNode* Node, int32& PositionalIndex)
{
    if (Node == nullptr)
    {
        return nullptr;
    }

    int32 Seen = 0;
    for (UEdGraphPin* Pin : Node->Pins)
    {
        if (Pin == nullptr
            || Pin->Direction != EGPD_Input
            || Pin->PinType.PinCategory == UEdGraphSchema_K2::PC_Exec
            || Pin == FindSelfPin(Node))
        {
            continue;
        }

        if (Seen++ == PositionalIndex)
        {
            ++PositionalIndex;
            return Pin;
        }
    }

    return nullptr;
}

static bool TrySetPinDefaultFromLiteral(const FKmsBpNativeGenContext& Context, const TSharedPtr<FJsonObject>& Expression, UEdGraphPin* Pin)
{
    if (Context.K2Schema == nullptr || Pin == nullptr || !Expression.IsValid()
        || !GetStringField(Expression, TEXT("kind")).Equals(TEXT("literal"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    const FString DefaultValue = DefaultValueToString(Expression);
    if (DefaultValue.IsEmpty() && !IsLiteralExpression(Expression, TEXT("string")))
    {
        return false;
    }

    Context.K2Schema->TrySetDefaultValue(*Pin, DefaultValue, false);
    return true;
}

static const TCHAR* GetBinaryFunctionName(const FString& Op, const FString& ResultType, const FString& LeftType, const FString& RightType)
{
    if (Op.Equals(TEXT("&&"))) return TEXT("BooleanAND");
    if (Op.Equals(TEXT("||"))) return TEXT("BooleanOR");

    if (LeftType.Equals(TEXT("Object"), ESearchCase::IgnoreCase) || RightType.Equals(TEXT("Object"), ESearchCase::IgnoreCase)
        || LeftType.StartsWith(TEXT("Object<"), ESearchCase::IgnoreCase) || RightType.StartsWith(TEXT("Object<"), ESearchCase::IgnoreCase))
    {
        if (Op.Equals(TEXT("=="))) return TEXT("EqualEqual_ObjectObject");
        if (Op.Equals(TEXT("!="))) return TEXT("NotEqual_ObjectObject");
        return nullptr;
    }

    if (LeftType.Equals(TEXT("bool"), ESearchCase::IgnoreCase) || RightType.Equals(TEXT("bool"), ESearchCase::IgnoreCase))
    {
        if (Op.Equals(TEXT("=="))) return TEXT("EqualEqual_BoolBool");
        if (Op.Equals(TEXT("!="))) return TEXT("NotEqual_BoolBool");
        return nullptr;
    }

    if (IsFloatLikeType(ResultType) || IsFloatLikeType(LeftType) || IsFloatLikeType(RightType))
    {
        if (Op.Equals(TEXT("+"))) return TEXT("Add_DoubleDouble");
        if (Op.Equals(TEXT("-"))) return TEXT("Subtract_DoubleDouble");
        if (Op.Equals(TEXT("*"))) return TEXT("Multiply_DoubleDouble");
        if (Op.Equals(TEXT("/"))) return TEXT("Divide_DoubleDouble");
        if (Op.Equals(TEXT("<"))) return TEXT("Less_DoubleDouble");
        if (Op.Equals(TEXT("<="))) return TEXT("LessEqual_DoubleDouble");
        if (Op.Equals(TEXT(">"))) return TEXT("Greater_DoubleDouble");
        if (Op.Equals(TEXT(">="))) return TEXT("GreaterEqual_DoubleDouble");
        if (Op.Equals(TEXT("=="))) return TEXT("EqualEqual_DoubleDouble");
        if (Op.Equals(TEXT("!="))) return TEXT("NotEqual_DoubleDouble");
        return nullptr;
    }

    if (Op.Equals(TEXT("+"))) return TEXT("Add_IntInt");
    if (Op.Equals(TEXT("-"))) return TEXT("Subtract_IntInt");
    if (Op.Equals(TEXT("*"))) return TEXT("Multiply_IntInt");
    if (Op.Equals(TEXT("/"))) return TEXT("Divide_IntInt");
    if (Op.Equals(TEXT("<"))) return TEXT("Less_IntInt");
    if (Op.Equals(TEXT("<="))) return TEXT("LessEqual_IntInt");
    if (Op.Equals(TEXT(">"))) return TEXT("Greater_IntInt");
    if (Op.Equals(TEXT(">="))) return TEXT("GreaterEqual_IntInt");
    if (Op.Equals(TEXT("=="))) return TEXT("EqualEqual_IntInt");
    if (Op.Equals(TEXT("!="))) return TEXT("NotEqual_IntInt");
    return nullptr;
}

static UEdGraphPin* GenerateExpressionPin(FKmsBpNativeGenContext& Context, const TSharedPtr<FJsonObject>& Expression, int32 Depth);
static UK2Node_CallFunction* SpawnResolvedCallNode(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    int32 X,
    int32 Y,
    int32 Depth);
static UEdGraphPin* GenerateArrayExpressionPin(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    int32 Depth,
    const FEdGraphPinType* ExpectedArrayType = nullptr);

static bool ConnectExpressionToPin(FKmsBpNativeGenContext& Context, const TSharedPtr<FJsonObject>& Expression, UEdGraphPin* TargetPin, int32 Depth)
{
    if (TargetPin == nullptr || !Expression.IsValid())
    {
        return false;
    }
    if (TrySetPinDefaultFromLiteral(Context, Expression, TargetPin))
    {
        return true;
    }
    if (GetStringField(Expression, TEXT("kind")).Equals(TEXT("array"), ESearchCase::IgnoreCase))
    {
        return TryConnectPins(Context, GenerateArrayExpressionPin(Context, Expression, Depth, &TargetPin->PinType), TargetPin);
    }
    return TryConnectPins(Context, GenerateExpressionPin(Context, Expression, Depth), TargetPin);
}

static UEdGraphPin* GenerateBinaryExpressionPin(FKmsBpNativeGenContext& Context, const TSharedPtr<FJsonObject>& Expression, int32 Depth)
{
    const FString Op = GetStringField(Expression, TEXT("op"));
    if (Op.Equals(TEXT("=")) || Op.Equals(TEXT("+=")) || Op.Equals(TEXT("-=")) || Op.Equals(TEXT("*=")) || Op.Equals(TEXT("/=")))
    {
        return nullptr;
    }

    const TSharedPtr<FJsonObject> Left = GetObjectField(Expression, TEXT("left"));
    const TSharedPtr<FJsonObject> Right = GetObjectField(Expression, TEXT("right"));
    const FString LeftType = InferExpressionType(Context, Left);
    const FString RightType = InferExpressionType(Context, Right);
    const FString ResultType = InferExpressionType(Context, Expression);
    const TCHAR* FunctionName = GetBinaryFunctionName(Op, ResultType, LeftType, RightType);
    if (FunctionName == nullptr)
    {
        return nullptr;
    }

    UK2Node_CallFunction* Node = SpawnCallFunctionNode(Context, FindKismetMathFunction(FunctionName), FString::Printf(TEXT("binary %s"), *Op), 620 + Depth * 260, Context.NextExpressionY);
    Context.NextExpressionY += 120;
    if (Node == nullptr)
    {
        return nullptr;
    }

    ConnectExpressionToPin(Context, Left, FindPinByName(Node, TEXT("A")), Depth + 1);
    ConnectExpressionToPin(Context, Right, FindPinByName(Node, TEXT("B")), Depth + 1);
    return Node->GetReturnValuePin();
}

static UEdGraphPin* GenerateArrayExpressionPin(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    int32 Depth,
    const FEdGraphPinType* ExpectedArrayType)
{
    const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
    if (!GetArrayField(Expression, TEXT("items"), Items))
    {
        return nullptr;
    }

    UK2Node_MakeArray* Node = NewObject<UK2Node_MakeArray>(Context.Graph);
    Node->NumInputs = Items->Num();
    AddGeneratedNodeToGraph(Context.Graph, Node, TEXT("array"), 620 + Depth * 260, Context.NextExpressionY);
    Context.NextExpressionY += 120;

    UEdGraphPin* OutputPin = Node->GetOutputPin();
    if (OutputPin == nullptr)
    {
        return nullptr;
    }

    FEdGraphPinType ArrayPinType;
    if (ExpectedArrayType != nullptr && ExpectedArrayType->ContainerType == EPinContainerType::Array)
    {
        ArrayPinType = *ExpectedArrayType;
    }
    else
    {
        const FString InferredType = InferExpressionType(Context, Expression);
        if (InferredType.IsEmpty() || !TryResolvePinTypeForNativeGen(Context, InferredType, ArrayPinType))
        {
            return OutputPin;
        }
    }

    OutputPin->PinType = ArrayPinType;
    FEdGraphPinType ElementPinType = ArrayPinType;
    ElementPinType.ContainerType = EPinContainerType::None;

    for (int32 Index = 0; Index < Items->Num(); ++Index)
    {
        UEdGraphPin* ItemPin = Node->FindPin(Node->GetPinName(Index), EGPD_Input);
        if (ItemPin == nullptr)
        {
            continue;
        }

        ItemPin->PinType = ElementPinType;
        if (Context.K2Schema != nullptr)
        {
            Context.K2Schema->SetPinAutogeneratedDefaultValueBasedOnType(ItemPin);
        }
        ConnectExpressionToPin(Context, (*Items)[Index].IsValid() ? (*Items)[Index]->AsObject() : nullptr, ItemPin, Depth + 1);
    }

    return OutputPin;
}

static UEdGraphPin* GenerateSubscriptExpressionPin(FKmsBpNativeGenContext& Context, const TSharedPtr<FJsonObject>& Expression, int32 Depth)
{
    const TSharedPtr<FJsonObject> ArrayExpression = GetObjectField(Expression, TEXT("context"));
    FString ElementType;
    if (!TryGetArrayElementType(InferExpressionType(Context, ArrayExpression), ElementType))
    {
        return nullptr;
    }

    UK2Node_CallArrayFunction* Node = SpawnCallArrayFunctionNode(
        Context,
        FindKismetArrayFunction(TEXT("Array_Get")),
        TEXT("subscript"),
        620 + Depth * 260,
        Context.NextExpressionY);
    Context.NextExpressionY += 120;
    if (Node == nullptr)
    {
        return nullptr;
    }

    UEdGraphPin* ArrayPin = GenerateExpressionPin(Context, ArrayExpression, Depth + 1);
    UEdGraphPin* TargetArrayPin = Node->GetTargetArrayPin();
    if (ArrayPin != nullptr)
    {
        FEdGraphPinType ElementPinType = ArrayPin->PinType;
        ElementPinType.ContainerType = EPinContainerType::None;
        if (TargetArrayPin != nullptr)
        {
            TargetArrayPin->PinType = ArrayPin->PinType;
        }
        if (UEdGraphPin* ItemPin = FindPinByName(Node, TEXT("Item")))
        {
            ItemPin->PinType = ElementPinType;
        }
        TryConnectPins(Context, ArrayPin, TargetArrayPin);
    }

    ConnectExpressionToPin(Context, GetObjectField(Expression, TEXT("index")), FindPinByName(Node, TEXT("Index")), Depth + 1);
    return FindPinByName(Node, TEXT("Item"));
}

static UEdGraphPin* GenerateArrayLengthExpressionPin(FKmsBpNativeGenContext& Context, const TSharedPtr<FJsonObject>& Expression, int32 Depth)
{
    if (!GetStringField(GetObjectField(Expression, TEXT("member")), TEXT("name")).Equals(TEXT("Length"), ESearchCase::IgnoreCase))
    {
        return nullptr;
    }

    const TSharedPtr<FJsonObject> ArrayExpression = GetObjectField(Expression, TEXT("context"));
    FString ElementType;
    if (!TryGetArrayElementType(InferExpressionType(Context, ArrayExpression), ElementType))
    {
        return nullptr;
    }

    UK2Node_CallArrayFunction* Node = SpawnCallArrayFunctionNode(
        Context,
        FindKismetArrayFunction(TEXT("Array_Length")),
        TEXT("array length"),
        620 + Depth * 260,
        Context.NextExpressionY);
    Context.NextExpressionY += 120;
    if (Node == nullptr)
    {
        return nullptr;
    }

    UEdGraphPin* ArrayPin = GenerateExpressionPin(Context, ArrayExpression, Depth + 1);
    UEdGraphPin* TargetArrayPin = Node->GetTargetArrayPin();
    if (ArrayPin != nullptr)
    {
        if (TargetArrayPin != nullptr)
        {
            TargetArrayPin->PinType = ArrayPin->PinType;
        }
        TryConnectPins(Context, ArrayPin, TargetArrayPin);
    }

    return Node->GetReturnValuePin();
}

static UEdGraphPin* GenerateConditionalExpressionPin(FKmsBpNativeGenContext& Context, const TSharedPtr<FJsonObject>& Expression, int32 Depth)
{
    const TSharedPtr<FJsonObject> Then = GetObjectField(Expression, TEXT("then"));
    const TSharedPtr<FJsonObject> Else = GetObjectField(Expression, TEXT("else"));
    bool BoolValue = false;
    if (TryGetLiteralBool(Then, BoolValue) && BoolValue)
    {
        TSharedPtr<FJsonObject> OrExpression = MakeShared<FJsonObject>();
        OrExpression->SetStringField(TEXT("kind"), TEXT("binary"));
        OrExpression->SetStringField(TEXT("op"), TEXT("||"));
        OrExpression->SetObjectField(TEXT("left"), GetObjectField(Expression, TEXT("condition")));
        OrExpression->SetObjectField(TEXT("right"), Else);
        return GenerateBinaryExpressionPin(Context, OrExpression, Depth);
    }

    if (TryGetLiteralBool(Else, BoolValue) && !BoolValue)
    {
        TSharedPtr<FJsonObject> AndExpression = MakeShared<FJsonObject>();
        AndExpression->SetStringField(TEXT("kind"), TEXT("binary"));
        AndExpression->SetStringField(TEXT("op"), TEXT("&&"));
        AndExpression->SetObjectField(TEXT("left"), GetObjectField(Expression, TEXT("condition")));
        AndExpression->SetObjectField(TEXT("right"), Then);
        return GenerateBinaryExpressionPin(Context, AndExpression, Depth);
    }

    return nullptr;
}

static UEdGraphPin* GenerateExpressionPin(FKmsBpNativeGenContext& Context, const TSharedPtr<FJsonObject>& Expression, int32 Depth)
{
    if (!Expression.IsValid())
    {
        return nullptr;
    }

    const FString Kind = GetStringField(Expression, TEXT("kind"));
    if (Kind.Equals(TEXT("identifier"), ESearchCase::IgnoreCase))
    {
        const FString Name = GetStringField(Expression, TEXT("name"));
        if (Name.Equals(TEXT("self"), ESearchCase::IgnoreCase)
            || Name.Equals(TEXT("none"), ESearchCase::IgnoreCase)
            || Name.Equals(TEXT("KismetSystemLibrary"), ESearchCase::IgnoreCase)
            || Name.Equals(TEXT("MathLibrary"), ESearchCase::IgnoreCase)
            || Name.Equals(TEXT("KismetMathLibrary"), ESearchCase::IgnoreCase))
        {
            return nullptr;
        }

        UK2Node_VariableGet* Node = SpawnVariableGetNode(Context, Name, 620 + Depth * 260, Context.NextExpressionY);
        Context.NextExpressionY += 120;
        return Node != nullptr ? Node->GetValuePin() : nullptr;
    }
    if (Kind.Equals(TEXT("binary"), ESearchCase::IgnoreCase))
    {
        return GenerateBinaryExpressionPin(Context, Expression, Depth);
    }
    if (Kind.Equals(TEXT("array"), ESearchCase::IgnoreCase))
    {
        return GenerateArrayExpressionPin(Context, Expression, Depth);
    }
    if (Kind.Equals(TEXT("subscript"), ESearchCase::IgnoreCase))
    {
        return GenerateSubscriptExpressionPin(Context, Expression, Depth);
    }
    if (Kind.Equals(TEXT("member"), ESearchCase::IgnoreCase))
    {
        if (UEdGraphPin* LengthPin = GenerateArrayLengthExpressionPin(Context, Expression, Depth))
        {
            return LengthPin;
        }
    }
    if (Kind.Equals(TEXT("unary"), ESearchCase::IgnoreCase) && GetStringField(Expression, TEXT("op")).Equals(TEXT("!"), ESearchCase::CaseSensitive))
    {
        UK2Node_CallFunction* Node = SpawnCallFunctionNode(Context, FindKismetMathFunction(TEXT("Not_PreBool")), TEXT("unary !"), 620 + Depth * 260, Context.NextExpressionY);
        Context.NextExpressionY += 120;
        if (Node != nullptr)
        {
            ConnectExpressionToPin(Context, GetObjectField(Expression, TEXT("operand")), FindPinByName(Node, TEXT("A")), Depth + 1);
            return Node->GetReturnValuePin();
        }
    }
    if (Kind.Equals(TEXT("cast"), ESearchCase::IgnoreCase))
    {
        const TSharedPtr<FJsonObject> Operand = GetObjectField(Expression, TEXT("operand"));
        if (GetStringField(Expression, TEXT("type")).Equals(TEXT("float"), ESearchCase::IgnoreCase) && IsIntLikeType(InferExpressionType(Context, Operand)))
        {
            UK2Node_CallFunction* Node = SpawnCallFunctionNode(Context, FindKismetMathFunction(TEXT("Conv_IntToFloat")), TEXT("cast float"), 620 + Depth * 260, Context.NextExpressionY);
            Context.NextExpressionY += 120;
            if (Node != nullptr)
            {
                ConnectExpressionToPin(Context, Operand, FindPinByName(Node, TEXT("InInt")), Depth + 1);
                return Node->GetReturnValuePin();
            }
        }
        return GenerateExpressionPin(Context, Operand, Depth + 1);
    }
    if (Kind.Equals(TEXT("conditional"), ESearchCase::IgnoreCase))
    {
        return GenerateConditionalExpressionPin(Context, Expression, Depth);
    }
    if (Kind.Equals(TEXT("call"), ESearchCase::IgnoreCase) || Kind.Equals(TEXT("member"), ESearchCase::IgnoreCase))
    {
        if (Kind.Equals(TEXT("call"), ESearchCase::IgnoreCase) && GetStringField(Expression, TEXT("name")).Equals(TEXT("asset"), ESearchCase::IgnoreCase))
        {
            return nullptr;
        }
        UK2Node_CallFunction* Node = SpawnResolvedCallNode(Context, Expression, 620 + Depth * 260, Context.NextExpressionY, Depth);
        Context.NextExpressionY += 120;
        return Node != nullptr ? Node->GetReturnValuePin() : nullptr;
    }

    SpawnGeneratedKmsNode(Context.Graph, false, Kind.IsEmpty() ? TEXT("expression") : Kind, ExpressionToText(Expression), 620 + Depth * 260, Context.NextExpressionY);
    Context.NextExpressionY += 120;
    return nullptr;
}

static void MaterializeExpressionNodes(UEdGraph* Graph, const TSharedPtr<FJsonObject>& Expression, int32 Depth, int32& NextY)
{
    if (!Expression.IsValid())
    {
        return;
    }

    const FString Kind = GetStringField(Expression, TEXT("kind"));
    SpawnGeneratedKmsNode(
        Graph,
        false,
        Kind.IsEmpty() ? TEXT("expression") : Kind,
        ExpressionToText(Expression),
        620 + Depth * 300,
        NextY);
    NextY += 120;

    static const TCHAR* ObjectFields[] =
    {
        TEXT("context"),
        TEXT("member"),
        TEXT("left"),
        TEXT("right"),
        TEXT("operand"),
        TEXT("index"),
        TEXT("condition"),
        TEXT("then"),
        TEXT("else")
    };

    for (const TCHAR* FieldName : ObjectFields)
    {
        MaterializeExpressionNodes(Graph, GetObjectField(Expression, FieldName), Depth + 1, NextY);
    }

    const TArray<TSharedPtr<FJsonValue>>* Arguments = nullptr;
    if (GetArrayField(Expression, TEXT("arguments"), Arguments))
    {
        for (const TSharedPtr<FJsonValue>& Argument : *Arguments)
        {
            MaterializeExpressionNodes(Graph, GetArgumentExpression(Argument), Depth + 1, NextY);
        }
    }

    const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
    if (GetArrayField(Expression, TEXT("items"), Items))
    {
        for (const TSharedPtr<FJsonValue>& Item : *Items)
        {
            MaterializeExpressionNodes(Graph, Item.IsValid() ? Item->AsObject() : nullptr, Depth + 1, NextY);
        }
    }

    const TArray<TSharedPtr<FJsonValue>>* Entries = nullptr;
    if (GetArrayField(Expression, TEXT("entries"), Entries))
    {
        for (const TSharedPtr<FJsonValue>& EntryValue : *Entries)
        {
            const TSharedPtr<FJsonObject> Entry = EntryValue.IsValid() ? EntryValue->AsObject() : nullptr;
            MaterializeExpressionNodes(Graph, GetObjectField(Entry, TEXT("key")), Depth + 1, NextY);
            MaterializeExpressionNodes(Graph, GetObjectField(Entry, TEXT("value")), Depth + 1, NextY);
        }
    }
}

static void MaterializeStatementNodes(
    UEdGraph* Graph,
    const TSharedPtr<FJsonObject>& Statement,
    const bool bUseExecNodes,
    UEdGraphPin*& ExecTail,
    int32 Depth,
    int32& NextY)
{
    if (!Statement.IsValid())
    {
        return;
    }

    const FString Kind = GetStringField(Statement, TEXT("kind"));
    if (!Kind.Equals(TEXT("block"), ESearchCase::IgnoreCase))
    {
        UK2Node_KmsGeneratedNode* Node = SpawnGeneratedKmsNode(
            Graph,
            bUseExecNodes,
            Kind.IsEmpty() ? TEXT("statement") : Kind,
            StatementToText(Statement),
            260 + Depth * 240,
            NextY);
        if (bUseExecNodes)
        {
            TryConnectExecPins(Graph, ExecTail, Node);
        }
        NextY += 150;
    }

    static const TCHAR* ExpressionFields[] =
    {
        TEXT("initializer"),
        TEXT("expression"),
        TEXT("condition"),
        TEXT("value"),
        TEXT("after"),
        TEXT("collection"),
        TEXT("target"),
        TEXT("handler"),
        TEXT("switchOn")
    };

    for (const TCHAR* FieldName : ExpressionFields)
    {
        MaterializeExpressionNodes(Graph, GetObjectField(Statement, FieldName), Depth + 1, NextY);
    }

    const TArray<TSharedPtr<FJsonValue>>* Statements = nullptr;
    if (GetArrayField(Statement, TEXT("statements"), Statements))
    {
        for (const TSharedPtr<FJsonValue>& Child : *Statements)
        {
            MaterializeStatementNodes(Graph, Child.IsValid() ? Child->AsObject() : nullptr, bUseExecNodes, ExecTail, Depth, NextY);
        }
    }

    MaterializeStatementNodes(Graph, GetObjectField(Statement, TEXT("then")), bUseExecNodes, ExecTail, Depth + 1, NextY);
    MaterializeStatementNodes(Graph, GetObjectField(Statement, TEXT("else")), bUseExecNodes, ExecTail, Depth + 1, NextY);
    MaterializeStatementNodes(Graph, GetObjectField(Statement, TEXT("body")), bUseExecNodes, ExecTail, Depth + 1, NextY);
    MaterializeStatementNodes(Graph, GetObjectField(Statement, TEXT("initializerStatement")), bUseExecNodes, ExecTail, Depth + 1, NextY);

    const TArray<TSharedPtr<FJsonValue>>* Cases = nullptr;
    if (GetArrayField(Statement, TEXT("cases"), Cases))
    {
        for (const TSharedPtr<FJsonValue>& CaseValue : *Cases)
        {
            const TSharedPtr<FJsonObject> CaseObject = CaseValue.IsValid() ? CaseValue->AsObject() : nullptr;
            MaterializeExpressionNodes(Graph, GetObjectField(CaseObject, TEXT("condition")), Depth + 1, NextY);
            MaterializeStatementNodes(Graph, GetObjectField(CaseObject, TEXT("body")), bUseExecNodes, ExecTail, Depth + 1, NextY);
        }
    }
}

static TArray<UEdGraphPin*> ConnectExecTails(FKmsBpNativeGenContext& Context, const TArray<UEdGraphPin*>& ExecTails, UK2Node* Node)
{
    TArray<UEdGraphPin*> OutTails;
    if (Node == nullptr)
    {
        return ExecTails;
    }

    UEdGraphPin* ExecutePin = Node->GetExecPin();
    if (ExecutePin == nullptr)
    {
        return ExecTails;
    }
    for (UEdGraphPin* ExecTail : ExecTails)
    {
        TryConnectPins(Context, ExecTail, ExecutePin);
    }

    if (UEdGraphPin* ThenPin = Node->GetThenPin())
    {
        OutTails.Add(ThenPin);
    }
    return OutTails;
}

static TArray<UEdGraphPin*> ConnectExecTailsToGeneratedNode(
    FKmsBpNativeGenContext& Context,
    const TArray<UEdGraphPin*>& ExecTails,
    UK2Node_KmsGeneratedNode* Node)
{
    TArray<UEdGraphPin*> OutTails;
    if (Node == nullptr)
    {
        return ExecTails;
    }

    UEdGraphPin* ExecutePin = Node->FindPin(UEdGraphSchema_K2::PN_Execute);
    for (UEdGraphPin* ExecTail : ExecTails)
    {
        TryConnectPins(Context, ExecTail, ExecutePin);
    }

    if (UEdGraphPin* ThenPin = Node->FindPin(UEdGraphSchema_K2::PN_Then))
    {
        OutTails.Add(ThenPin);
    }
    return OutTails;
}

static bool GenerateAssignmentStatement(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    const TArray<UEdGraphPin*>& ExecTails,
    TArray<UEdGraphPin*>& OutExecTails,
    int32 Depth,
    int32& NextY)
{
    if (!Expression.IsValid() || !GetStringField(Expression, TEXT("kind")).Equals(TEXT("binary"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    const FString Op = GetStringField(Expression, TEXT("op"));
    if (!Op.Equals(TEXT("=")) && !Op.Equals(TEXT("+=")) && !Op.Equals(TEXT("-=")) && !Op.Equals(TEXT("*=")) && !Op.Equals(TEXT("/=")))
    {
        return false;
    }

    const TSharedPtr<FJsonObject> Left = GetObjectField(Expression, TEXT("left"));
    if (!Left.IsValid() || !GetStringField(Left, TEXT("kind")).Equals(TEXT("identifier"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    UK2Node_VariableSet* SetNode = SpawnVariableSetNode(Context, GetStringField(Left, TEXT("name")), 260 + Depth * 240, NextY);
    if (SetNode == nullptr)
    {
        return false;
    }

    OutExecTails = ConnectExecTails(Context, ExecTails, SetNode);
    TSharedPtr<FJsonObject> ValueExpression = GetObjectField(Expression, TEXT("right"));
    if (!Op.Equals(TEXT("=")))
    {
        TSharedPtr<FJsonObject> CompoundExpression = MakeShared<FJsonObject>();
        CompoundExpression->SetStringField(TEXT("kind"), TEXT("binary"));
        CompoundExpression->SetStringField(TEXT("op"), Op.Left(1));
        CompoundExpression->SetObjectField(TEXT("left"), Left);
        CompoundExpression->SetObjectField(TEXT("right"), ValueExpression);
        ValueExpression = CompoundExpression;
    }

    if (!ConnectExpressionToPin(Context, ValueExpression, GetVariableSetValuePin(SetNode), Depth + 1))
    {
        MaterializeExpressionNodes(Context.Graph, ValueExpression, Depth + 1, Context.NextExpressionY);
    }

    NextY += 180;
    return true;
}

static bool GeneratePrintStatement(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    const TArray<UEdGraphPin*>& ExecTails,
    TArray<UEdGraphPin*>& OutExecTails,
    int32 Depth,
    int32& NextY)
{
    if (!Expression.IsValid()
        || !GetStringField(Expression, TEXT("kind")).Equals(TEXT("call"), ESearchCase::IgnoreCase)
        || !GetStringField(Expression, TEXT("name")).Equals(TEXT("print"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    UK2Node_CallFunction* Node = SpawnCallFunctionNode(Context, FindKismetSystemFunction(TEXT("PrintString")), TEXT("call print"), 260 + Depth * 240, NextY);
    if (Node == nullptr)
    {
        return false;
    }

    OutExecTails = ConnectExecTails(Context, ExecTails, Node);
    const TArray<TSharedPtr<FJsonValue>>* Arguments = nullptr;
    if (GetArrayField(Expression, TEXT("arguments"), Arguments) && Arguments->Num() > 0)
    {
        ConnectExpressionToPin(Context, GetArgumentExpression((*Arguments)[0]), FindPinByName(Node, TEXT("InString")), Depth + 1);
    }

    NextY += 180;
    return true;
}

static UFunction* ResolveCallFunction(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    TSharedPtr<FJsonObject>& OutTargetExpression,
    bool& bOutSelfContext)
{
    OutTargetExpression.Reset();
    bOutSelfContext = true;

    FString FunctionName = GetStringField(Expression, TEXT("name"));
    TSharedPtr<FJsonObject> CallExpression = Expression;
    const FString Kind = GetStringField(Expression, TEXT("kind"));
    if (Kind.Equals(TEXT("member"), ESearchCase::IgnoreCase))
    {
        OutTargetExpression = GetObjectField(Expression, TEXT("context"));
        CallExpression = GetObjectField(Expression, TEXT("member"));
        FunctionName = GetStringField(CallExpression, TEXT("name"));
        bOutSelfContext = false;
    }

    if (FunctionName.IsEmpty())
    {
        return nullptr;
    }

    if (FunctionName.Equals(TEXT("Rotator"), ESearchCase::IgnoreCase))
    {
        bOutSelfContext = false;
        return FindKismetMathFunction(TEXT("MakeRotator"));
    }
    if (FunctionName.Equals(TEXT("Clamp"), ESearchCase::IgnoreCase))
    {
        bOutSelfContext = false;
        const FString TypeArg = GetStringField(CallExpression, TEXT("type"));
        return FindKismetMathFunction(TEXT("FClamp"));
    }
    if (FunctionName.Equals(TEXT("PrintString"), ESearchCase::IgnoreCase) || FunctionName.Equals(TEXT("print"), ESearchCase::IgnoreCase))
    {
        bOutSelfContext = false;
        return FindKismetSystemFunction(TEXT("PrintString"));
    }

    if (OutTargetExpression.IsValid())
    {
        const FString TargetText = ExpressionToText(OutTargetExpression);
        if (TargetText.Equals(TEXT("KismetSystemLibrary"), ESearchCase::IgnoreCase))
        {
            bOutSelfContext = false;
            return FindKismetSystemFunction(*FunctionName);
        }
        if (TargetText.Equals(TEXT("MathLibrary"), ESearchCase::IgnoreCase) || TargetText.Equals(TEXT("KismetMathLibrary"), ESearchCase::IgnoreCase))
        {
            bOutSelfContext = false;
            if (FunctionName.Equals(TEXT("Clamp"), ESearchCase::IgnoreCase))
            {
                return FindKismetMathFunction(TEXT("FClamp"));
            }
            return FindKismetMathFunction(*FunctionName);
        }

        const FString TargetType = InferExpressionType(Context, OutTargetExpression);
        if (UClass* OwnerClass = ResolveFunctionOwnerClass(TargetType))
        {
            return FindFunctionByKmsName(OwnerClass, FunctionName);
        }
    }

    if (Context.Blueprint != nullptr)
    {
        if (Context.Blueprint->SkeletonGeneratedClass != nullptr)
        {
            if (UFunction* Function = Context.Blueprint->SkeletonGeneratedClass->FindFunctionByName(FName(*FunctionName)))
            {
                bOutSelfContext = true;
                return Function;
            }
        }
        if (Context.Blueprint->GeneratedClass != nullptr)
        {
            if (UFunction* Function = Context.Blueprint->GeneratedClass->FindFunctionByName(FName(*FunctionName)))
            {
                bOutSelfContext = true;
                return Function;
            }
        }
    }

    return nullptr;
}

static bool ConnectCallArguments(
    FKmsBpNativeGenContext& Context,
    UEdGraphNode* Node,
    const TSharedPtr<FJsonObject>& CallExpression,
    int32 Depth)
{
    if (Node == nullptr || !CallExpression.IsValid())
    {
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Arguments = nullptr;
    if (!GetArrayField(CallExpression, TEXT("arguments"), Arguments))
    {
        return true;
    }

    int32 PositionalIndex = 0;
    for (const TSharedPtr<FJsonValue>& Argument : *Arguments)
    {
        UEdGraphPin* TargetPin = nullptr;
        const FString ArgumentName = GetArgumentName(Argument);
        if (!ArgumentName.IsEmpty())
        {
            TargetPin = FindPinByName(Node, *ArgumentName);
        }
        if (TargetPin == nullptr)
        {
            TargetPin = FindNextValueInputPin(Node, PositionalIndex);
        }
        if (TargetPin == nullptr)
        {
            continue;
        }

        ConnectExpressionToPin(Context, GetArgumentExpression(Argument), TargetPin, Depth + 1);
    }

    return true;
}

static UK2Node_CallFunction* SpawnUnresolvedCallPlaceholder(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    const FString& FunctionName,
    int32 X,
    int32 Y)
{
    UK2Node_CallFunction* Node = SpawnCallFunctionNode(Context, FindKismetSystemFunction(TEXT("PrintString")), FString::Printf(TEXT("unresolved call %s"), *FunctionName), X, Y);
    if (Node != nullptr && Context.K2Schema != nullptr)
    {
        if (UEdGraphPin* StringPin = FindPinByName(Node, TEXT("InString")))
        {
            Context.K2Schema->TrySetDefaultValue(*StringPin, FString::Printf(TEXT("KMS unresolved call: %s"), *ExpressionToText(Expression)), false);
        }
    }
    return Node;
}

static FMulticastDelegateProperty* FindBlueprintDelegateProperty(UBlueprint* Blueprint, const FString& DelegateName)
{
    if (Blueprint == nullptr || DelegateName.IsEmpty())
    {
        return nullptr;
    }

    const FName PropertyName(*DelegateName);
    if (Blueprint->SkeletonGeneratedClass != nullptr)
    {
        if (FMulticastDelegateProperty* Property = FindFProperty<FMulticastDelegateProperty>(Blueprint->SkeletonGeneratedClass, PropertyName))
        {
            return Property;
        }
    }
    if (Blueprint->GeneratedClass != nullptr)
    {
        if (FMulticastDelegateProperty* Property = FindFProperty<FMulticastDelegateProperty>(Blueprint->GeneratedClass, PropertyName))
        {
            return Property;
        }
    }
    return nullptr;
}

static bool IsDelegateBroadcastExpression(
    const FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    FString& OutDelegateName,
    TSharedPtr<FJsonObject>& OutCallExpression)
{
    if (!Expression.IsValid() || !GetStringField(Expression, TEXT("kind")).Equals(TEXT("member"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    const TSharedPtr<FJsonObject> TargetExpression = GetObjectField(Expression, TEXT("context"));
    const TSharedPtr<FJsonObject> CallExpression = GetObjectField(Expression, TEXT("member"));
    if (!TargetExpression.IsValid()
        || !CallExpression.IsValid()
        || !GetStringField(TargetExpression, TEXT("kind")).Equals(TEXT("identifier"), ESearchCase::IgnoreCase)
        || !GetStringField(CallExpression, TEXT("kind")).Equals(TEXT("call"), ESearchCase::IgnoreCase)
        || !GetStringField(CallExpression, TEXT("name")).Equals(TEXT("Broadcast"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    const FString DelegateName = ResolveGeneratedVariableName(Context, GetStringField(TargetExpression, TEXT("name")));
    if (DelegateName.IsEmpty() || (!Context.DelegateVariables.Contains(DelegateName) && FindBlueprintDelegateProperty(Context.Blueprint, DelegateName) == nullptr))
    {
        return false;
    }

    OutDelegateName = DelegateName;
    OutCallExpression = CallExpression;
    return true;
}

static UK2Node_CallDelegate* SpawnDelegateBroadcastNode(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    int32 X,
    int32 Y,
    int32 Depth)
{
    FString DelegateName;
    TSharedPtr<FJsonObject> CallExpression;
    if (Context.Graph == nullptr || !IsDelegateBroadcastExpression(Context, Expression, DelegateName, CallExpression))
    {
        return nullptr;
    }

    UK2Node_CallDelegate* Node = NewObject<UK2Node_CallDelegate>(Context.Graph);
    if (FMulticastDelegateProperty* DelegateProperty = FindBlueprintDelegateProperty(Context.Blueprint, DelegateName))
    {
        Node->SetFromProperty(DelegateProperty, true, DelegateProperty->GetOwnerClass());
    }
    else
    {
        Node->DelegateReference.SetSelfMember(FName(*DelegateName), FBlueprintEditorUtils::FindMemberVariableGuidByName(Context.Blueprint, FName(*DelegateName)));
    }

    AddGeneratedNodeToGraph(Context.Graph, Node, FString::Printf(TEXT("broadcast %s"), *DelegateName), X, Y);
    Node->ReconstructNode();
    ConnectCallArguments(Context, Node, CallExpression, Depth);
    return Node;
}

template <typename TDelegateNode>
static TDelegateNode* SpawnDelegateNode(
    FKmsBpNativeGenContext& Context,
    const FString& DelegateName,
    const FString& Kind,
    int32 X,
    int32 Y)
{
    if (Context.Graph == nullptr || DelegateName.IsEmpty())
    {
        return nullptr;
    }

    TDelegateNode* Node = NewObject<TDelegateNode>(Context.Graph);
    if (FMulticastDelegateProperty* DelegateProperty = FindBlueprintDelegateProperty(Context.Blueprint, DelegateName))
    {
        Node->SetFromProperty(DelegateProperty, true, DelegateProperty->GetOwnerClass());
    }
    else
    {
        Node->DelegateReference.SetSelfMember(FName(*DelegateName), FBlueprintEditorUtils::FindMemberVariableGuidByName(Context.Blueprint, FName(*DelegateName)));
    }

    AddGeneratedNodeToGraph(Context.Graph, Node, Kind, X, Y);
    Node->ReconstructNode();
    return Node;
}

static bool GenerateDelegateBindStatement(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    TArray<UEdGraphPin*>& OutExecTails,
    int32 Depth,
    int32& NextY)
{
    const FString Kind = GetStringField(Statement, TEXT("kind"));
    const bool bBind = Kind.Equals(TEXT("bind"), ESearchCase::IgnoreCase);
    if (!bBind && !Kind.Equals(TEXT("unbind"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    const TSharedPtr<FJsonObject> TargetExpression = GetObjectField(Statement, TEXT("target"));
    const TSharedPtr<FJsonObject> HandlerExpression = GetObjectField(Statement, TEXT("handler"));
    if (!TargetExpression.IsValid()
        || !HandlerExpression.IsValid()
        || !GetStringField(TargetExpression, TEXT("kind")).Equals(TEXT("identifier"), ESearchCase::IgnoreCase)
        || !GetStringField(HandlerExpression, TEXT("kind")).Equals(TEXT("identifier"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    const FString DelegateName = ResolveGeneratedVariableName(Context, GetStringField(TargetExpression, TEXT("name")));
    const FString HandlerName = GetStringField(HandlerExpression, TEXT("name"));
    if (DelegateName.IsEmpty()
        || HandlerName.IsEmpty()
        || (!Context.DelegateVariables.Contains(DelegateName) && FindBlueprintDelegateProperty(Context.Blueprint, DelegateName) == nullptr))
    {
        return false;
    }

    UK2Node* DelegateNode = bBind
        ? static_cast<UK2Node*>(SpawnDelegateNode<UK2Node_AddDelegate>(Context, DelegateName, FString::Printf(TEXT("bind %s"), *DelegateName), 260 + Depth * 240, NextY))
        : static_cast<UK2Node*>(SpawnDelegateNode<UK2Node_RemoveDelegate>(Context, DelegateName, FString::Printf(TEXT("unbind %s"), *DelegateName), 260 + Depth * 240, NextY));
    if (DelegateNode == nullptr)
    {
        return false;
    }

    UK2Node_CreateDelegate* CreateDelegateNode = NewObject<UK2Node_CreateDelegate>(Context.Graph);
    AddGeneratedNodeToGraph(Context.Graph, CreateDelegateNode, FString::Printf(TEXT("handler %s"), *HandlerName), 620 + Depth * 240, NextY);
    CreateDelegateNode->SetFunction(FName(*HandlerName));

    UEdGraphPin* DelegatePin = nullptr;
    if (UK2Node_BaseMCDelegate* BaseDelegateNode = Cast<UK2Node_BaseMCDelegate>(DelegateNode))
    {
        DelegatePin = BaseDelegateNode->GetDelegatePin();
    }
    TryConnectPins(Context, CreateDelegateNode->GetDelegateOutPin(), DelegatePin);
    CreateDelegateNode->HandleAnyChangeWithoutNotifying();

    OutExecTails = ConnectExecTails(Context, ExecTails, DelegateNode);
    NextY += 180;
    return true;
}

static UK2Node_CallFunction* SpawnResolvedCallNode(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    int32 X,
    int32 Y,
    int32 Depth)
{
    TSharedPtr<FJsonObject> TargetExpression;
    bool bSelfContext = true;
    const TSharedPtr<FJsonObject> CallExpression = GetStringField(Expression, TEXT("kind")).Equals(TEXT("member"), ESearchCase::IgnoreCase)
        ? GetObjectField(Expression, TEXT("member"))
        : Expression;
    const FString RequestedFunctionName = GetStringField(CallExpression, TEXT("name"));
    if (GetStringField(Expression, TEXT("kind")).Equals(TEXT("member"), ESearchCase::IgnoreCase)
        && ExpressionToText(GetObjectField(Expression, TEXT("context"))).Equals(TEXT("super"), ESearchCase::IgnoreCase))
    {
        return SpawnUnresolvedCallPlaceholder(Context, Expression, RequestedFunctionName, X, Y);
    }

    UFunction* Function = ResolveCallFunction(Context, Expression, TargetExpression, bSelfContext);
    if (Function == nullptr && !TargetExpression.IsValid())
    {
        // The source may reference project-specific methods that are outside the
        // imported closure. Keep the graph compilable with a real K2 call node.
        return SpawnUnresolvedCallPlaceholder(Context, Expression, RequestedFunctionName, X, Y);
    }

    if (Function == nullptr)
    {
        return SpawnUnresolvedCallPlaceholder(Context, Expression, RequestedFunctionName, X, Y);
    }

    UK2Node_CallFunction* Node = SpawnCallFunctionNode(Context, Function, FString::Printf(TEXT("call %s"), *Function->GetName()), X, Y);
    if (Node == nullptr)
    {
        return nullptr;
    }

    if (!bSelfContext)
    {
        Node->FunctionReference.SetFromField<UFunction>(Function, false, Function->GetOwnerClass());
        Node->ReconstructNode();
    }

    if (TargetExpression.IsValid())
    {
        ConnectExpressionToPin(Context, TargetExpression, FindSelfPin(Node), Depth + 1);
    }

    ConnectCallArguments(Context, Node, CallExpression, Depth);
    return Node;
}

static bool GenerateCallStatement(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Expression,
    const TArray<UEdGraphPin*>& ExecTails,
    TArray<UEdGraphPin*>& OutExecTails,
    int32 Depth,
    int32& NextY)
{
    if (!Expression.IsValid())
    {
        return false;
    }

    const FString Kind = GetStringField(Expression, TEXT("kind"));
    if (!Kind.Equals(TEXT("call"), ESearchCase::IgnoreCase) && !Kind.Equals(TEXT("member"), ESearchCase::IgnoreCase))
    {
        return false;
    }

    UK2Node* Node = SpawnDelegateBroadcastNode(Context, Expression, 260 + Depth * 240, NextY, Depth);
    if (Node == nullptr)
    {
        Node = SpawnResolvedCallNode(Context, Expression, 260 + Depth * 240, NextY, Depth);
    }
    if (Node == nullptr)
    {
        return false;
    }

    OutExecTails = ConnectExecTails(Context, ExecTails, Node);
    NextY += 180;
    return true;
}

static bool ShouldGenerateNativeVariableInitializer(const TSharedPtr<FJsonObject>& Initializer)
{
    if (!Initializer.IsValid())
    {
        return true;
    }

    const FString Kind = GetStringField(Initializer, TEXT("kind"));
    return !Kind.Equals(TEXT("object"), ESearchCase::IgnoreCase)
        && !Kind.Equals(TEXT("typeof"), ESearchCase::IgnoreCase);
}

static bool IsAssetCallExpression(const TSharedPtr<FJsonObject>& Expression)
{
    return Expression.IsValid()
        && GetStringField(Expression, TEXT("kind")).Equals(TEXT("call"), ESearchCase::IgnoreCase)
        && GetStringField(Expression, TEXT("name")).Equals(TEXT("asset"), ESearchCase::IgnoreCase);
}

static void CollectLocalVariableTypes(const TSharedPtr<FJsonObject>& Statement, TMap<FString, FString>& OutVariables)
{
    if (!Statement.IsValid())
    {
        return;
    }

    if (GetStringField(Statement, TEXT("kind")).Equals(TEXT("var"), ESearchCase::IgnoreCase))
    {
        OutVariables.FindOrAdd(GetStringField(Statement, TEXT("name"))) = GetStringField(Statement, TEXT("type"));
    }

    const TArray<TSharedPtr<FJsonValue>>* Statements = nullptr;
    if (GetArrayField(Statement, TEXT("statements"), Statements))
    {
        for (const TSharedPtr<FJsonValue>& Child : *Statements)
        {
            CollectLocalVariableTypes(Child.IsValid() ? Child->AsObject() : nullptr, OutVariables);
        }
    }

    CollectLocalVariableTypes(GetObjectField(Statement, TEXT("then")), OutVariables);
    CollectLocalVariableTypes(GetObjectField(Statement, TEXT("else")), OutVariables);
    CollectLocalVariableTypes(GetObjectField(Statement, TEXT("body")), OutVariables);
    CollectLocalVariableTypes(GetObjectField(Statement, TEXT("initializerStatement")), OutVariables);

    const TArray<TSharedPtr<FJsonValue>>* Cases = nullptr;
    if (GetArrayField(Statement, TEXT("cases"), Cases))
    {
        for (const TSharedPtr<FJsonValue>& CaseValue : *Cases)
        {
            CollectLocalVariableTypes(GetObjectField(CaseValue.IsValid() ? CaseValue->AsObject() : nullptr, TEXT("body")), OutVariables);
        }
    }
}

static void SyncLocalVariables(FKmsBpNativeGenContext& Context, const TMap<FString, FString>& LocalTypes, FKmsBpImportResult& Result)
{
    for (const TPair<FString, FString>& Local : LocalTypes)
    {
        Context.LocalVariables.Add(Local.Key);
        Context.VariableTypes.FindOrAdd(Local.Key) = Local.Value;
        if (Context.Blueprint == nullptr || Context.Graph == nullptr || FBlueprintEditorUtils::FindLocalVariable(Context.Blueprint, Context.Graph, FName(*Local.Key)) != nullptr)
        {
            continue;
        }

        FEdGraphPinType PinType;
        ResolvePinType(Local.Value, PinType, Result, false);
        FBlueprintEditorUtils::AddLocalVariable(Context.Blueprint, Context.Graph, FName(*Local.Key), PinType);
    }
}

static TArray<UEdGraphPin*> GenerateStatementNodes(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    int32 Depth,
    int32& NextY);

static TArray<UEdGraphPin*> GenerateBlockStatements(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    int32 Depth,
    int32& NextY)
{
    TArray<UEdGraphPin*> CurrentTails = ExecTails;
    const TArray<TSharedPtr<FJsonValue>>* Statements = nullptr;
    if (GetArrayField(Statement, TEXT("statements"), Statements))
    {
        for (const TSharedPtr<FJsonValue>& Child : *Statements)
        {
            CurrentTails = GenerateStatementNodes(Context, Child.IsValid() ? Child->AsObject() : nullptr, CurrentTails, Depth, NextY);
        }
    }
    return CurrentTails;
}

static TArray<UEdGraphPin*> GenerateIfStatement(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    int32 Depth,
    int32& NextY)
{
    UK2Node_IfThenElse* BranchNode = NewObject<UK2Node_IfThenElse>(Context.Graph);
    AddGeneratedNodeToGraph(Context.Graph, BranchNode, TEXT("if"), 260 + Depth * 240, NextY);
    for (UEdGraphPin* ExecTail : ExecTails)
    {
        TryConnectPins(Context, ExecTail, BranchNode->GetExecPin());
    }
    ConnectExpressionToPin(Context, GetObjectField(Statement, TEXT("condition")), BranchNode->GetConditionPin(), Depth + 1);

    int32 ThenY = NextY + 180;
    TArray<UEdGraphPin*> ThenExits = GenerateStatementNodes(Context, GetObjectField(Statement, TEXT("then")), MakeExecTails(BranchNode->GetThenPin()), Depth + 1, ThenY);

    int32 ElseY = ThenY + 80;
    TArray<UEdGraphPin*> ElseExits = GenerateStatementNodes(Context, GetObjectField(Statement, TEXT("else")), MakeExecTails(BranchNode->GetElsePin()), Depth + 1, ElseY);

    ThenExits.Append(ElseExits);
    NextY = FMath::Max(ThenY, ElseY) + 40;
    return ThenExits;
}

static bool GenerateWhileStatement(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    TArray<UEdGraphPin*>& OutExecTails,
    int32 Depth,
    int32& NextY)
{
    UK2Node_MacroInstance* WhileNode = SpawnStandardMacroNode(Context, TEXT("WhileLoop"), TEXT("while"), 260 + Depth * 240, NextY);
    if (WhileNode == nullptr)
    {
        return false;
    }

    UEdGraphPin* ExecutePin = FindExecPinByName(WhileNode, UEdGraphSchema_K2::PN_Execute, EGPD_Input);
    if (ExecutePin == nullptr)
    {
        ExecutePin = WhileNode->GetExecPin();
    }
    for (UEdGraphPin* ExecTail : ExecTails)
    {
        TryConnectPins(Context, ExecTail, ExecutePin);
    }

    ConnectExpressionToPin(Context, GetObjectField(Statement, TEXT("condition")), FindPinByName(WhileNode, TEXT("Condition")), Depth + 1);

    UEdGraphPin* BodyEntry = FindExecPinByDisplayText(WhileNode, TEXT("Loop Body"), EGPD_Output);
    if (BodyEntry == nullptr)
    {
        BodyEntry = FindExecPinByDisplayText(WhileNode, TEXT("Loop"), EGPD_Output);
    }
    int32 BodyY = NextY + 180;
    GenerateStatementNodes(Context, GetObjectField(Statement, TEXT("body")), MakeExecTails(BodyEntry), Depth + 1, BodyY);

    UEdGraphPin* CompletedPin = FindExecPinByDisplayText(WhileNode, TEXT("Completed"), EGPD_Output);
    if (CompletedPin == nullptr)
    {
        CompletedPin = FindExecPinByDisplayText(WhileNode, TEXT("Complete"), EGPD_Output);
    }
    OutExecTails = MakeExecTails(CompletedPin != nullptr ? CompletedPin : BodyEntry);
    NextY = BodyY + 40;
    return true;
}

static bool GenerateForStatement(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    TArray<UEdGraphPin*>& OutExecTails,
    int32 Depth,
    int32& NextY)
{
    int32 InitY = NextY;
    TArray<UEdGraphPin*> InitTails = GenerateStatementNodes(Context, GetObjectField(Statement, TEXT("initializerStatement")), ExecTails, Depth, InitY);

    UK2Node_MacroInstance* WhileNode = SpawnStandardMacroNode(Context, TEXT("WhileLoop"), TEXT("for"), 260 + Depth * 240, InitY);
    if (WhileNode == nullptr)
    {
        return false;
    }

    UEdGraphPin* ExecutePin = FindExecPinByName(WhileNode, UEdGraphSchema_K2::PN_Execute, EGPD_Input);
    if (ExecutePin == nullptr)
    {
        ExecutePin = WhileNode->GetExecPin();
    }
    for (UEdGraphPin* ExecTail : InitTails)
    {
        TryConnectPins(Context, ExecTail, ExecutePin);
    }

    ConnectExpressionToPin(Context, GetObjectField(Statement, TEXT("condition")), FindPinByName(WhileNode, TEXT("Condition")), Depth + 1);

    UEdGraphPin* BodyEntry = FindExecPinByDisplayText(WhileNode, TEXT("Loop Body"), EGPD_Output);
    if (BodyEntry == nullptr)
    {
        BodyEntry = FindExecPinByDisplayText(WhileNode, TEXT("Loop"), EGPD_Output);
    }

    int32 BodyY = InitY + 180;
    TArray<UEdGraphPin*> BodyTails = GenerateStatementNodes(Context, GetObjectField(Statement, TEXT("body")), MakeExecTails(BodyEntry), Depth + 1, BodyY);

    TSharedPtr<FJsonObject> AfterExpression = GetObjectField(Statement, TEXT("after"));
    if (AfterExpression.IsValid())
    {
        TSharedPtr<FJsonObject> AfterStatement = MakeShared<FJsonObject>();
        AfterStatement->SetStringField(TEXT("kind"), TEXT("expression"));
        AfterStatement->SetObjectField(TEXT("expression"), AfterExpression);
        TArray<UEdGraphPin*> AfterTails = GenerateStatementNodes(Context, AfterStatement, BodyTails, Depth + 1, BodyY);
        for (UEdGraphPin* AfterTail : AfterTails)
        {
            TryConnectPins(Context, AfterTail, ExecutePin);
        }
    }

    UEdGraphPin* CompletedPin = FindExecPinByDisplayText(WhileNode, TEXT("Completed"), EGPD_Output);
    if (CompletedPin == nullptr)
    {
        CompletedPin = FindExecPinByDisplayText(WhileNode, TEXT("Complete"), EGPD_Output);
    }
    OutExecTails = MakeExecTails(CompletedPin != nullptr ? CompletedPin : BodyEntry);
    NextY = BodyY + 40;
    return true;
}

static bool GenerateForeachStatement(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    TArray<UEdGraphPin*>& OutExecTails,
    int32 Depth,
    int32& NextY)
{
    UK2Node_MacroInstance* ForEachNode = SpawnStandardMacroNode(Context, TEXT("ForEachLoop"), TEXT("foreach"), 260 + Depth * 240, NextY);
    if (ForEachNode == nullptr)
    {
        return false;
    }

    UEdGraphPin* ExecutePin = FindExecPinByName(ForEachNode, UEdGraphSchema_K2::PN_Execute, EGPD_Input);
    if (ExecutePin == nullptr)
    {
        ExecutePin = ForEachNode->GetExecPin();
    }
    for (UEdGraphPin* ExecTail : ExecTails)
    {
        TryConnectPins(Context, ExecTail, ExecutePin);
    }

    ConnectExpressionToPin(Context, GetObjectField(Statement, TEXT("collection")), FindPinByName(ForEachNode, TEXT("Array")), Depth + 1);

    const FString ItemName = GetStringField(Statement, TEXT("name"));
    if (!ItemName.IsEmpty())
    {
        Context.LocalVariables.Add(ItemName);
        Context.VariableTypes.FindOrAdd(ItemName) = GetStringField(Statement, TEXT("type"));
        if (FBlueprintEditorUtils::FindLocalVariable(Context.Blueprint, Context.Graph, FName(*ItemName)) == nullptr)
        {
            FEdGraphPinType PinType;
            ResolvePinType(GetStringField(Statement, TEXT("type")), PinType, *Context.ImportResult, false);
            FBlueprintEditorUtils::AddLocalVariable(Context.Blueprint, Context.Graph, FName(*ItemName), PinType);
        }

        UK2Node_VariableSet* SetItemNode = SpawnVariableSetNode(Context, ItemName, 520 + Depth * 240, NextY + 40);
        TryConnectPins(Context, FindExecPinByDisplayText(ForEachNode, TEXT("Loop Body"), EGPD_Output), SetItemNode != nullptr ? SetItemNode->GetExecPin() : nullptr);
        TryConnectPins(Context, FindPinByName(ForEachNode, TEXT("Array Element")), SetItemNode != nullptr ? GetVariableSetValuePin(SetItemNode) : nullptr);

        int32 BodyY = NextY + 220;
        GenerateStatementNodes(Context, GetObjectField(Statement, TEXT("body")), ConnectExecTails(Context, MakeExecTails(FindExecPinByDisplayText(ForEachNode, TEXT("Loop Body"), EGPD_Output)), SetItemNode), Depth + 1, BodyY);
        NextY = BodyY + 40;
    }
    else
    {
        int32 BodyY = NextY + 180;
        GenerateStatementNodes(Context, GetObjectField(Statement, TEXT("body")), MakeExecTails(FindExecPinByDisplayText(ForEachNode, TEXT("Loop Body"), EGPD_Output)), Depth + 1, BodyY);
        NextY = BodyY + 40;
    }

    OutExecTails = MakeExecTails(FindExecPinByDisplayText(ForEachNode, TEXT("Completed"), EGPD_Output));
    return true;
}

static TArray<UEdGraphPin*> GenerateSwitchAsBranches(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    int32 Depth,
    int32& NextY)
{
    const TArray<TSharedPtr<FJsonValue>>* Cases = nullptr;
    if (!GetArrayField(Statement, TEXT("cases"), Cases) || Cases->Num() == 0)
    {
        return ExecTails;
    }

    TArray<UEdGraphPin*> CurrentExecTails = ExecTails;
    TArray<UEdGraphPin*> ExitTails;
    const TSharedPtr<FJsonObject> SwitchOn = GetObjectField(Statement, TEXT("switchOn"));

    for (const TSharedPtr<FJsonValue>& CaseValue : *Cases)
    {
        const TSharedPtr<FJsonObject> CaseObject = CaseValue.IsValid() ? CaseValue->AsObject() : nullptr;
        if (!CaseObject.IsValid())
        {
            continue;
        }

        if (GetBoolField(CaseObject, TEXT("isDefault")))
        {
            ExitTails.Append(GenerateStatementNodes(Context, GetObjectField(CaseObject, TEXT("body")), CurrentExecTails, Depth + 1, NextY));
            CurrentExecTails.Reset();
            continue;
        }

        UK2Node_IfThenElse* BranchNode = NewObject<UK2Node_IfThenElse>(Context.Graph);
        AddGeneratedNodeToGraph(Context.Graph, BranchNode, TEXT("switch case"), 260 + Depth * 240, NextY);
        for (UEdGraphPin* ExecTail : CurrentExecTails)
        {
            TryConnectPins(Context, ExecTail, BranchNode->GetExecPin());
        }

        TSharedPtr<FJsonObject> Equality = MakeShared<FJsonObject>();
        Equality->SetStringField(TEXT("kind"), TEXT("binary"));
        Equality->SetStringField(TEXT("op"), TEXT("=="));
        Equality->SetObjectField(TEXT("left"), SwitchOn);
        Equality->SetObjectField(TEXT("right"), GetObjectField(CaseObject, TEXT("condition")));
        ConnectExpressionToPin(Context, Equality, BranchNode->GetConditionPin(), Depth + 1);

        int32 CaseY = NextY + 180;
        ExitTails.Append(GenerateStatementNodes(Context, GetObjectField(CaseObject, TEXT("body")), MakeExecTails(BranchNode->GetThenPin()), Depth + 1, CaseY));
        CurrentExecTails = MakeExecTails(BranchNode->GetElsePin());
        NextY = CaseY + 40;
    }

    ExitTails.Append(CurrentExecTails);
    return ExitTails;
}

static TArray<UEdGraphPin*> GenerateReturnStatement(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    int32 Depth,
    int32& NextY)
{
    UK2Node_FunctionResult* ResultNode = FindFunctionResultNode(Context.Graph);
    if (ResultNode == nullptr)
    {
        UK2Node_KmsGeneratedNode* Node = SpawnGeneratedKmsNode(Context.Graph, ExecTails.Num() > 0, TEXT("return"), StatementToText(Statement), 260 + Depth * 240, NextY);
        NextY += 150;
        return ConnectExecTailsToGeneratedNode(Context, ExecTails, Node);
    }

    ResultNode->NodePosX = 260 + Depth * 240;
    ResultNode->NodePosY = NextY;
    for (UEdGraphPin* ExecTail : ExecTails)
    {
        TryConnectPins(Context, ExecTail, ResultNode->GetExecPin());
    }
    ConnectExpressionToPin(Context, GetObjectField(Statement, TEXT("value")), ResultNode->FindPin(UEdGraphSchema_K2::PN_ReturnValue), Depth + 1);
    NextY += 180;
    return TArray<UEdGraphPin*>();
}

static TArray<UEdGraphPin*> GenerateStatementNodes(
    FKmsBpNativeGenContext& Context,
    const TSharedPtr<FJsonObject>& Statement,
    const TArray<UEdGraphPin*>& ExecTails,
    int32 Depth,
    int32& NextY)
{
    if (!Statement.IsValid())
    {
        return ExecTails;
    }

    const FString Kind = GetStringField(Statement, TEXT("kind"));
    if (Kind.Equals(TEXT("block"), ESearchCase::IgnoreCase))
    {
        return GenerateBlockStatements(Context, Statement, ExecTails, Depth, NextY);
    }
    if (Kind.Equals(TEXT("var"), ESearchCase::IgnoreCase))
    {
        const TSharedPtr<FJsonObject> Initializer = GetObjectField(Statement, TEXT("initializer"));
        if (!ShouldGenerateNativeVariableInitializer(Initializer))
        {
            SpawnGeneratedKmsNode(Context.Graph, false, TEXT("var"), StatementToText(Statement), 260 + Depth * 240, NextY);
            MaterializeExpressionNodes(Context.Graph, Initializer, Depth + 1, Context.NextExpressionY);
            NextY += 150;
            return ExecTails;
        }

        UK2Node_VariableSet* SetNode = SpawnVariableSetNode(Context, GetStringField(Statement, TEXT("name")), 260 + Depth * 240, NextY);
        TArray<UEdGraphPin*> OutExecTails = ConnectExecTails(Context, ExecTails, SetNode);
        if (!ConnectExpressionToPin(Context, Initializer, GetVariableSetValuePin(SetNode), Depth + 1) && !IsAssetCallExpression(Initializer))
        {
            MaterializeExpressionNodes(Context.Graph, Initializer, Depth + 1, Context.NextExpressionY);
        }
        NextY += 180;
        return OutExecTails;
    }
    if (Kind.Equals(TEXT("expression"), ESearchCase::IgnoreCase))
    {
        const TSharedPtr<FJsonObject> Expression = GetObjectField(Statement, TEXT("expression"));
        TArray<UEdGraphPin*> OutExecTails;
        if (GenerateAssignmentStatement(Context, Expression, ExecTails, OutExecTails, Depth, NextY)
            || GeneratePrintStatement(Context, Expression, ExecTails, OutExecTails, Depth, NextY)
            || GenerateCallStatement(Context, Expression, ExecTails, OutExecTails, Depth, NextY))
        {
            return OutExecTails;
        }
    }
    if (Kind.Equals(TEXT("if"), ESearchCase::IgnoreCase))
    {
        return GenerateIfStatement(Context, Statement, ExecTails, Depth, NextY);
    }
    TArray<UEdGraphPin*> OutExecTails;
    if (Kind.Equals(TEXT("while"), ESearchCase::IgnoreCase) && GenerateWhileStatement(Context, Statement, ExecTails, OutExecTails, Depth, NextY))
    {
        return OutExecTails;
    }
    if (Kind.Equals(TEXT("for"), ESearchCase::IgnoreCase) && GenerateForStatement(Context, Statement, ExecTails, OutExecTails, Depth, NextY))
    {
        return OutExecTails;
    }
    if (Kind.Equals(TEXT("foreach"), ESearchCase::IgnoreCase) && GenerateForeachStatement(Context, Statement, ExecTails, OutExecTails, Depth, NextY))
    {
        return OutExecTails;
    }
    if (Kind.Equals(TEXT("switch"), ESearchCase::IgnoreCase))
    {
        return GenerateSwitchAsBranches(Context, Statement, ExecTails, Depth, NextY);
    }
    if (Kind.Equals(TEXT("break"), ESearchCase::IgnoreCase) || Kind.Equals(TEXT("continue"), ESearchCase::IgnoreCase))
    {
        return TArray<UEdGraphPin*>();
    }
    if ((Kind.Equals(TEXT("bind"), ESearchCase::IgnoreCase) || Kind.Equals(TEXT("unbind"), ESearchCase::IgnoreCase))
        && GenerateDelegateBindStatement(Context, Statement, ExecTails, OutExecTails, Depth, NextY))
    {
        return OutExecTails;
    }
    if (Kind.Equals(TEXT("return"), ESearchCase::IgnoreCase))
    {
        return GenerateReturnStatement(Context, Statement, ExecTails, Depth, NextY);
    }

    SpawnGeneratedKmsNode(Context.Graph, false, Kind.IsEmpty() ? TEXT("statement") : Kind, StatementToText(Statement), 260 + Depth * 240, NextY);
    static const TCHAR* FallbackExpressionFields[] =
    {
        TEXT("condition"),
        TEXT("after"),
        TEXT("collection"),
        TEXT("target"),
        TEXT("handler"),
        TEXT("switchOn")
    };
    for (const TCHAR* FieldName : FallbackExpressionFields)
    {
        MaterializeExpressionNodes(Context.Graph, GetObjectField(Statement, FieldName), Depth + 1, Context.NextExpressionY);
    }

    UEdGraphPin* FallbackTail = nullptr;
    MaterializeStatementNodes(Context.Graph, GetObjectField(Statement, TEXT("initializerStatement")), false, FallbackTail, Depth + 1, NextY);
    MaterializeStatementNodes(Context.Graph, GetObjectField(Statement, TEXT("body")), false, FallbackTail, Depth + 1, NextY);
    const TArray<TSharedPtr<FJsonValue>>* Cases = nullptr;
    if (GetArrayField(Statement, TEXT("cases"), Cases))
    {
        for (const TSharedPtr<FJsonValue>& CaseValue : *Cases)
        {
            const TSharedPtr<FJsonObject> CaseObject = CaseValue.IsValid() ? CaseValue->AsObject() : nullptr;
            MaterializeExpressionNodes(Context.Graph, GetObjectField(CaseObject, TEXT("condition")), Depth + 1, Context.NextExpressionY);
            MaterializeStatementNodes(Context.Graph, GetObjectField(CaseObject, TEXT("body")), false, FallbackTail, Depth + 1, NextY);
        }
    }
    NextY += 150;
    return ExecTails;
}

static void MaterializeProcedureNodes(UBlueprint* Blueprint, UEdGraph* Graph, const FKmsBpJsonProcedure& Procedure, const bool bPure, FKmsBpImportResult& Result)
{
    if (Blueprint == nullptr || Graph == nullptr || !Procedure.Body.IsValid())
    {
        return;
    }

    Graph->Modify();
    ClearGeneratedKmsNodes(Graph);

    TArray<UEdGraphPin*> ExecTails;
    if (!bPure)
    {
        if (UK2Node_FunctionEntry* EntryNode = FindFunctionEntryNode(Graph))
        {
            ExecTails = MakeExecTails(EntryNode->FindPin(UEdGraphSchema_K2::PN_Then));
        }
    }

    int32 NextY = 380;
    FKmsBpNativeGenContext Context;
    Context.Blueprint = Blueprint;
    Context.Graph = Graph;
    Context.K2Schema = Cast<const UEdGraphSchema_K2>(Graph->GetSchema());
    Context.ImportResult = &Result;

    for (const FBPVariableDescription& Variable : Blueprint->NewVariables)
    {
        Context.VariableTypes.FindOrAdd(Variable.VarName.ToString()) = Variable.VarType.PinCategory.ToString();
        if (Variable.VarType.PinCategory == UEdGraphSchema_K2::PC_MCDelegate)
        {
            Context.DelegateVariables.Add(Variable.VarName.ToString());
        }
        if (Variable.VarType.PinCategory == UEdGraphSchema_K2::PC_Boolean)
        {
            Context.VariableTypes[Variable.VarName.ToString()] = TEXT("bool");
        }
        else if (Variable.VarType.PinCategory == UEdGraphSchema_K2::PC_Int)
        {
            Context.VariableTypes[Variable.VarName.ToString()] = TEXT("int");
        }
        else if (Variable.VarType.PinCategory == UEdGraphSchema_K2::PC_Real)
        {
            Context.VariableTypes[Variable.VarName.ToString()] = TEXT("float");
        }
        else if (Variable.VarType.PinCategory == UEdGraphSchema_K2::PC_String)
        {
            Context.VariableTypes[Variable.VarName.ToString()] = TEXT("string");
        }
    }

    if (Blueprint->SimpleConstructionScript != nullptr)
    {
        for (USCS_Node* Node : Blueprint->SimpleConstructionScript->GetAllNodes())
        {
            if (Node != nullptr && Node->ComponentClass != nullptr)
            {
                Context.VariableTypes.FindOrAdd(Node->GetVariableName().ToString()) = Node->ComponentClass->GetName();
            }
        }
    }

    for (const FKmsBpJsonParameter& Parameter : Procedure.Parameters)
    {
        Context.VariableTypes.FindOrAdd(Parameter.Name) = Parameter.Type;
        if (!Parameter.Modifier.Equals(TEXT("out"), ESearchCase::IgnoreCase))
        {
            Context.LocalVariables.Add(Parameter.Name);
        }
    }

    TMap<FString, FString> LocalTypes;
    CollectLocalVariableTypes(Procedure.Body, LocalTypes);
    for (const FKmsBpJsonParameter& Parameter : Procedure.Parameters)
    {
        if (Parameter.Modifier.Equals(TEXT("out"), ESearchCase::IgnoreCase))
        {
            const FString AliasName = Parameter.Name + TEXT("_Out");
            Context.VariableAliases.FindOrAdd(Parameter.Name) = AliasName;
            Context.VariableTypes.FindOrAdd(AliasName) = Parameter.Type;
            LocalTypes.FindOrAdd(AliasName) = Parameter.Type;
        }
    }
    FKmsBpImportResult DummyResult;
    SyncLocalVariables(Context, LocalTypes, DummyResult);

    ExecTails = GenerateStatementNodes(Context, Procedure.Body, ExecTails, 0, NextY);

    if (!bPure)
    {
        if (UK2Node_FunctionResult* ResultNode = FindFunctionResultNode(Graph))
        {
            bool bHasOutPins = false;
            for (const FKmsBpJsonParameter& Parameter : Procedure.Parameters)
            {
                if (!Parameter.Modifier.Equals(TEXT("out"), ESearchCase::IgnoreCase))
                {
                    continue;
                }

                bHasOutPins = true;
                UK2Node_VariableGet* GetNode = SpawnVariableGetNode(Context, Parameter.Name, 620, Context.NextExpressionY);
                Context.NextExpressionY += 120;
                TryConnectPins(Context, GetNode != nullptr ? GetNode->GetValuePin() : nullptr, ResultNode->FindPin(FName(*Parameter.Name)));
            }

            if (bHasOutPins)
            {
                ResultNode->NodePosX = 260;
                ResultNode->NodePosY = NextY;
                for (UEdGraphPin* ExecTail : ExecTails)
                {
                    TryConnectPins(Context, ExecTail, ResultNode->GetExecPin());
                }
            }
        }
    }
    FBlueprintEditorUtils::MarkBlueprintAsModified(Blueprint);
}

struct FKmsGraphFormatNode
{
    UEdGraphNode* Node = nullptr;
    int32 X = 0;
    int32 Y = 0;
    int32 Width = 260;
    int32 Height = 120;
};

static bool HasExecPin(const UEdGraphNode* Node, EEdGraphPinDirection Direction)
{
    if (Node == nullptr)
    {
        return false;
    }

    for (const UEdGraphPin* Pin : Node->Pins)
    {
        if (Pin != nullptr && Pin->Direction == Direction && Pin->PinType.PinCategory == UEdGraphSchema_K2::PC_Exec)
        {
            return true;
        }
    }
    return false;
}

static bool IsExecNodeForFormat(const UEdGraphNode* Node)
{
    return Node != nullptr && (HasExecPin(Node, EGPD_Input) || HasExecPin(Node, EGPD_Output));
}

static bool IsGeneratedNodeForFormat(const UEdGraphNode* Node)
{
    return IsGeneratedKmsNode(Node)
        || (Node != nullptr
            && (Node->IsA<UK2Node_Event>()
                || Node->IsA<UK2Node_FunctionEntry>()
                || Node->IsA<UK2Node_FunctionResult>()
                || Node->IsA<UK2Node_IfThenElse>()
                || Node->IsA<UK2Node_MacroInstance>()
                || Node->IsA<UK2Node_VariableGet>()
                || Node->IsA<UK2Node_VariableSet>()
                || Node->IsA<UK2Node_CallFunction>()
                || Node->IsA<UK2Node_CallArrayFunction>()
                || Node->IsA<UK2Node_MakeArray>()));
}

static bool IsExecPinNamed(const UEdGraphPin* Pin, const TCHAR* Name)
{
    if (Pin == nullptr)
    {
        return false;
    }

    const FString PinName = Pin->PinName.ToString();
    const FString DisplayName = Pin->GetDisplayName().ToString();
    return PinName.Equals(Name, ESearchCase::IgnoreCase)
        || DisplayName.Equals(Name, ESearchCase::IgnoreCase)
        || PinName.Contains(Name, ESearchCase::IgnoreCase)
        || DisplayName.Contains(Name, ESearchCase::IgnoreCase);
}

static UEdGraphNode* FirstLinkedNode(const UEdGraphPin* Pin)
{
    if (Pin == nullptr)
    {
        return nullptr;
    }

    for (UEdGraphPin* LinkedPin : Pin->LinkedTo)
    {
        UEdGraphNode* LinkedNode = LinkedPin != nullptr ? LinkedPin->GetOwningNode() : nullptr;
        if (LinkedNode != nullptr)
        {
            return LinkedNode;
        }
    }
    return nullptr;
}

static int32 EstimateNodeWidth(const UEdGraphNode* Node)
{
    if (Node == nullptr)
    {
        return 260;
    }
    if (Node->IsA<UEdGraphNode_Comment>())
    {
        return FMath::Max(320, Node->NodeWidth);
    }
    return IsExecNodeForFormat(Node) ? 260 : 220;
}

static int32 EstimateNodeHeight(const UEdGraphNode* Node)
{
    if (Node == nullptr)
    {
        return 120;
    }
    if (Node->IsA<UEdGraphNode_Comment>())
    {
        return FMath::Max(120, Node->NodeHeight);
    }
    return IsExecNodeForFormat(Node) ? 120 : 90;
}

static void PlaceExecTree(
    UEdGraphNode* Node,
    int32 X,
    int32 Y,
    TMap<UEdGraphNode*, FKmsGraphFormatNode>& Placements,
    TSet<UEdGraphNode*>& Visiting)
{
    if (Node == nullptr || !IsGeneratedNodeForFormat(Node) || Visiting.Contains(Node))
    {
        return;
    }

    if (FKmsGraphFormatNode* Existing = Placements.Find(Node))
    {
        Existing->X = FMath::Min(Existing->X, X);
        Existing->Y = FMath::Min(Existing->Y, Y);
        return;
    }

    Visiting.Add(Node);
    FKmsGraphFormatNode Placement;
    Placement.Node = Node;
    Placement.X = X;
    Placement.Y = Y;
    Placement.Width = EstimateNodeWidth(Node);
    Placement.Height = EstimateNodeHeight(Node);
    Placements.Add(Node, Placement);

    constexpr int32 ExecSpacingX = 360;
    constexpr int32 BranchSpacingY = 220;

    if (Node->IsA<UK2Node_IfThenElse>())
    {
        UEdGraphNode* ThenNode = FirstLinkedNode(FindExecPinByDisplayText(Node, TEXT("then"), EGPD_Output));
        UEdGraphNode* ElseNode = FirstLinkedNode(FindExecPinByDisplayText(Node, TEXT("else"), EGPD_Output));
        PlaceExecTree(ThenNode, X + ExecSpacingX, Y - BranchSpacingY, Placements, Visiting);
        PlaceExecTree(ElseNode, X + ExecSpacingX, Y + BranchSpacingY, Placements, Visiting);
    }
    else
    {
        for (UEdGraphPin* Pin : Node->Pins)
        {
            if (Pin == nullptr
                || Pin->Direction != EGPD_Output
                || Pin->PinType.PinCategory != UEdGraphSchema_K2::PC_Exec
                || Pin->LinkedTo.Num() == 0)
            {
                continue;
            }

            const bool bLoopBody = IsExecPinNamed(Pin, TEXT("Loop Body")) || IsExecPinNamed(Pin, TEXT("Loop"));
            const bool bCompleted = IsExecPinNamed(Pin, TEXT("Completed")) || IsExecPinNamed(Pin, TEXT("Complete"));
            const int32 ChildX = X + ExecSpacingX;
            const int32 ChildY = bLoopBody ? Y + BranchSpacingY : Y + (bCompleted ? 0 : 40);
            PlaceExecTree(FirstLinkedNode(Pin), ChildX, ChildY, Placements, Visiting);
        }
    }

    Visiting.Remove(Node);
}

static void PlacePureInputsForNode(
    UEdGraphNode* Consumer,
    const TMap<UEdGraphNode*, FKmsGraphFormatNode>& ExecPlacements,
    TMap<UEdGraphNode*, FKmsGraphFormatNode>& Placements,
    TSet<UEdGraphNode*>& Visiting)
{
    const FKmsGraphFormatNode* ConsumerPlacement = ExecPlacements.Find(Consumer);
    if (Consumer == nullptr || ConsumerPlacement == nullptr)
    {
        return;
    }

    int32 InputIndex = 0;
    for (UEdGraphPin* Pin : Consumer->Pins)
    {
        if (Pin == nullptr || Pin->Direction != EGPD_Input || Pin->PinType.PinCategory == UEdGraphSchema_K2::PC_Exec)
        {
            continue;
        }

        for (UEdGraphPin* LinkedPin : Pin->LinkedTo)
        {
            UEdGraphNode* InputNode = LinkedPin != nullptr ? LinkedPin->GetOwningNode() : nullptr;
            if (InputNode == nullptr
                || IsExecNodeForFormat(InputNode)
                || !IsGeneratedNodeForFormat(InputNode)
                || Visiting.Contains(InputNode))
            {
                continue;
            }

            Visiting.Add(InputNode);
            FKmsGraphFormatNode Placement;
            Placement.Node = InputNode;
            Placement.X = ConsumerPlacement->X - 300;
            Placement.Y = ConsumerPlacement->Y + InputIndex * 110 - 30;
            Placement.Width = EstimateNodeWidth(InputNode);
            Placement.Height = EstimateNodeHeight(InputNode);
            Placements.Add(InputNode, Placement);
            PlacePureInputsForNode(InputNode, Placements, Placements, Visiting);
            Visiting.Remove(InputNode);
            ++InputIndex;
        }
    }
}

static bool BoundsOverlap(const FKmsGraphFormatNode& A, const FKmsGraphFormatNode& B)
{
    constexpr int32 Padding = 40;
    return A.X < B.X + B.Width + Padding
        && A.X + A.Width + Padding > B.X
        && A.Y < B.Y + B.Height + Padding
        && A.Y + A.Height + Padding > B.Y;
}

static void ResolveFormatOverlaps(TArray<FKmsGraphFormatNode*>& Nodes)
{
    Nodes.Sort([](const FKmsGraphFormatNode& A, const FKmsGraphFormatNode& B)
    {
        if (A.X != B.X)
        {
            return A.X < B.X;
        }
        return A.Y < B.Y;
    });

    for (int32 Index = 0; Index < Nodes.Num(); ++Index)
    {
        FKmsGraphFormatNode* Current = Nodes[Index];
        if (Current == nullptr)
        {
            continue;
        }

        bool bMoved = true;
        while (bMoved)
        {
            bMoved = false;
            for (int32 PreviousIndex = 0; PreviousIndex < Index; ++PreviousIndex)
            {
                FKmsGraphFormatNode* Previous = Nodes[PreviousIndex];
                if (Previous != nullptr && BoundsOverlap(*Current, *Previous))
                {
                    Current->Y = Previous->Y + Previous->Height + 80;
                    bMoved = true;
                }
            }
        }
    }
}

static void FormatGeneratedGraph(UBlueprint* Blueprint, UEdGraph* Graph)
{
    if (Blueprint == nullptr || Graph == nullptr)
    {
        return;
    }

    TMap<UEdGraphNode*, FKmsGraphFormatNode> Placements;
    TSet<UEdGraphNode*> Visiting;

    UEdGraphNode* EntryNode = FindFunctionEntryNode(Graph);
    if (EntryNode == nullptr)
    {
        for (UEdGraphNode* Node : Graph->Nodes)
        {
            if (Node != nullptr && IsExecNodeForFormat(Node) && !HasExecPin(Node, EGPD_Input))
            {
                EntryNode = Node;
                break;
            }
        }
    }

    if (EntryNode != nullptr)
    {
        PlaceExecTree(EntryNode, 0, 0, Placements, Visiting);
        if (!IsExecNodeForFormat(EntryNode))
        {
            if (UK2Node_FunctionResult* ResultNode = FindFunctionResultNode(Graph); ResultNode != nullptr && !Placements.Contains(ResultNode))
            {
                FKmsGraphFormatNode ResultPlacement;
                ResultPlacement.Node = ResultNode;
                ResultPlacement.X = 360;
                ResultPlacement.Y = 0;
                ResultPlacement.Width = EstimateNodeWidth(ResultNode);
                ResultPlacement.Height = EstimateNodeHeight(ResultNode);
                Placements.Add(ResultNode, ResultPlacement);
            }
        }
    }

    int32 FallbackY = 0;
    for (UEdGraphNode* Node : Graph->Nodes)
    {
        if (Node == nullptr || !IsGeneratedNodeForFormat(Node) || Placements.Contains(Node))
        {
            continue;
        }

        if (IsExecNodeForFormat(Node))
        {
            PlaceExecTree(Node, 0, FallbackY, Placements, Visiting);
            FallbackY += 220;
        }
    }

    TMap<UEdGraphNode*, FKmsGraphFormatNode> ExecPlacements = Placements;
    for (const TPair<UEdGraphNode*, FKmsGraphFormatNode>& Pair : ExecPlacements)
    {
        PlacePureInputsForNode(Pair.Key, ExecPlacements, Placements, Visiting);
    }

    int32 MarkerY = 520;
    for (UEdGraphNode* Node : Graph->Nodes)
    {
        if (Node == nullptr || !IsGeneratedNodeForFormat(Node) || Placements.Contains(Node))
        {
            continue;
        }

        FKmsGraphFormatNode Placement;
        Placement.Node = Node;
        Placement.X = -660;
        Placement.Y = MarkerY;
        Placement.Width = EstimateNodeWidth(Node);
        Placement.Height = EstimateNodeHeight(Node);
        Placements.Add(Node, Placement);
        MarkerY += Placement.Height + 80;
    }

    TArray<FKmsGraphFormatNode*> PlacementValues;
    for (TPair<UEdGraphNode*, FKmsGraphFormatNode>& Pair : Placements)
    {
        PlacementValues.Add(&Pair.Value);
    }
    ResolveFormatOverlaps(PlacementValues);

    bool bChanged = false;
    for (const TPair<UEdGraphNode*, FKmsGraphFormatNode>& Pair : Placements)
    {
        UEdGraphNode* Node = Pair.Key;
        if (Node == nullptr)
        {
            continue;
        }

        if (Node->NodePosX != Pair.Value.X || Node->NodePosY != Pair.Value.Y)
        {
            Node->Modify();
            Node->NodePosX = Pair.Value.X;
            Node->NodePosY = Pair.Value.Y;
            bChanged = true;
        }
    }

    int32 CommentY = MarkerY + 120;
    for (UEdGraphNode* Node : Graph->Nodes)
    {
        UEdGraphNode_Comment* Comment = Cast<UEdGraphNode_Comment>(Node);
        if (Comment == nullptr || !Comment->NodeComment.StartsWith(KmsGeneratedCommentPrefix))
        {
            continue;
        }

        Comment->Modify();
        Comment->NodePosX = -660;
        Comment->NodePosY = CommentY;
        CommentY += FMath::Max(Comment->NodeHeight, 120) + 80;
        bChanged = true;
    }

    if (bChanged)
    {
        Graph->Modify();
        FBlueprintEditorUtils::MarkBlueprintAsModified(Blueprint);
    }
}

static FKmsBpJsonProperty ParseProperty(const TSharedPtr<FJsonObject>& Object)
{
    FKmsBpJsonProperty Property;
    Property.Name = GetStringField(Object, TEXT("name"));
    Property.Type = GetStringField(Object, TEXT("type"));
    Property.Value = GetObjectField(Object, TEXT("value"));
    return Property;
}

static FKmsBpJsonComponent ParseComponent(const TSharedPtr<FJsonObject>& Object)
{
    FKmsBpJsonComponent Component;
    Component.Name = GetStringField(Object, TEXT("name"));
    Component.Type = GetStringField(Object, TEXT("type"));
    Component.bIsRoot = GetBoolField(Object, TEXT("isRoot"));
    Component.AttachTarget = GetStringField(Object, TEXT("attachTarget"));
    Component.Metadata = ParseMetadata(Object);

    const TArray<TSharedPtr<FJsonValue>>* Properties = nullptr;
    if (GetArrayField(Object, TEXT("properties"), Properties))
    {
        for (const TSharedPtr<FJsonValue>& Value : *Properties)
        {
            Component.Properties.Add(ParseProperty(Value.IsValid() ? Value->AsObject() : nullptr));
        }
    }

    return Component;
}

static FKmsBpJsonVariable ParseVariable(const TSharedPtr<FJsonObject>& Object)
{
    FKmsBpJsonVariable Variable;
    Variable.Name = GetStringField(Object, TEXT("name"));
    Variable.Type = GetStringField(Object, TEXT("type"));
    Variable.bIsEditable = GetBoolField(Object, TEXT("isEditable"));
    Variable.Category = GetStringField(Object, TEXT("category"));
    Variable.Metadata = ParseMetadata(Object);
    Variable.Initializer = GetObjectField(Object, TEXT("initializer"));
    return Variable;
}

static FKmsBpJsonParameter ParseParameter(const TSharedPtr<FJsonObject>& Object)
{
    FKmsBpJsonParameter Parameter;
    Parameter.Name = GetStringField(Object, TEXT("name"));
    Parameter.Type = GetStringField(Object, TEXT("type"));
    Parameter.Modifier = GetStringField(Object, TEXT("modifier"));
    return Parameter;
}

static FKmsBpJsonProcedure ParseProcedure(const TSharedPtr<FJsonObject>& Object)
{
    FKmsBpJsonProcedure Procedure;
    Procedure.Name = GetStringField(Object, TEXT("name"));
    Procedure.Kind = GetStringField(Object, TEXT("kind"));
    Procedure.ReturnType = GetStringField(Object, TEXT("returnType"));
    Procedure.EventName = GetStringField(Object, TEXT("eventName"));
    Procedure.Category = GetStringField(Object, TEXT("category"));
    Procedure.Metadata = ParseMetadata(Object);
    Procedure.Body = GetObjectField(Object, TEXT("body"));

    const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
    if (GetArrayField(Object, TEXT("parameters"), Parameters))
    {
        for (const TSharedPtr<FJsonValue>& Value : *Parameters)
        {
            Procedure.Parameters.Add(ParseParameter(Value.IsValid() ? Value->AsObject() : nullptr));
        }
    }

    return Procedure;
}

static FKmsBpJsonBlueprint ParseBlueprint(const TSharedPtr<FJsonObject>& Object)
{
    FKmsBpJsonBlueprint Blueprint;
    Blueprint.Name = GetStringField(Object, TEXT("name"));
    Blueprint.ParentType = GetStringField(Object, TEXT("parentType"));
    Blueprint.AssetPath = GetStringField(Object, TEXT("assetPath"));
    Blueprint.Metadata = ParseMetadata(Object);

    const TArray<TSharedPtr<FJsonValue>>* Components = nullptr;
    if (GetArrayField(Object, TEXT("components"), Components))
    {
        for (const TSharedPtr<FJsonValue>& Value : *Components)
        {
            Blueprint.Components.Add(ParseComponent(Value.IsValid() ? Value->AsObject() : nullptr));
        }
    }

    const TArray<TSharedPtr<FJsonValue>>* Variables = nullptr;
    if (GetArrayField(Object, TEXT("variables"), Variables))
    {
        for (const TSharedPtr<FJsonValue>& Value : *Variables)
        {
            Blueprint.Variables.Add(ParseVariable(Value.IsValid() ? Value->AsObject() : nullptr));
        }
    }

    const TArray<TSharedPtr<FJsonValue>>* Procedures = nullptr;
    if (GetArrayField(Object, TEXT("procedures"), Procedures))
    {
        for (const TSharedPtr<FJsonValue>& Value : *Procedures)
        {
            Blueprint.Procedures.Add(ParseProcedure(Value.IsValid() ? Value->AsObject() : nullptr));
        }
    }

    return Blueprint;
}

static bool ParseDocument(const FString& JsonText, FKmsBpJsonDocument& OutDocument, FKmsBpImportResult& Result)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(JsonText);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        AddError(Result, TEXT("Failed to parse KMS-BP JSON document."));
        return false;
    }

    OutDocument.SchemaVersion = GetStringField(Root, TEXT("schemaVersion"));
    OutDocument.LanguageVersion = GetStringField(Root, TEXT("languageVersion"));
    OutDocument.SourcePath = GetStringField(Root, TEXT("sourcePath"));
    OutDocument.SourceSha256 = GetStringField(Root, TEXT("sourceSha256"));
    if (!OutDocument.SchemaVersion.Equals(KmsBpSchemaVersion, ESearchCase::CaseSensitive))
    {
        AddError(Result, FString::Printf(TEXT("Unsupported schemaVersion '%s'. Expected '%s'."), *OutDocument.SchemaVersion, KmsBpSchemaVersion));
        return false;
    }
    if (OutDocument.LanguageVersion.IsEmpty())
    {
        OutDocument.LanguageVersion = TEXT("0");
    }
    if (!OutDocument.LanguageVersion.Equals(TEXT("0"), ESearchCase::CaseSensitive)
        && !OutDocument.LanguageVersion.Equals(TEXT("1"), ESearchCase::CaseSensitive))
    {
        AddError(Result, FString::Printf(TEXT("Unsupported KMS-BP languageVersion '%s'. This importer supports 0 and 1."), *OutDocument.LanguageVersion));
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Blueprints = nullptr;
    if (!GetArrayField(Root, TEXT("blueprints"), Blueprints))
    {
        AddError(Result, TEXT("JSON document is missing the 'blueprints' array."));
        return false;
    }

    for (const TSharedPtr<FJsonValue>& Value : *Blueprints)
    {
        OutDocument.Blueprints.Add(ParseBlueprint(Value.IsValid() ? Value->AsObject() : nullptr));
    }

    return true;
}

static FString ToObjectPath(const FString& PackageName)
{
    const FString AssetName = FPackageName::GetLongPackageAssetName(PackageName);
    return FString::Printf(TEXT("%s.%s"), *PackageName, *AssetName);
}

static UClass* LoadClassByPathOrName(const FString& TypeName, UClass* RequiredBaseClass)
{
    if (TypeName.IsEmpty())
    {
        return nullptr;
    }

    UClass* KnownClass = nullptr;
    if (TypeName.Equals(TEXT("Actor"), ESearchCase::IgnoreCase))
    {
        KnownClass = AActor::StaticClass();
    }
    else if (TypeName.Equals(TEXT("SceneComponent"), ESearchCase::IgnoreCase))
    {
        KnownClass = USceneComponent::StaticClass();
    }
    else if (TypeName.Equals(TEXT("StaticMeshComponent"), ESearchCase::IgnoreCase))
    {
        KnownClass = UStaticMeshComponent::StaticClass();
    }
    else if (TypeName.Equals(TEXT("StaticMesh"), ESearchCase::IgnoreCase))
    {
        KnownClass = UStaticMesh::StaticClass();
    }

    if (KnownClass != nullptr && KnownClass->IsChildOf(RequiredBaseClass))
    {
        return KnownClass;
    }

    if (TypeName.StartsWith(TEXT("/")))
    {
        if (UClass* Loaded = StaticLoadClass(RequiredBaseClass, nullptr, *TypeName))
        {
            return Loaded;
        }
    }

    TArray<FString> Candidates;
    Candidates.Add(FString::Printf(TEXT("/Script/Engine.%s"), *TypeName));
    Candidates.Add(FString::Printf(TEXT("/Script/CoreUObject.%s"), *TypeName));

    for (const FString& Candidate : Candidates)
    {
        if (UClass* Loaded = StaticLoadClass(RequiredBaseClass, nullptr, *Candidate))
        {
            return Loaded;
        }
    }

    return nullptr;
}

static UClass* ResolveParentClass(const FString& TypeName, FKmsBpImportResult& Result)
{
    if (TypeName.Equals(TEXT("Actor"), ESearchCase::IgnoreCase))
    {
        return AActor::StaticClass();
    }

    if (UClass* Class = LoadClassByPathOrName(TypeName, UObject::StaticClass()))
    {
        if (Class->IsChildOf(AActor::StaticClass()))
        {
            return Class;
        }

        AddWarning(Result, FString::Printf(TEXT("Parent type '%s' is not an Actor subclass; using Actor."), *TypeName));
    }
    else
    {
        AddWarning(Result, FString::Printf(TEXT("Could not resolve parent type '%s'; using Actor."), *TypeName));
    }

    return AActor::StaticClass();
}

static UClass* ResolveComponentClass(const FString& TypeName, FKmsBpImportResult& Result)
{
    if (TypeName.Equals(TEXT("SceneComponent"), ESearchCase::IgnoreCase))
    {
        return USceneComponent::StaticClass();
    }

    if (TypeName.Equals(TEXT("StaticMeshComponent"), ESearchCase::IgnoreCase))
    {
        return UStaticMeshComponent::StaticClass();
    }

    if (UClass* Class = LoadClassByPathOrName(TypeName, UActorComponent::StaticClass()))
    {
        if (Class->IsChildOf(UActorComponent::StaticClass()))
        {
            return Class;
        }
    }

    AddWarning(Result, FString::Printf(TEXT("Could not resolve component type '%s'; using SceneComponent."), *TypeName));
    return USceneComponent::StaticClass();
}

static bool ResolvePinType(const FString& TypeText, FEdGraphPinType& OutPinType, FKmsBpImportResult& Result, bool bIsReference = false)
{
    FString TrimmedType = TypeText;
    TrimmedType.TrimStartAndEndInline();

    FString ConstructedBaseType;
    TArray<FString> ConstructedArguments;
    if (TrySplitConstructedType(TrimmedType, ConstructedBaseType, ConstructedArguments))
    {
        if ((ConstructedBaseType.Equals(TEXT("Array"), ESearchCase::IgnoreCase)
            || ConstructedBaseType.Equals(TEXT("Set"), ESearchCase::IgnoreCase)) && ConstructedArguments.Num() == 1)
        {
            FEdGraphPinType ElementType;
            ResolvePinType(ConstructedArguments[0], ElementType, Result, false);
            OutPinType = ElementType;
            OutPinType.ContainerType = ConstructedBaseType.Equals(TEXT("Set"), ESearchCase::IgnoreCase)
                ? EPinContainerType::Set
                : EPinContainerType::Array;
            OutPinType.bIsReference = bIsReference;
            return true;
        }

        if (ConstructedBaseType.Equals(TEXT("Map"), ESearchCase::IgnoreCase) && ConstructedArguments.Num() == 2)
        {
            FEdGraphPinType KeyType;
            FEdGraphPinType ValueType;
            ResolvePinType(ConstructedArguments[0], KeyType, Result, false);
            ResolvePinType(ConstructedArguments[1], ValueType, Result, false);
            OutPinType = KeyType;
            OutPinType.ContainerType = EPinContainerType::Map;
            OutPinType.PinValueType = FEdGraphTerminalType::FromPinType(ValueType);
            OutPinType.bIsReference = bIsReference;
            return true;
        }
    }

    EPinContainerType ContainerType = EPinContainerType::None;
    if (TrimmedType.EndsWith(TEXT("[]")))
    {
        ContainerType = EPinContainerType::Array;
        TrimmedType.LeftChopInline(2);
    }

    FString WrapperType;
    if (TrySplitConstructedType(TrimmedType, ConstructedBaseType, ConstructedArguments)
        && ConstructedArguments.Num() == 1
        && (ConstructedBaseType.Equals(TEXT("Object"), ESearchCase::IgnoreCase)
            || ConstructedBaseType.Equals(TEXT("Class"), ESearchCase::IgnoreCase)
            || ConstructedBaseType.Equals(TEXT("SoftObject"), ESearchCase::IgnoreCase)
            || ConstructedBaseType.Equals(TEXT("SoftClass"), ESearchCase::IgnoreCase)
            || ConstructedBaseType.Equals(TEXT("Interface"), ESearchCase::IgnoreCase)))
    {
        WrapperType = ConstructedBaseType;
        TrimmedType = ConstructedArguments[0];
        TrimmedType.TrimStartAndEndInline();
    }

    FName Category = NAME_None;
    FName SubCategory = NAME_None;
    UObject* SubCategoryObject = nullptr;

    if (WrapperType.Equals(TEXT("Object"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Object;
        SubCategoryObject = LoadClassByPathOrName(TrimmedType, UObject::StaticClass());
    }
    else if (WrapperType.Equals(TEXT("Class"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Class;
        SubCategoryObject = LoadClassByPathOrName(TrimmedType, UObject::StaticClass());
    }
    else if (WrapperType.Equals(TEXT("SoftObject"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_SoftObject;
        SubCategoryObject = LoadClassByPathOrName(TrimmedType, UObject::StaticClass());
    }
    else if (WrapperType.Equals(TEXT("SoftClass"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_SoftClass;
        SubCategoryObject = LoadClassByPathOrName(TrimmedType, UObject::StaticClass());
    }
    else if (WrapperType.Equals(TEXT("Interface"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Interface;
        SubCategoryObject = LoadClassByPathOrName(TrimmedType, UObject::StaticClass());
    }
    else if (TrimmedType.Equals(TEXT("bool"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Boolean;
    }
    else if (TrimmedType.Equals(TEXT("byte"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Byte;
    }
    else if (TrimmedType.Equals(TEXT("int"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Int;
    }
    else if (TrimmedType.Equals(TEXT("float"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Real;
        SubCategory = UEdGraphSchema_K2::PC_Float;
    }
    else if (TrimmedType.Equals(TEXT("double"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Real;
        SubCategory = UEdGraphSchema_K2::PC_Double;
    }
    else if (TrimmedType.Equals(TEXT("string"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_String;
    }
    else if (TrimmedType.Equals(TEXT("name"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Name;
    }
    else if (TrimmedType.Equals(TEXT("text"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Text;
    }
    else
    {
        UClass* ObjectClass = LoadClassByPathOrName(TrimmedType, UObject::StaticClass());
        if (ObjectClass != nullptr)
        {
            Category = UEdGraphSchema_K2::PC_Object;
            SubCategoryObject = ObjectClass;
        }
    }

    if (Category == NAME_None)
    {
        AddWarning(Result, FString::Printf(TEXT("Could not map Blueprint variable type '%s'; using string."), *TypeText));
        Category = UEdGraphSchema_K2::PC_String;
    }

    const bool bObjectLikePin =
        Category == UEdGraphSchema_K2::PC_Object
        || Category == UEdGraphSchema_K2::PC_Class
        || Category == UEdGraphSchema_K2::PC_SoftObject
        || Category == UEdGraphSchema_K2::PC_SoftClass
        || Category == UEdGraphSchema_K2::PC_Interface;

    if (bObjectLikePin && SubCategoryObject == nullptr)
    {
        AddWarning(Result, FString::Printf(TEXT("Could not resolve Blueprint object type '%s'; using UObject."), *TypeText));
        SubCategoryObject = UObject::StaticClass();
    }

    OutPinType = FEdGraphPinType(
        Category,
        SubCategory,
        SubCategoryObject,
        ContainerType,
        bIsReference,
        FEdGraphTerminalType());

    return true;
}

static FString DefaultValueToString(const TSharedPtr<FJsonObject>& Initializer)
{
    FString StringValue;
    if (TryGetLiteralString(Initializer, StringValue))
    {
        return StringValue;
    }

    bool BoolValue = false;
    if (TryGetLiteralBool(Initializer, BoolValue))
    {
        return BoolValue ? TEXT("true") : TEXT("false");
    }

    double NumberValue = 0.0;
    if (TryGetLiteralNumber(Initializer, NumberValue))
    {
        const FString Type = GetStringField(Initializer, TEXT("type"));
        if (Type.Equals(TEXT("int"), ESearchCase::IgnoreCase)
            || Type.Equals(TEXT("int64"), ESearchCase::IgnoreCase)
            || Type.Equals(TEXT("uint32"), ESearchCase::IgnoreCase)
            || Type.Equals(TEXT("uint64"), ESearchCase::IgnoreCase))
        {
            return FString::Printf(TEXT("%lld"), static_cast<int64>(NumberValue));
        }

        return FString::SanitizeFloat(NumberValue);
    }

    return FString();
}

static FBPVariableDescription* FindNewVariable(UBlueprint* Blueprint, const FName VariableName)
{
    if (Blueprint == nullptr)
    {
        return nullptr;
    }

    return Blueprint->NewVariables.FindByPredicate([VariableName](const FBPVariableDescription& Variable)
    {
        return Variable.VarName == VariableName;
    });
}

static bool ApplyObjectPropertyValue(UObject* Target, const FKmsBpJsonProperty& Assignment, FKmsBpImportResult& Result)
{
    if (Target == nullptr)
    {
        return false;
    }

    FProperty* Property = Target->GetClass()->FindPropertyByName(FName(*Assignment.Name));
    if (Property == nullptr)
    {
        AddWarning(Result, FString::Printf(TEXT("Property '%s' was not found on '%s'."), *Assignment.Name, *Target->GetClass()->GetName()));
        return false;
    }

    void* ValuePtr = Property->ContainerPtrToValuePtr<void>(Target);
    if (FObjectPropertyBase* ObjectProperty = CastField<FObjectPropertyBase>(Property))
    {
        FString AssetPath;
        FString AssetType;
        if (!TryGetAssetReference(Assignment.Value, AssetPath, AssetType))
        {
            AddWarning(Result, FString::Printf(TEXT("Property '%s' expects an object value; only asset<T>(\"...\") is supported in this importer pass."), *Assignment.Name));
            return false;
        }

        UObject* LoadedAsset = StaticLoadObject(ObjectProperty->PropertyClass, nullptr, *AssetPath);
        if (LoadedAsset == nullptr)
        {
            AddWarning(Result, FString::Printf(TEXT("Could not load asset '%s' for property '%s'."), *AssetPath, *Assignment.Name));
            return false;
        }

        ObjectProperty->SetObjectPropertyValue(ValuePtr, LoadedAsset);
        Target->Modify();
        return true;
    }

    if (FBoolProperty* BoolProperty = CastField<FBoolProperty>(Property))
    {
        bool Value = false;
        if (TryGetLiteralBool(Assignment.Value, Value))
        {
            BoolProperty->SetPropertyValue(ValuePtr, Value);
            Target->Modify();
            return true;
        }
    }

    if (FNumericProperty* NumericProperty = CastField<FNumericProperty>(Property))
    {
        double NumberValue = 0.0;
        if (TryGetLiteralNumber(Assignment.Value, NumberValue))
        {
            if (NumericProperty->IsFloatingPoint())
            {
                NumericProperty->SetFloatingPointPropertyValue(ValuePtr, NumberValue);
            }
            else
            {
                NumericProperty->SetIntPropertyValue(ValuePtr, static_cast<int64>(NumberValue));
            }
            Target->Modify();
            return true;
        }
    }

    FString StringValue;
    if (TryGetLiteralString(Assignment.Value, StringValue))
    {
        if (FStrProperty* StringProperty = CastField<FStrProperty>(Property))
        {
            StringProperty->SetPropertyValue(ValuePtr, StringValue);
            Target->Modify();
            return true;
        }

        if (FNameProperty* NameProperty = CastField<FNameProperty>(Property))
        {
            NameProperty->SetPropertyValue(ValuePtr, FName(*StringValue));
            Target->Modify();
            return true;
        }

        if (FTextProperty* TextProperty = CastField<FTextProperty>(Property))
        {
            TextProperty->SetPropertyValue(ValuePtr, FText::FromString(StringValue));
            Target->Modify();
            return true;
        }
    }

    AddWarning(Result, FString::Printf(TEXT("Property '%s' value '%s' is not supported yet."), *Assignment.Name, *ExpressionToText(Assignment.Value)));
    return false;
}

static void CollectNodesChildrenFirst(USCS_Node* Node, TArray<USCS_Node*>& OutNodes)
{
    if (Node == nullptr)
    {
        return;
    }

    for (USCS_Node* Child : Node->GetChildNodes())
    {
        CollectNodesChildrenFirst(Child, OutNodes);
    }
    OutNodes.Add(Node);
}

static void ClearSCS(UBlueprint* Blueprint)
{
    if (Blueprint == nullptr || Blueprint->SimpleConstructionScript == nullptr)
    {
        return;
    }

    USimpleConstructionScript* SCS = Blueprint->SimpleConstructionScript;
    TArray<USCS_Node*> NodesToRemove;
    for (USCS_Node* RootNode : SCS->GetRootNodes())
    {
        CollectNodesChildrenFirst(RootNode, NodesToRemove);
    }

    for (USCS_Node* Node : NodesToRemove)
    {
        SCS->RemoveNode(Node, false);
    }

    SCS->ValidateSceneRootNodes();
}

static UBlueprint* CreateOrLoadBlueprint(const FKmsBpJsonBlueprint& JsonBlueprint, const FKmsBpImportOptions& Options, FKmsBpImportResult& Result)
{
    FString PackageName = JsonBlueprint.AssetPath;
    PackageName.TrimStartAndEndInline();

    if (!FPackageName::IsValidLongPackageName(PackageName))
    {
        AddError(Result, FString::Printf(TEXT("Invalid Blueprint asset path '%s'. Expected a long package path like /Game/Generated/BP_Door_Gen."), *PackageName));
        return nullptr;
    }

    const FString AssetName = FPackageName::GetLongPackageAssetName(PackageName);
    UBlueprint* Blueprint = nullptr;
    if (Options.bRecreateExistingBlueprint)
    {
        Blueprint = FindObject<UBlueprint>(nullptr, *ToObjectPath(PackageName));
        if (Blueprint != nullptr)
        {
            const int32 DeletedCount = ObjectTools::ForceDeleteObjects({ Blueprint }, false);
            if (DeletedCount <= 0)
            {
                AddError(Result, FString::Printf(TEXT("Failed to recreate existing Blueprint %s: delete failed."), *Blueprint->GetPathName()));
                return nullptr;
            }

            CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS);
            Blueprint = nullptr;
        }

        const FString ExistingPackageFilename = FPackageName::LongPackageNameToFilename(PackageName, FPackageName::GetAssetPackageExtension());
        if (IFileManager::Get().FileExists(*ExistingPackageFilename)
            && !IFileManager::Get().Delete(*ExistingPackageFilename, false, true))
        {
            AddError(Result, FString::Printf(TEXT("Failed to recreate Blueprint %s: could not delete '%s'."), *PackageName, *ExistingPackageFilename));
            return nullptr;
        }
    }
    else
    {
        Blueprint = Cast<UBlueprint>(StaticLoadObject(UBlueprint::StaticClass(), nullptr, *ToObjectPath(PackageName), nullptr, LOAD_NoWarn | LOAD_DisableCompileOnLoad));
    }
    if (Blueprint != nullptr)
    {
        AddMessage(Result, FString::Printf(TEXT("Updating existing Blueprint %s."), *Blueprint->GetPathName()));
        return Blueprint;
    }

    UClass* ParentClass = ResolveParentClass(JsonBlueprint.ParentType, Result);
    UPackage* Package = CreatePackage(*PackageName);
    if (Package == nullptr)
    {
        AddError(Result, FString::Printf(TEXT("Failed to create package '%s'."), *PackageName));
        return nullptr;
    }

    Blueprint = FKismetEditorUtilities::CreateBlueprint(
        ParentClass,
        Package,
        FName(*AssetName),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("KmsBpImport")));

    if (Blueprint == nullptr)
    {
        AddError(Result, FString::Printf(TEXT("Failed to create Blueprint '%s'."), *PackageName));
        return nullptr;
    }

    FAssetRegistryModule::AssetCreated(Blueprint);
    Package->MarkPackageDirty();
    AddMessage(Result, FString::Printf(TEXT("Created Blueprint %s."), *Blueprint->GetPathName()));
    return Blueprint;
}

static bool ImportComponents(UBlueprint* Blueprint, const FKmsBpJsonBlueprint& JsonBlueprint, const FKmsBpImportOptions& Options, FKmsBpImportResult& Result)
{
    if (Blueprint == nullptr)
    {
        return false;
    }

    USimpleConstructionScript* SCS = Blueprint->SimpleConstructionScript;
    if (SCS == nullptr)
    {
        AddWarning(Result, FString::Printf(TEXT("Blueprint '%s' does not support SimpleConstructionScript components."), *Blueprint->GetName()));
        return true;
    }

    if (Options.bClearGeneratedComponents)
    {
        ClearSCS(Blueprint);
    }

    TMap<FString, USCS_Node*> NodesByName;
    for (const FKmsBpJsonComponent& Component : JsonBlueprint.Components)
    {
        UClass* ComponentClass = ResolveComponentClass(Component.Type, Result);
        USCS_Node* Node = SCS->CreateNode(ComponentClass, FName(*Component.Name));
        if (Node == nullptr)
        {
            AddWarning(Result, FString::Printf(TEXT("Failed to create component node '%s'."), *Component.Name));
            continue;
        }

        for (const FKmsBpJsonProperty& Property : Component.Properties)
        {
            ApplyObjectPropertyValue(Node->ComponentTemplate, Property, Result);
        }

        NodesByName.Add(Component.Name, Node);
    }

    for (const FKmsBpJsonComponent& Component : JsonBlueprint.Components)
    {
        USCS_Node* const* NodePtr = NodesByName.Find(Component.Name);
        if (NodePtr == nullptr || *NodePtr == nullptr)
        {
            continue;
        }

        USCS_Node* Node = *NodePtr;
        if (!Component.AttachTarget.IsEmpty())
        {
            USCS_Node* const* ParentPtr = NodesByName.Find(Component.AttachTarget);
            if (ParentPtr != nullptr && *ParentPtr != nullptr)
            {
                (*ParentPtr)->AddChildNode(Node);
                continue;
            }

            AddWarning(Result, FString::Printf(TEXT("Attach target '%s' for component '%s' was not found; component will be added as a root node."), *Component.AttachTarget, *Component.Name));
        }

        SCS->AddNode(Node);
    }

    SCS->ValidateSceneRootNodes();
    return true;
}

static bool ImportVariables(UBlueprint* Blueprint, const FKmsBpJsonBlueprint& JsonBlueprint, FKmsBpImportResult& Result)
{
    if (Blueprint == nullptr)
    {
        return false;
    }

    for (const FKmsBpJsonVariable& Variable : JsonBlueprint.Variables)
    {
        FName VariableName(*Variable.Name);
        FEdGraphPinType PinType;
        ResolvePinType(Variable.Type, PinType, Result);

        FBPVariableDescription* ExistingVariable = FindNewVariable(Blueprint, VariableName);
        if (ExistingVariable == nullptr)
        {
            FBlueprintEditorUtils::AddMemberVariable(Blueprint, VariableName, PinType, DefaultValueToString(Variable.Initializer));
            ExistingVariable = FindNewVariable(Blueprint, VariableName);
        }
        else
        {
            ExistingVariable->VarType = PinType;
            ExistingVariable->DefaultValue = DefaultValueToString(Variable.Initializer);
        }

        if (ExistingVariable != nullptr)
        {
            ExistingVariable->PropertyFlags |= CPF_BlueprintVisible;
            if (Variable.bIsEditable)
            {
                ExistingVariable->PropertyFlags |= CPF_Edit;
            }
            else
            {
                ExistingVariable->PropertyFlags &= ~CPF_Edit;
            }

            if (!Variable.Category.IsEmpty())
            {
                ExistingVariable->Category = FText::FromString(Variable.Category);
            }

            const FString Tooltip = GetMetadataString(Variable.Metadata, TEXT("tooltip"));
            if (!Tooltip.IsEmpty())
            {
                ExistingVariable->SetMetaData(TEXT("Tooltip"), Tooltip);
            }

            if (HasMetadata(Variable.Metadata, TEXT("replicated")) || HasMetadata(Variable.Metadata, TEXT("repnotify")))
            {
                ExistingVariable->PropertyFlags |= CPF_Net;
                ExistingVariable->ReplicationCondition = COND_None;
            }

            const FString RepNotify = GetMetadataString(Variable.Metadata, TEXT("repnotify"));
            if (!RepNotify.IsEmpty())
            {
                ExistingVariable->RepNotifyFunc = FName(*RepNotify);
            }

            if (HasMetadata(Variable.Metadata, TEXT("exposeonspawn")))
            {
                ExistingVariable->PropertyFlags |= CPF_ExposeOnSpawn;
            }

            double ClampMin = 0.0;
            double ClampMax = 0.0;
            if (GetMetadataNumber(Variable.Metadata, TEXT("clamp"), 0, ClampMin))
            {
                ExistingVariable->SetMetaData(TEXT("ClampMin"), FString::SanitizeFloat(ClampMin));
            }
            if (GetMetadataNumber(Variable.Metadata, TEXT("clamp"), 1, ClampMax))
            {
                ExistingVariable->SetMetaData(TEXT("ClampMax"), FString::SanitizeFloat(ClampMax));
            }

            double UiMin = 0.0;
            double UiMax = 0.0;
            if (GetMetadataNumber(Variable.Metadata, TEXT("ui"), 0, UiMin))
            {
                ExistingVariable->SetMetaData(TEXT("UIMin"), FString::SanitizeFloat(UiMin));
            }
            if (GetMetadataNumber(Variable.Metadata, TEXT("ui"), 1, UiMax))
            {
                ExistingVariable->SetMetaData(TEXT("UIMax"), FString::SanitizeFloat(UiMax));
            }
        }
    }

    return true;
}

static UEdGraph* FindFunctionGraph(UBlueprint* Blueprint, const FName GraphName)
{
    if (Blueprint == nullptr)
    {
        return nullptr;
    }

    for (int32 Index = Blueprint->FunctionGraphs.Num() - 1; Index >= 0; --Index)
    {
        UEdGraph* Graph = Blueprint->FunctionGraphs[Index];
        if (Graph != nullptr && Graph->GetFName() == GraphName)
        {
            return Graph;
        }
    }

    return nullptr;
}

static void RemoveDuplicateFunctionGraphs(UBlueprint* Blueprint, const FName GraphName)
{
    if (Blueprint == nullptr)
    {
        return;
    }

    TArray<UEdGraph*> MatchingGraphs;
    for (UEdGraph* Graph : Blueprint->FunctionGraphs)
    {
        if (Graph != nullptr && Graph->GetFName() == GraphName)
        {
            MatchingGraphs.Add(Graph);
        }
    }

    if (MatchingGraphs.Num() <= 1)
    {
        return;
    }

    TArray<UEdGraph*> GraphsToRemove;
    for (int32 Index = 0; Index < MatchingGraphs.Num() - 1; ++Index)
    {
        GraphsToRemove.Add(MatchingGraphs[Index]);
    }

    if (GraphsToRemove.Num() > 0)
    {
        FBlueprintEditorUtils::RemoveGraphs(Blueprint, GraphsToRemove);
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
    }
}

static UEdGraph* FindOrCreateEventGraph(UBlueprint* Blueprint)
{
    UEdGraph* Graph = FBlueprintEditorUtils::FindEventGraph(Blueprint);
    if (Graph != nullptr)
    {
        return Graph;
    }

    Graph = FBlueprintEditorUtils::CreateNewGraph(
        Blueprint,
        UEdGraphSchema_K2::GN_EventGraph,
        UEdGraph::StaticClass(),
        UEdGraphSchema_K2::StaticClass());
    FBlueprintEditorUtils::AddUbergraphPage(Blueprint, Graph);
    return Graph;
}

static UEdGraph* FindOrCreateConstructionScriptGraph(UBlueprint* Blueprint)
{
    if (Blueprint == nullptr)
    {
        return nullptr;
    }

    if (UEdGraph* Existing = FBlueprintEditorUtils::FindUserConstructionScript(Blueprint))
    {
        return Existing;
    }

    return FKismetEditorUtilities::CreateUserConstructionScript(Blueprint);
}

static UEdGraph* FindOrCreateFunctionGraph(UBlueprint* Blueprint, const FString& Name, bool bPure)
{
    const FName GraphName(*Name);
    if (UEdGraph* Existing = FindFunctionGraph(Blueprint, GraphName))
    {
        return Existing;
    }

    UEdGraph* Graph = FBlueprintEditorUtils::CreateNewGraph(
        Blueprint,
        GraphName,
        UEdGraph::StaticClass(),
        UEdGraphSchema_K2::StaticClass());
    FBlueprintEditorUtils::AddFunctionGraph(Blueprint, Graph, true, static_cast<UFunction*>(nullptr));

    if (bPure)
    {
        if (const UEdGraphSchema_K2* K2Schema = Cast<const UEdGraphSchema_K2>(Graph->GetSchema()))
        {
            K2Schema->AddExtraFunctionFlags(Graph, FUNC_BlueprintPure);
        }
    }

    return Graph;
}

static UEdGraph* FindOrCreateDelegateSignatureGraph(UBlueprint* Blueprint, const FKmsBpJsonProcedure& Procedure, FKmsBpImportResult& Result)
{
    if (Blueprint == nullptr)
    {
        return nullptr;
    }

    const FName DelegateName(*Procedure.Name);
    FEdGraphPinType DelegateType;
    DelegateType.PinCategory = UEdGraphSchema_K2::PC_MCDelegate;

    bool bCreatedDelegateVariable = false;
    FBPVariableDescription* DelegateVariable = FindNewVariable(Blueprint, DelegateName);
    if (DelegateVariable == nullptr)
    {
        if (!FBlueprintEditorUtils::AddMemberVariable(Blueprint, DelegateName, DelegateType))
        {
            AddWarning(Result, FString::Printf(TEXT("Failed to create event dispatcher '%s'."), *Procedure.Name));
            return nullptr;
        }

        bCreatedDelegateVariable = true;
        DelegateVariable = FindNewVariable(Blueprint, DelegateName);
    }
    else
    {
        DelegateVariable->VarType = DelegateType;
        DelegateVariable->PropertyFlags |= CPF_BlueprintVisible | CPF_BlueprintAssignable | CPF_BlueprintCallable;
    }

    if (DelegateVariable != nullptr && !Procedure.Category.IsEmpty())
    {
        DelegateVariable->Category = FText::FromString(Procedure.Category);
    }

    if (UEdGraph* ExistingGraph = FBlueprintEditorUtils::GetDelegateSignatureGraphByName(Blueprint, DelegateName))
    {
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
        return ExistingGraph;
    }

    const UEdGraphSchema_K2* K2Schema = GetDefault<UEdGraphSchema_K2>();
    if (K2Schema == nullptr)
    {
        AddWarning(Result, FString::Printf(TEXT("Failed to create event dispatcher graph '%s': K2 schema unavailable."), *Procedure.Name));
        if (bCreatedDelegateVariable)
        {
            FBlueprintEditorUtils::RemoveMemberVariable(Blueprint, DelegateName);
        }
        return nullptr;
    }

    UEdGraph* NewGraph = FBlueprintEditorUtils::CreateNewGraph(
        Blueprint,
        DelegateName,
        UEdGraph::StaticClass(),
        UEdGraphSchema_K2::StaticClass());
    if (NewGraph == nullptr)
    {
        AddWarning(Result, FString::Printf(TEXT("Failed to create event dispatcher graph '%s'."), *Procedure.Name));
        if (bCreatedDelegateVariable)
        {
            FBlueprintEditorUtils::RemoveMemberVariable(Blueprint, DelegateName);
        }
        return nullptr;
    }

    NewGraph->bEditable = false;
    K2Schema->CreateDefaultNodesForGraph(*NewGraph);
    K2Schema->CreateFunctionGraphTerminators(*NewGraph, static_cast<UClass*>(nullptr));
    K2Schema->AddExtraFunctionFlags(NewGraph, FUNC_BlueprintCallable | FUNC_BlueprintEvent | FUNC_Public);
    K2Schema->MarkFunctionEntryAsEditable(NewGraph, true);

    Blueprint->DelegateSignatureGraphs.Add(NewGraph);
    FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
    return NewGraph;
}

static UK2Node_FunctionEntry* FindFunctionEntryNode(UEdGraph* Graph)
{
    if (Graph == nullptr)
    {
        return nullptr;
    }

    TArray<UK2Node_FunctionEntry*> EntryNodes;
    Graph->GetNodesOfClass(EntryNodes);
    return EntryNodes.Num() > 0 ? EntryNodes[0] : nullptr;
}

static UK2Node_FunctionResult* FindFunctionResultNode(UEdGraph* Graph)
{
    if (Graph == nullptr)
    {
        return nullptr;
    }

    TArray<UK2Node_FunctionResult*> ResultNodes;
    Graph->GetNodesOfClass(ResultNodes);
    return ResultNodes.Num() > 0 ? ResultNodes[0] : nullptr;
}

static void ClearUserDefinedPins(UK2Node_EditablePinBase* Node)
{
    if (Node == nullptr)
    {
        return;
    }

    TArray<FName> PinNames;
    for (const TSharedPtr<FUserPinInfo>& PinInfo : Node->UserDefinedPins)
    {
        if (PinInfo.IsValid())
        {
            PinNames.Add(PinInfo->PinName);
        }
    }

    for (const FName PinName : PinNames)
    {
        Node->RemoveUserDefinedPinByName(PinName);
    }
}

static bool IsVoidType(const FString& TypeText)
{
    FString TrimmedType = TypeText;
    TrimmedType.TrimStartAndEndInline();
    return TrimmedType.IsEmpty() || TrimmedType.Equals(TEXT("void"), ESearchCase::IgnoreCase);
}

static void SyncFunctionSignature(UBlueprint* Blueprint, UEdGraph* Graph, const FKmsBpJsonProcedure& Procedure, bool bPure, FKmsBpImportResult& Result)
{
    UK2Node_FunctionEntry* EntryNode = FindFunctionEntryNode(Graph);
    if (EntryNode == nullptr)
    {
        AddWarning(Result, FString::Printf(TEXT("Function graph '%s' has no entry node; parameters were not generated."), *Procedure.Name));
        return;
    }

    EntryNode->Modify();
    EntryNode->bIsEditable = true;
    if (!Procedure.Category.IsEmpty())
    {
        EntryNode->MetaData.Category = FText::FromString(Procedure.Category);
    }

    const FString Tooltip = GetMetadataString(Procedure.Metadata, TEXT("tooltip"));
    if (!Tooltip.IsEmpty())
    {
        EntryNode->MetaData.ToolTip = FText::FromString(Tooltip);
    }

    const FString Keywords = GetMetadataString(Procedure.Metadata, TEXT("keywords"));
    if (!Keywords.IsEmpty())
    {
        EntryNode->MetaData.Keywords = FText::FromString(Keywords);
    }

    if (bPure)
    {
        EntryNode->AddExtraFlags(FUNC_BlueprintPure);
    }
    else
    {
        EntryNode->ClearExtraFlags(FUNC_BlueprintPure);
    }

    if (HasMetadata(Procedure.Metadata, TEXT("callineditor")))
    {
        EntryNode->MetaData.bCallInEditor = true;
    }
    if (HasMetadata(Procedure.Metadata, TEXT("override")))
    {
        EntryNode->AddExtraFlags(FUNC_BlueprintEvent);
    }
    if (HasMetadata(Procedure.Metadata, TEXT("rpc")))
    {
        const FString RpcType = GetMetadataString(Procedure.Metadata, TEXT("rpc"), 0).ToLower();
        const FString Reliability = GetMetadataString(Procedure.Metadata, TEXT("rpc"), 1).ToLower();
        EntryNode->AddExtraFlags(FUNC_Net);
        if (RpcType.Equals(TEXT("server")))
        {
            EntryNode->AddExtraFlags(FUNC_NetServer);
        }
        else if (RpcType.Equals(TEXT("client")))
        {
            EntryNode->AddExtraFlags(FUNC_NetClient);
        }
        else if (RpcType.Equals(TEXT("multicast")) || RpcType.Equals(TEXT("netmulticast")))
        {
            EntryNode->AddExtraFlags(FUNC_NetMulticast);
        }
        if (Reliability.Equals(TEXT("reliable")))
        {
            EntryNode->AddExtraFlags(FUNC_NetReliable);
        }
    }

    UK2Node_FunctionResult* ResultNode = FindFunctionResultNode(Graph);
    ClearUserDefinedPins(EntryNode);
    ClearUserDefinedPins(ResultNode);

    bool bNeedsResultNode = !IsVoidType(Procedure.ReturnType);
    for (const FKmsBpJsonParameter& Parameter : Procedure.Parameters)
    {
        if (Parameter.Modifier.Equals(TEXT("out"), ESearchCase::IgnoreCase))
        {
            bNeedsResultNode = true;
            continue;
        }

        FEdGraphPinType PinType;
        ResolvePinType(Parameter.Type, PinType, Result, Parameter.Modifier.Equals(TEXT("ref"), ESearchCase::IgnoreCase));
        EntryNode->CreateUserDefinedPin(FName(*Parameter.Name), PinType, EGPD_Output, false);
    }

    if (bNeedsResultNode && ResultNode == nullptr)
    {
        ResultNode = FBlueprintEditorUtils::FindOrCreateFunctionResultNode(EntryNode);
    }

    if (ResultNode != nullptr)
    {
        ResultNode->Modify();
        ResultNode->bIsEditable = true;

        if (!IsVoidType(Procedure.ReturnType))
        {
            FEdGraphPinType ReturnPinType;
            ResolvePinType(Procedure.ReturnType, ReturnPinType, Result);
            ResultNode->CreateUserDefinedPin(UEdGraphSchema_K2::PN_ReturnValue, ReturnPinType, EGPD_Input, false);
        }

        for (const FKmsBpJsonParameter& Parameter : Procedure.Parameters)
        {
            if (!Parameter.Modifier.Equals(TEXT("out"), ESearchCase::IgnoreCase))
            {
                continue;
            }

            FEdGraphPinType PinType;
            ResolvePinType(Parameter.Type, PinType, Result);
            ResultNode->CreateUserDefinedPin(FName(*Parameter.Name), PinType, EGPD_Input, false);
        }
    }

    EntryNode->ReconstructNode();
    if (ResultNode != nullptr)
    {
        ResultNode->ReconstructNode();
    }

    Graph->Modify();
    FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
}

static void SyncDelegateSignature(UBlueprint* Blueprint, UEdGraph* Graph, const FKmsBpJsonProcedure& Procedure, FKmsBpImportResult& Result)
{
    UK2Node_FunctionEntry* EntryNode = FindFunctionEntryNode(Graph);
    if (EntryNode == nullptr)
    {
        AddWarning(Result, FString::Printf(TEXT("Dispatcher graph '%s' has no entry node; parameters were not generated."), *Procedure.Name));
        return;
    }

    EntryNode->Modify();
    EntryNode->bIsEditable = true;
    EntryNode->AddExtraFlags(FUNC_BlueprintCallable | FUNC_BlueprintEvent | FUNC_Public);
    if (!Procedure.Category.IsEmpty())
    {
        EntryNode->MetaData.Category = FText::FromString(Procedure.Category);
    }

    ClearUserDefinedPins(EntryNode);
    for (const FKmsBpJsonParameter& Parameter : Procedure.Parameters)
    {
        if (Parameter.Modifier.Equals(TEXT("out"), ESearchCase::IgnoreCase))
        {
            AddWarning(Result, FString::Printf(TEXT("Dispatcher '%s' parameter '%s' uses out; event dispatcher outputs are not supported."), *Procedure.Name, *Parameter.Name));
            continue;
        }

        FEdGraphPinType PinType;
        ResolvePinType(Parameter.Type, PinType, Result, Parameter.Modifier.Equals(TEXT("ref"), ESearchCase::IgnoreCase));
        EntryNode->CreateUserDefinedPin(FName(*Parameter.Name), PinType, EGPD_Output, false);
    }

    EntryNode->ReconstructNode();
    Graph->Modify();
    FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
}

static bool HasKmsGeneratedComment(UEdGraph* Graph, const FString& ProcedureName)
{
    if (Graph == nullptr)
    {
        return false;
    }

    for (UEdGraphNode* Node : Graph->Nodes)
    {
        if (const UEdGraphNode_Comment* Comment = Cast<UEdGraphNode_Comment>(Node))
        {
            if (Comment->NodeComment.StartsWith(KmsGeneratedCommentPrefix) && Comment->NodeComment.Contains(ProcedureName))
            {
                return true;
            }
        }
    }

    return false;
}

static void AddProcedureComment(UBlueprint* Blueprint, UEdGraph* Graph, const FKmsBpJsonProcedure& Procedure, int32 Index)
{
    if (Graph == nullptr || HasKmsGeneratedComment(Graph, Procedure.Name))
    {
        return;
    }

    FString CommentText = FString::Printf(
        TEXT("%s %s %s\nReturn: %s"),
        KmsGeneratedCommentPrefix,
        *Procedure.Kind,
        *Procedure.Name,
        *Procedure.ReturnType);

    if (!Procedure.EventName.IsEmpty())
    {
        CommentText += FString::Printf(TEXT("\nEvent: %s"), *Procedure.EventName);
    }

    if (!Procedure.Category.IsEmpty())
    {
        CommentText += FString::Printf(TEXT("\nCategory: %s"), *Procedure.Category);
    }

    if (Procedure.Parameters.Num() > 0)
    {
        TArray<FString> ParameterTexts;
        for (const FKmsBpJsonParameter& Parameter : Procedure.Parameters)
        {
            const FString Prefix = Parameter.Modifier.IsEmpty() ? TEXT("") : Parameter.Modifier + TEXT(" ");
            ParameterTexts.Add(FString::Printf(TEXT("%s%s: %s"), *Prefix, *Parameter.Name, *Parameter.Type));
        }

        CommentText += FString::Printf(TEXT("\nParameters: %s"), *FString::Join(ParameterTexts, TEXT(", ")));
    }

    const FString BodyText = StatementToText(Procedure.Body);
    if (!BodyText.IsEmpty())
    {
        CommentText += TEXT("\n\nKMS body skeleton:\n") + BodyText;
    }

    UEdGraphNode_Comment* CommentNode = NewObject<UEdGraphNode_Comment>(Graph);
    CommentNode->SetFlags(RF_Transactional);
    CommentNode->NodePosX = 160;
    CommentNode->NodePosY = 120 + Index * 240;
    CommentNode->NodeWidth = 640;
    CommentNode->NodeHeight = 180;
    CommentNode->CreateNewGuid();
    CommentNode->PostPlacedNewNode();
    CommentNode->AllocateDefaultPins();
    CommentNode->NodeComment = CommentText;

    Graph->Modify();
    Graph->AddNode(CommentNode, true, false);
    FBlueprintEditorUtils::MarkBlueprintAsModified(Blueprint);
}

static bool ImportProcedures(UBlueprint* Blueprint, const FKmsBpJsonBlueprint& JsonBlueprint, const FKmsBpImportOptions& Options, FKmsBpImportResult& Result)
{
    if (Blueprint == nullptr || !Options.bCreateProcedureGraphs)
    {
        return true;
    }

    int32 ProcedureIndex = 0;
    for (const FKmsBpJsonProcedure& Procedure : JsonBlueprint.Procedures)
    {
        if (Procedure.Kind.Equals(TEXT("event"), ESearchCase::IgnoreCase))
        {
            UEdGraph* EventGraph = FindOrCreateEventGraph(Blueprint);
            MaterializeProcedureNodes(Blueprint, EventGraph, Procedure, false, Result);
            AddProcedureComment(Blueprint, EventGraph, Procedure, ProcedureIndex++);
            if (Options.bFormatGraphs)
            {
                FormatGeneratedGraph(Blueprint, EventGraph);
            }
            continue;
        }

        if (Procedure.Kind.Equals(TEXT("construction"), ESearchCase::IgnoreCase))
        {
            UEdGraph* ConstructionGraph = FindOrCreateConstructionScriptGraph(Blueprint);
            MaterializeProcedureNodes(Blueprint, ConstructionGraph, Procedure, false, Result);
            AddProcedureComment(Blueprint, ConstructionGraph, Procedure, ProcedureIndex++);
            if (Options.bFormatGraphs)
            {
                FormatGeneratedGraph(Blueprint, ConstructionGraph);
            }
            continue;
        }

        if (Procedure.Kind.Equals(TEXT("dispatcher"), ESearchCase::IgnoreCase))
        {
            UEdGraph* DelegateGraph = FindOrCreateDelegateSignatureGraph(Blueprint, Procedure, Result);
            SyncDelegateSignature(Blueprint, DelegateGraph, Procedure, Result);
            AddProcedureComment(Blueprint, DelegateGraph, Procedure, ProcedureIndex++);
            if (Options.bFormatGraphs)
            {
                FormatGeneratedGraph(Blueprint, DelegateGraph);
            }
            continue;
        }

        if (Procedure.Kind.Equals(TEXT("callable"), ESearchCase::IgnoreCase)
            || Procedure.Kind.Equals(TEXT("pure"), ESearchCase::IgnoreCase))
        {
            const bool bPure = Procedure.Kind.Equals(TEXT("pure"), ESearchCase::IgnoreCase);
            RemoveDuplicateFunctionGraphs(Blueprint, FName(*Procedure.Name));
            UEdGraph* FunctionGraph = FindOrCreateFunctionGraph(Blueprint, Procedure.Name, bPure);
            SyncFunctionSignature(Blueprint, FunctionGraph, Procedure, bPure, Result);
            MaterializeProcedureNodes(Blueprint, FunctionGraph, Procedure, bPure, Result);
            AddProcedureComment(Blueprint, FunctionGraph, Procedure, ProcedureIndex++);
            if (Options.bFormatGraphs)
            {
                FormatGeneratedGraph(Blueprint, FunctionGraph);
            }
            continue;
        }

        AddWarning(Result, FString::Printf(TEXT("Unsupported procedure kind '%s' for '%s'."), *Procedure.Kind, *Procedure.Name));
    }

    return true;
}

static bool SaveBlueprintPackage(UBlueprint* Blueprint, FKmsBpImportResult& Result)
{
    if (Blueprint == nullptr)
    {
        return false;
    }

    TArray<UPackage*> Packages;
    Packages.Add(Blueprint->GetOutermost());
    if (!UEditorLoadingAndSavingUtils::SavePackages(Packages, false))
    {
        AddError(Result, FString::Printf(TEXT("Failed to save package '%s'."), *Blueprint->GetOutermost()->GetName()));
        return false;
    }

    return true;
}

static bool ImportBlueprint(const FKmsBpJsonBlueprint& JsonBlueprint, const FKmsBpImportOptions& Options, FKmsBpImportResult& Result)
{
    UBlueprint* Blueprint = CreateOrLoadBlueprint(JsonBlueprint, Options, Result);
    if (Blueprint == nullptr)
    {
        return false;
    }

    Blueprint->Modify();
    ImportComponents(Blueprint, JsonBlueprint, Options, Result);
    ImportVariables(Blueprint, JsonBlueprint, Result);
    ImportProcedures(Blueprint, JsonBlueprint, Options, Result);

    FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
    if (Options.bCompile)
    {
        FKismetEditorUtilities::CompileBlueprint(Blueprint);
    }

    Blueprint->GetOutermost()->MarkPackageDirty();
    if (Options.bSave && !SaveBlueprintPackage(Blueprint, Result))
    {
        return false;
    }

    Result.ImportedAssets.Add(Blueprint->GetPathName());
    return true;
}
}

bool UKmsBpImporterLibrary::ImportKmsBlueprintJson(const FString& JsonPath, const FKmsBpImportOptions& Options, FKmsBpImportResult& Result)
{
    Result = FKmsBpImportResult();

    FString JsonText;
    if (!FFileHelper::LoadFileToString(JsonText, *JsonPath))
    {
        AddError(Result, FString::Printf(TEXT("Could not read JSON file '%s'."), *JsonPath));
        return false;
    }

    FKmsBpJsonDocument Document;
    if (!ParseDocument(JsonText, Document, Result))
    {
        return false;
    }

    if (Document.Blueprints.Num() == 0)
    {
        AddError(Result, TEXT("KMS-BP JSON contains no blueprints."));
        return false;
    }

    bool bAllSucceeded = true;
    for (const FKmsBpJsonBlueprint& Blueprint : Document.Blueprints)
    {
        if (Blueprint.AssetPath.IsEmpty())
        {
            AddError(Result, FString::Printf(TEXT("Blueprint '%s' has an empty assetPath."), *Blueprint.Name));
            bAllSucceeded = false;
            continue;
        }

        bAllSucceeded &= ImportBlueprint(Blueprint, Options, Result);
    }

    Result.bSuccess = bAllSucceeded;
    return bAllSucceeded;
}

FString UKmsBpImporterLibrary::KmsGeneratedExpression(const FString& Kind, const FString& Text)
{
    return Text.IsEmpty() ? Kind : Text;
}

void UKmsBpImporterLibrary::KmsGeneratedStatement(const FString& Kind, const FString& Text)
{
}

FString UKmsBpImporterLibrary::KmsGeneratedStatementPure(const FString& Kind, const FString& Text)
{
    return Text.IsEmpty() ? Kind : Text;
}
