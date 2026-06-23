using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Identifiers;

namespace KismetScript.Syntax.Statements.Declarations;

/// <summary>
/// Represents an object declaration for sub-objects with property assignments.
/// Syntax: object Name : ClassName { Type PropName = Value; ... }
/// </summary>
public class ObjectDeclaration : Declaration
{
    public ObjectDeclaration() : this(DeclarationType.Object)
    {
    }

    protected ObjectDeclaration(DeclarationType type) : base(type)
    {
    }

    /// <summary>
    /// The class/type of this object instance.
    /// </summary>
    public Identifier ClassIdentifier { get; set; } = null!;

    /// <summary>
    /// Property assignments within the object body.
    /// </summary>
    public List<ObjectPropertyAssignment> Properties { get; init; } = new();
}

/// <summary>
/// Represents a property assignment within an object declaration.
/// Syntax: Type Name = Value;
/// </summary>
public class ObjectPropertyAssignment : SyntaxNode
{
    /// <summary>
    /// The type of the property.
    /// </summary>
    public TypeIdentifier Type { get; set; } = null!;

    /// <summary>
    /// The name of the property.
    /// </summary>
    public Identifier Name { get; set; } = null!;

    /// <summary>
    /// The value expression assigned to the property.
    /// </summary>
    public Expression Value { get; set; } = null!;

    public override string ToString()
    {
        return $"{Type} {Name} = {Value};";
    }
}
