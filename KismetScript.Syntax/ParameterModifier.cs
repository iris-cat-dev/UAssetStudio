namespace KismetScript.Syntax;

[Flags]
public enum ParameterModifier
{
    None = 0,
    Out = 1 << 1,
    Ref = 1 << 2,
    Const = 1 << 3
}
