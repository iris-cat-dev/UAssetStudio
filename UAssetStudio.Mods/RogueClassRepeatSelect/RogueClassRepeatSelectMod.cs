using UAssetStudio.Patching;

namespace UAssetStudio.Mods.RogueClassRepeatSelect
{
    /// <summary>
    /// RogueCore: allow players to repeatedly pick the same class.
    /// Surgically replaces only ITM_Wardrobe_ClassSelector::CanSwitchToCharacter,
    /// leaving every other function (incl. SetHoverProgress, which previously crashed
    /// when the whole file was recompiled) byte-identical.
    /// </summary>
    public sealed class RogueClassRepeatSelectMod : IAssetMod
    {
        private const string RelPath =
            "Content/UI/Menus/Menu_Character_Customization/CardSelector/ITM_Wardrobe_ClassSelector.uasset";

        public string Name => "rogue-class-repeat-select";

        public string Description =>
            "Allow repeated class selection by surgically patching ITM_Wardrobe_ClassSelector::CanSwitchToCharacter.";

        public string Directory => "RogueClassRepeatSelect";

        public void Build(PatchContext ctx)
        {
            AssetPatchSession
                .Load(ctx.Asset(RelPath), ctx.UeVersion, ctx.Mappings)
                .ReplaceFunctionBytecode(ctx.Local("ITM_Wardrobe_ClassSelector.kms"), "CanSwitchToCharacter")
                .Save(ctx.Output(RelPath));
        }
    }
}
