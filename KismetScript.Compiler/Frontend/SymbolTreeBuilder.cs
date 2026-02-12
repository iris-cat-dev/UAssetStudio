using KismetScript.Compiler.Compiler.Context;
using KismetScript.Syntax;
using KismetScript.Syntax.Statements;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions.Literals;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Kismet.Bytecode;

namespace KismetScript.Compiler.Compiler.Frontend;

public class SymbolTreeBuilder
{
    public void Build(CompilationUnit compilationUnit, Scope rootScope)
    {
        void ScanCompoundStatement(CompoundStatement compoundStatement, Symbol parent, bool isExternal)
        {
            foreach (var statement in compoundStatement)
            {
                ScanStatement(statement, parent, isExternal);
            }
        }

        void ScanStatement(Statement statement, Symbol parent, bool isExternal)
        {
            if (statement is Declaration declaration)
            {
                CreateDeclarationSymbol(declaration, parent, isExternal);
            }
            else if (statement is ForStatement forStatement)
            {
                ScanStatement(forStatement.Initializer, parent, isExternal);
                if (forStatement.Body != null)
                    ScanCompoundStatement(forStatement.Body, parent, isExternal);
            }
            else if (statement is IBlockStatement blockStatement)
            {
                foreach (var block in blockStatement.Blocks)
                    ScanCompoundStatement(block, parent, isExternal);
            }
        }

        Symbol CreateDeclarationSymbol(Declaration declaration, Symbol? parent, bool isExternal)
        {
            var declaringPackage = (parent is PackageSymbol packageSymbol) ? packageSymbol : parent?.DeclaringPackage;
            var declaringClass = (parent is ClassSymbol classSymbol) ? classSymbol : parent?.DeclaringClass;
            var declaringProcedure = (parent is ProcedureSymbol procedureSymbol) ? procedureSymbol : parent?.DeclaringProcedure;

            if (declaration is LabelDeclaration labelDeclaration)
            {
                return new LabelSymbol(labelDeclaration)
                {
                    Name = labelDeclaration.Identifier.Text,
                    DeclaringSymbol = parent,
                    IsExternal = isExternal,
                };
            }
            else if (declaration is VariableDeclaration variableDeclaration)
            {
                return new VariableSymbol(variableDeclaration)
                {
                    Name = variableDeclaration.Identifier.Text,
                    DeclaringSymbol = parent,
                    IsExternal = isExternal,
                };
            }
            else if (declaration is ProcedureDeclaration procedureDeclaration)
            {
                var symbol = new ProcedureSymbol(procedureDeclaration)
                {
                    Name = procedureDeclaration.Identifier.Text,
                    DeclaringSymbol = parent,
                    IsExternal = isExternal,
                    Flags = GetFunctionFlags(procedureDeclaration),
                    CustomFlags = GetCustomFunctionFlags(procedureDeclaration)
                };
                if (procedureDeclaration.Body != null)
                {
                    ScanCompoundStatement(procedureDeclaration.Body, symbol, isExternal);
                }
                return symbol;
            }
            else if (declaration is ClassDeclaration classDeclaration)
            {
                var symbol = new ClassSymbol(classDeclaration)
                {
                    Name = classDeclaration.Identifier.Text,
                    DeclaringSymbol = parent,
                    IsExternal = isExternal,
                };
                if (classDeclaration.Declarations != null)
                {
                    foreach (var subDeclaration in classDeclaration.Declarations)
                        ScanStatement(subDeclaration, symbol, isExternal);
                }
                return symbol;
            }
            else if (declaration is EnumDeclaration enumDeclaration)
            {
                var symbol = new EnumSymbol(enumDeclaration)
                {
                    Name = enumDeclaration.Identifier.Text,
                    DeclaringSymbol = parent,
                    IsExternal = isExternal,
                };
                var lastValue = 0;
                foreach (var enumValueDeclaration in enumDeclaration.Values)
                {
                    var value = (enumValueDeclaration.Value as IntLiteral)?.Value ?? lastValue + 1;
                    lastValue = value;

                    var valueSymbol = new EnumValueSymbol(enumValueDeclaration)
                    {
                        Name = enumValueDeclaration.Identifier.Text,
                        Value = value,
                        DeclaringSymbol = symbol,
                        IsExternal = isExternal,
                    };
                }
                return symbol;
            }
            else if (declaration is ObjectDeclaration)
            {
                // ObjectDeclaration is a data-only construct for property values,
                // it doesn't create a symbol in the symbol tree
                return null;
            }

            throw new NotImplementedException();
        }

        var packageImports = new List<(string PackagePath, List<Declaration> Declarations)>();
        foreach (var decl in compilationUnit.Declarations)
        {
            // Handle "from ... import { ... }" syntax (PackageDeclaration)
            if (decl is PackageDeclaration packageDecl)
            {
                var pkgPath = packageDecl.Identifier.Text;
                var importedDeclarations = packageImports.Where(x => x.PackagePath == pkgPath).FirstOrDefault().Declarations;
                if (importedDeclarations != null)
                    importedDeclarations.AddRange(packageDecl.Declarations);
                else
                    packageImports.Add((pkgPath, new(packageDecl.Declarations)));
                continue;
            }

            // Handle legacy [Import("path")] attribute syntax
            var importAttrib = decl.Attributes.FirstOrDefault(x => IsPackageImportAttribute(x));
            if (importAttrib != null)
            {
                if (importAttrib.Arguments.Count != 1)
                    throw new KismetScript.Compiler.Compiler.Exceptions.UnexpectedSyntaxError(importAttrib);
                var packagePath = importAttrib.Arguments[0].Expression as StringLiteral;
                if (packagePath == null)
                    throw new KismetScript.Compiler.Compiler.Exceptions.UnexpectedSyntaxError(importAttrib.Arguments[0]);
                var importedDeclarations = packageImports.Where(x => x.PackagePath == packagePath).FirstOrDefault().Declarations;
                if (importedDeclarations != null)
                    importedDeclarations.Add(decl);
                else
                    packageImports.Add((packagePath, new() { decl }));
            }
        }

        foreach ((var packagePath, var declarations) in packageImports)
        {
            var packageSymbol = new PackageSymbol()
            {
                IsExternal = true,
                Name = packagePath,
                DeclaringSymbol = null,
            };
            foreach (var item in declarations)
                CreateDeclarationSymbol(item, packageSymbol, true);

            IEnumerable<Symbol> GetImportGlobalSymbols(Symbol symbol)
            {
                yield return symbol;
                foreach (var item in symbol.Members)
                {
                    if (item.DeclaringClass != null)
                    {
                        continue;
                    }
                    foreach (var sub in GetImportGlobalSymbols(item))
                        yield return sub;
                }
            }

            var globalSymbols = GetImportGlobalSymbols(packageSymbol);
            var distinctGlobalSymbols = globalSymbols.DistinctBy(x => x.Key);
            foreach (var symbol in distinctGlobalSymbols)
                rootScope.DeclareSymbol(symbol);
        }

        foreach (var declaration in compilationUnit.Declarations
            .Where(x => x is not PackageDeclaration)
            .Except(packageImports.SelectMany(x => x.Declarations)))
        {
            var declarationSymbol = CreateDeclarationSymbol(declaration, null, false);
            if (declarationSymbol == null)
                continue; // Skip ObjectDeclaration and other data-only constructs

            rootScope.DeclareSymbol(declarationSymbol);

            void DeclareUbergraphFunctionGlobalSymbols(Symbol symbol)
            {
                foreach (var item in symbol.Members)
                {
                    if (ShouldGloballyDeclareProcecureLocalSymbol(item))
                    {
                        rootScope.DeclareSymbol(item);
                    }
                    DeclareUbergraphFunctionGlobalSymbols(item);
                }
            }

            DeclareUbergraphFunctionGlobalSymbols(declarationSymbol);
        }

        void ResolveSymbolReferences(Symbol symbol)
        {
            if (symbol is ClassSymbol classSymbol)
            {
                var baseClass = classSymbol.Declaration.BaseClassIdentifier;
                if (baseClass != null && classSymbol.BaseClass == null)
                {
                    var baseClassSymbol = rootScope.GetSymbol(baseClass.Text);
                    if (baseClassSymbol != null && baseClassSymbol != classSymbol)
                    {
                        classSymbol.BaseSymbol = baseClassSymbol;
                    }
                    else
                    {
                        classSymbol.BaseSymbol = new ClassSymbol(null)
                        {
                            DeclaringSymbol = null,
                            IsExternal = true,
                            Name = baseClass.Text,
                        };
                    }
                }
            }
            else if (symbol is VariableSymbol variableSymbol)
            {
                var type = variableSymbol.Declaration.Type;
                if (type.IsConstructedType)
                    type = type.TypeParameter;
                variableSymbol.InnerSymbol = rootScope.GetSymbol(type.Text);
            }

            foreach (var member in symbol.Members)
            {
                ResolveSymbolReferences(member);
            }
        }

        foreach (var item in rootScope)
        {
            ResolveSymbolReferences(item);
        }
    }

