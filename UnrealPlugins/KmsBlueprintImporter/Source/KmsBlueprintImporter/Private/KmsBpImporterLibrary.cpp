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
#include "FileHelpers.h"
#include "GameFramework/Actor.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/Package.h"
#include "UObject/UnrealType.h"

namespace
{
constexpr const TCHAR* KmsBpSchemaVersion = TEXT("kms-bp-export-v1");
constexpr const TCHAR* KmsGeneratedCommentPrefix = TEXT("[KMS-BP]");

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
    TArray<FKmsBpJsonProperty> Properties;
};

struct FKmsBpJsonVariable
{
    FString Name;
    FString Type;
    bool bIsEditable = false;
    FString Category;
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
    TArray<FKmsBpJsonParameter> Parameters;
    TSharedPtr<FJsonObject> Body;
};

struct FKmsBpJsonBlueprint
{
    FString Name;
    FString ParentType;
    FString AssetPath;
    TArray<FKmsBpJsonComponent> Components;
    TArray<FKmsBpJsonVariable> Variables;
    TArray<FKmsBpJsonProcedure> Procedures;
};

struct FKmsBpJsonDocument
{
    FString SchemaVersion;
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

    return TryGetLiteralString((*Arguments)[0]->AsObject(), OutPath);
}

static FString ExpressionToText(const TSharedPtr<FJsonObject>& Expression);

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
        TArray<FString> ArgumentTexts;
        const TArray<TSharedPtr<FJsonValue>>* Arguments = nullptr;
        if (GetArrayField(Expression, TEXT("arguments"), Arguments))
        {
            for (const TSharedPtr<FJsonValue>& Argument : *Arguments)
            {
                ArgumentTexts.Add(ExpressionToText(Argument.IsValid() ? Argument->AsObject() : nullptr));
            }
        }

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

        return FString::Printf(TEXT("%s%s(%s"), *GetStringField(Expression, TEXT("name")), *TypeSuffix, *FString::Join(ArgumentTexts, TEXT(", "))) + TEXT(")");
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
        return FString::Printf(TEXT("%s%s"), *GetStringField(Expression, TEXT("op")), *ExpressionToText(GetObjectField(Expression, TEXT("operand"))));
    }

    if (Kind.Equals(TEXT("member"), ESearchCase::IgnoreCase))
    {
        return FString::Printf(
            TEXT("%s%s%s"),
            *ExpressionToText(GetObjectField(Expression, TEXT("context"))),
            *GetStringField(Expression, TEXT("op")),
            *ExpressionToText(GetObjectField(Expression, TEXT("member"))));
    }

    return Kind.IsEmpty() ? TEXT("<expression>") : Kind;
}

static FString StatementToText(const TSharedPtr<FJsonObject>& Statement, int32 Indent = 0)
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
        return FString::Printf(
            TEXT("%svar %s: %s = %s;"),
            *Padding,
            *GetStringField(Statement, TEXT("name")),
            *GetStringField(Statement, TEXT("type")),
            *ExpressionToText(GetObjectField(Statement, TEXT("initializer"))));
    }

    if (Kind.Equals(TEXT("return"), ESearchCase::IgnoreCase))
    {
        const TSharedPtr<FJsonObject> Value = GetObjectField(Statement, TEXT("value"));
        return Value.IsValid()
            ? Padding + TEXT("return ") + ExpressionToText(Value) + TEXT(";")
            : Padding + TEXT("return;");
    }

    return Padding + Kind;
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
    OutDocument.SourcePath = GetStringField(Root, TEXT("sourcePath"));
    OutDocument.SourceSha256 = GetStringField(Root, TEXT("sourceSha256"));
    if (!OutDocument.SchemaVersion.Equals(KmsBpSchemaVersion, ESearchCase::CaseSensitive))
    {
        AddError(Result, FString::Printf(TEXT("Unsupported schemaVersion '%s'. Expected '%s'."), *OutDocument.SchemaVersion, KmsBpSchemaVersion));
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

static bool ResolvePinType(const FString& TypeText, FEdGraphPinType& OutPinType, FKmsBpImportResult& Result)
{
    FString TrimmedType = TypeText;
    TrimmedType.TrimStartAndEndInline();

    bool bIsArray = false;
    if (TrimmedType.StartsWith(TEXT("Array<")) && TrimmedType.EndsWith(TEXT(">")))
    {
        bIsArray = true;
        TrimmedType = TrimmedType.Mid(6, TrimmedType.Len() - 7);
    }
    else if (TrimmedType.EndsWith(TEXT("[]")))
    {
        bIsArray = true;
        TrimmedType.LeftChopInline(2);
    }

    FName Category = NAME_None;
    FName SubCategory = NAME_None;
    UObject* SubCategoryObject = nullptr;

    if (TrimmedType.Equals(TEXT("bool"), ESearchCase::IgnoreCase))
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
    else if (TrimmedType.Equals(TEXT("Name"), ESearchCase::IgnoreCase))
    {
        Category = UEdGraphSchema_K2::PC_Name;
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

    OutPinType = FEdGraphPinType(
        Category,
        SubCategory,
        SubCategoryObject,
        bIsArray ? EPinContainerType::Array : EPinContainerType::None,
        false,
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

static UBlueprint* CreateOrLoadBlueprint(const FKmsBpJsonBlueprint& JsonBlueprint, FKmsBpImportResult& Result)
{
    FString PackageName = JsonBlueprint.AssetPath;
    PackageName.TrimStartAndEndInline();

    if (!FPackageName::IsValidLongPackageName(PackageName))
    {
        AddError(Result, FString::Printf(TEXT("Invalid Blueprint asset path '%s'. Expected a long package path like /Game/Generated/BP_Door_Gen."), *PackageName));
        return nullptr;
    }

    const FString AssetName = FPackageName::GetLongPackageAssetName(PackageName);
    UBlueprint* Blueprint = LoadObject<UBlueprint>(nullptr, *ToObjectPath(PackageName));
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
                Node->SetParent(*ParentPtr);
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

    for (UEdGraph* Graph : Blueprint->FunctionGraphs)
    {
        if (Graph != nullptr && Graph->GetFName() == GraphName)
        {
            return Graph;
        }
    }

    return nullptr;
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
            AddProcedureComment(Blueprint, EventGraph, Procedure, ProcedureIndex++);
            continue;
        }

        if (Procedure.Kind.Equals(TEXT("callable"), ESearchCase::IgnoreCase)
            || Procedure.Kind.Equals(TEXT("pure"), ESearchCase::IgnoreCase))
        {
            UEdGraph* FunctionGraph = FindOrCreateFunctionGraph(Blueprint, Procedure.Name, Procedure.Kind.Equals(TEXT("pure"), ESearchCase::IgnoreCase));
            AddProcedureComment(Blueprint, FunctionGraph, Procedure, ProcedureIndex++);
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
    UBlueprint* Blueprint = CreateOrLoadBlueprint(JsonBlueprint, Result);
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
