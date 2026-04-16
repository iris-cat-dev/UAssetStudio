using System;
using System.CommandLine;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Validation;

namespace UAssetStudio.Cli.CMD
{
    /// <summary>
    /// 验证 UAsset 文件命令
    /// </summary>
    public static class ValidateCommand
    {
        public static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings)
        {
            var command = new Command("validate", "验证 UAsset 文件的结构完整性");

            var assetPathArg = new Argument<string>("asset", "要验证的 .uasset 文件路径");
            var gameContentPathOption = new Option<string?>(
                new[] { "--game-content", "-g" },
                () => null,
                "游戏 Content 目录路径，用于验证导入资产是否存在");

            command.AddArgument(assetPathArg);
            command.AddOption(gameContentPathOption);
            command.AddOption(ueVersion);
            command.AddOption(mappings);

            command.SetHandler((EngineVersion ver, string? mapPath, string assetPath, string? gameContentPath) =>
            {
                try
                {
                    if (!File.Exists(assetPath))
                    {
                        Console.WriteLine($"错误: 文件不存在: {assetPath}");
                        return;
                    }

                    Console.WriteLine($"验证资产: {assetPath}");
                    if (!string.IsNullOrEmpty(gameContentPath))
                    {
                        Console.WriteLine($"游戏目录: {gameContentPath}");
                    }
                    Console.WriteLine();

                    // 加载资产
                    UAsset asset;
                    try
                    {
                        asset = CliHelpers.LoadAsset(ver, mapPath, assetPath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ 加载资产失败: {ex.Message}");
                        return;
                    }

                    // 执行验证
                    var validator = new UAssetValidator(gameContentPath);
                    var result = validator.Validate(asset);

                    // 打印结果
                    ValidationExtensions.PrintResult(result);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"验证过程中发生错误: {ex.Message}");
                }
            }, ueVersion, mappings, assetPathArg, gameContentPathOption);

            return command;
        }
    }
}