    private static bool IsUbergraphFunction(ProcedureSymbol procedureSymbol)
    {
        return procedureSymbol.HasAnyFunctionFlags(UAssetAPI.UnrealTypes.EFunctionFlags.FUNC_UbergraphFunction)
            || procedureSymbol.Name.StartsWith("ExecuteUbergraph_");
    }

    private static bool ShouldGloballyDeclareProcecureLocalSymbol(Symbol item)
    {
        var isUbergraphFunction = item.DeclaringProcedure == null ? false : IsUbergraphFunction(item.DeclaringProcedure);
        var isK2NodeVariable = item.Name.StartsWith("K2Node") && item.SymbolCategory == SymbolCategory.Variable;
        var isLabel = item.SymbolCategory == SymbolCategory.Label;
        var shouldDeclareSymbol = isUbergraphFunction && (isK2NodeVariable || isLabel);
        return shouldDeclareSymbol;
    }

    private static bool IsPackageImportAttribute(AttributeDeclaration attribute)
    {
        return attribute.Identifier.Text == "Import";
    }

    private static UAssetAPI.UnrealTypes.EFunctionFlags GetFunctionFlags(ProcedureDeclaration procedureDeclaration)
    {
        var functionFlags = (UAssetAPI.UnrealTypes.EFunctionFlags)0;
        if (procedureDeclaration.Modifiers.HasFlag(ProcedureModifier.Public))
            functionFlags |= UAssetAPI.UnrealTypes.EFunctionFlags.FUNC_Public;
        if (procedureDeclaration.Modifiers.HasFlag(ProcedureModifier.Private))
            functionFlags |= UAssetAPI.UnrealTypes.EFunctionFlags.FUNC_Private;
        if (procedureDeclaration.Modifiers.HasFlag(ProcedureModifier.Sealed))
            functionFlags |= UAssetAPI.UnrealTypes.EFunctionFlags.FUNC_Final;
        if (procedureDeclaration.Modifiers.HasFlag(ProcedureModifier.Protected))
            functionFlags |= UAssetAPI.UnrealTypes.EFunctionFlags.FUNC_Protected;
        if (procedureDeclaration.Modifiers.HasFlag(ProcedureModifier.Static))
            functionFlags |= UAssetAPI.UnrealTypes.EFunctionFlags.FUNC_Static;

        foreach (var attr in procedureDeclaration.Attributes)
        {
            var flagFormat = $"FUNC_{attr.Identifier.Text}";
            if (!System.Enum.TryParse<UAssetAPI.UnrealTypes.EFunctionFlags>(flagFormat, out var flag))
                continue;
            functionFlags |= flag;
        }
        return functionFlags;
    }

    private static KismetScript.Compiler.Compiler.FunctionCustomFlags GetCustomFunctionFlags(ProcedureDeclaration procedureDeclaration)
    {
        var functionFlags = (KismetScript.Compiler.Compiler.FunctionCustomFlags)0;
        foreach (var attr in procedureDeclaration.Attributes)
        {
            if (!System.Enum.TryParse<KismetScript.Compiler.Compiler.FunctionCustomFlags>(attr.Identifier.Text, out var flag))
                continue;
            functionFlags |= flag;
        }
        return functionFlags;
    }
}
